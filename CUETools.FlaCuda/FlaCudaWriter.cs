using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using GASS.CUDA;
using GASS.CUDA.Types;

namespace CUETools.Codecs.FlaCuda
{
	public class FlaCudaWriter : IAudioDest
	{
		Stream _IO = null;
		string _path;
		long _position;

		// number of audio channels
		// valid values are 1 to 8
		int channels, ch_code;

		// audio sample rate in Hz
		int sample_rate, sr_code0, sr_code1;

		// sample size in bits
		// only 16-bit is currently supported
		uint bits_per_sample;
		int bps_code;

		// total stream samples
		// if 0, stream length is unknown
		int sample_count;

		FlakeEncodeParams eparams;

		// maximum frame size in bytes
		// this can be used to allocate memory for output
		int max_frame_size;

		byte[] frame_buffer = null;

		int frame_count = 0;

		long first_frame_offset = 0;

		TimeSpan _userProcessorTime;

		// header bytes
		// allocated by flake_encode_init and freed by flake_encode_close
		byte[] header;

		int[] verifyBuffer;
		int[] residualBuffer;
		float[] windowBuffer;
		int samplesInBuffer = 0;

		int _compressionLevel = 7;
		int _blocksize = 0;
		int _totalSize = 0;
		int _windowsize = 0, _windowcount = 0;

		Crc8 crc8;
		Crc16 crc16;
		MD5 md5;

		FlacFrame frame;
		FlakeReader verify;

		SeekPoint[] seek_table;
		int seek_table_offset = -1;

		bool inited = false;

		CUDA cuda;
		CUfunction cudaComputeAutocor;
		CUfunction cudaEncodeResidual;
		CUdeviceptr cudaSamples;
		CUdeviceptr cudaWindow;
		CUdeviceptr cudaAutocor;
		CUdeviceptr cudaResidualTasks;
		CUdeviceptr cudaResidualOutput;
		CUdeviceptr cudaCoeffs;
		IntPtr samplesBufferPtr = IntPtr.Zero;
		IntPtr autocorBufferPtr = IntPtr.Zero;
		IntPtr residualOutputPtr = IntPtr.Zero;
		IntPtr lpcCoeffBufferPtr = IntPtr.Zero;
		IntPtr residualTasksPtr = IntPtr.Zero;
		CUstream cudaStream;

		const int MAX_BLOCKSIZE = 8192;
		const int maxResidualTasks = MAX_BLOCKSIZE / (256 - 32);

		public FlaCudaWriter(string path, int bitsPerSample, int channelCount, int sampleRate, Stream IO)
		{
			if (bitsPerSample != 16)
				throw new Exception("Bits per sample must be 16.");
			if (channelCount != 2)
				throw new Exception("ChannelCount must be 2.");

			channels = channelCount;
			sample_rate = sampleRate;
			bits_per_sample = (uint) bitsPerSample;

			// flake_validate_params

			_path = path;
			_IO = IO;

			residualBuffer = new int[FlaCudaWriter.MAX_BLOCKSIZE * (channels == 2 ? 10 : channels + 1)];
			windowBuffer = new float[FlaCudaWriter.MAX_BLOCKSIZE * 2 * lpc.MAX_LPC_WINDOWS];

			eparams.flake_set_defaults(_compressionLevel);
			eparams.padding_size = 8192;

			crc8 = new Crc8();
			crc16 = new Crc16();
			frame = new FlacFrame(channels * 2);
		}

		public int TotalSize
		{
			get
			{
				return _totalSize;
			}
		}

		public int PaddingLength
		{
			get
			{
				return eparams.padding_size;
			}
			set
			{
				eparams.padding_size = value;
			}
		}

		public int CompressionLevel
		{
			get
			{
				return _compressionLevel;
			}
			set
			{
				if (value < 0 || value > 11)
					throw new Exception("unsupported compression level");
				_compressionLevel = value;
				eparams.flake_set_defaults(_compressionLevel);
			}
		}

		//[DllImport("kernel32.dll")]
		//static extern bool GetThreadTimes(IntPtr hThread, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);
		//[DllImport("kernel32.dll")]
		//static extern IntPtr GetCurrentThread();

		void DoClose()
		{
			if (inited)
			{
				while (samplesInBuffer > 0)
				{
					eparams.block_size = samplesInBuffer;
					output_frame();
				}

				if (_IO.CanSeek)
				{
					if (md5 != null)
					{
						md5.TransformFinalBlock(frame_buffer, 0, 0);
						_IO.Position = 26;
						_IO.Write(md5.Hash, 0, md5.Hash.Length);
					}

					if (seek_table != null)
					{
						_IO.Position = seek_table_offset;
						int len = write_seekpoints(header, 0, 0);
						_IO.Write(header, 4, len - 4);
					}
				}
				_IO.Close();

				cuda.Free(cudaWindow);
				cuda.Free(cudaSamples);
				cuda.Free(cudaAutocor);
				cuda.Free(cudaCoeffs);
				cuda.Free(cudaResidualTasks);
				cuda.Free(cudaResidualOutput);
				CUDADriver.cuMemFreeHost(autocorBufferPtr);
				CUDADriver.cuMemFreeHost(residualOutputPtr);
				CUDADriver.cuMemFreeHost(samplesBufferPtr);
				CUDADriver.cuMemFreeHost(lpcCoeffBufferPtr);
				CUDADriver.cuMemFreeHost(residualTasksPtr);
				cuda.DestroyStream(cudaStream);
				cuda.Dispose();
				inited = false;
			}

			//long fake, KernelStart, UserStart;
			//GetThreadTimes(GetCurrentThread(), out fake, out fake, out KernelStart, out UserStart);
			//_userProcessorTime = new TimeSpan(UserStart);
		}

		public void Close()
		{
			DoClose();
			if (sample_count != 0 && _position != sample_count)
				throw new Exception("Samples written differs from the expected sample count.");
		}

		public void Delete()
		{
			if (inited)
			{
				_IO.Close();
				cuda.Free(cudaWindow);
				cuda.Free(cudaSamples);
				cuda.Free(cudaAutocor);
				cuda.Free(cudaCoeffs);
				cuda.Free(cudaResidualTasks);
				cuda.Free(cudaResidualOutput);
				CUDADriver.cuMemFreeHost(autocorBufferPtr);
				CUDADriver.cuMemFreeHost(residualOutputPtr);
				CUDADriver.cuMemFreeHost(samplesBufferPtr);
				CUDADriver.cuMemFreeHost(lpcCoeffBufferPtr);
				CUDADriver.cuMemFreeHost(residualTasksPtr);
				cuda.DestroyStream(cudaStream);
				cuda.Dispose();
				inited = false;
			}

			if (_path != "")
				File.Delete(_path);
		}

		public long Position
		{
			get
			{
				return _position;
			}
		}

		public long FinalSampleCount
		{
			set { sample_count = (int)value; }
		}

		public long BlockSize
		{
			set { _blocksize = (int)value; }
			get { return _blocksize == 0 ? eparams.block_size : _blocksize; }
		}

		public OrderMethod OrderMethod
		{
			get { return eparams.order_method; }
			set { eparams.order_method = value; }
		}

		public PredictionType PredictionType
		{
			get { return eparams.prediction_type; }
			set { eparams.prediction_type = value; }
		}

		public StereoMethod StereoMethod
		{
			get { return eparams.stereo_method; }
			set { eparams.stereo_method = value; }
		}

		public int MinPrecisionSearch
		{
			get { return eparams.lpc_min_precision_search; }
			set
			{
				if (value < 0 || value > eparams.lpc_max_precision_search)
					throw new Exception("unsupported MinPrecisionSearch value");
				eparams.lpc_min_precision_search = value;
			}
		}

		public int MaxPrecisionSearch
		{
			get { return eparams.lpc_max_precision_search; }
			set
			{
				if (value < eparams.lpc_min_precision_search || value >= lpc.MAX_LPC_PRECISIONS)
					throw new Exception("unsupported MaxPrecisionSearch value");
				eparams.lpc_max_precision_search = value;
			}
		}

		public WindowFunction WindowFunction
		{
			get { return eparams.window_function; }
			set { eparams.window_function = value; }
		}

		public bool DoMD5
		{
			get { return eparams.do_md5; }
			set { eparams.do_md5 = value; }
		}

		public bool DoVerify
		{
			get { return eparams.do_verify; }
			set { eparams.do_verify = value; }
		}

		public bool DoSeekTable
		{
			get { return eparams.do_seektable; }
			set { eparams.do_seektable = value; }
		}

		public int VBRMode
		{
			get { return eparams.variable_block_size; }
			set { eparams.variable_block_size = value; }
		}

		public int MinPredictionOrder
		{
			get
			{
				return PredictionType == PredictionType.Fixed ?
					MinFixedOrder : MinLPCOrder;
			}
			set
			{
				if (PredictionType == PredictionType.Fixed)
					MinFixedOrder = value;
				else
					MinLPCOrder = value;
			}
		}

		public int MaxPredictionOrder
		{
			get 
			{
				return PredictionType == PredictionType.Fixed ?
					MaxFixedOrder : MaxLPCOrder; 
			}
			set
			{
				if (PredictionType == PredictionType.Fixed)
					MaxFixedOrder = value;
				else
					MaxLPCOrder = value;
			}
		}

		public int MinLPCOrder
		{
			get
			{
				return eparams.min_prediction_order;
			}
			set
			{
				if (value < 1 || value > eparams.max_prediction_order)
					throw new Exception("invalid MinLPCOrder " + value.ToString());
				eparams.min_prediction_order = value;
			}
		}

		public int MaxLPCOrder
		{
			get
			{
				return eparams.max_prediction_order;
			}
			set
			{
				if (value > lpc.MAX_LPC_ORDER || value < eparams.min_prediction_order)
					throw new Exception("invalid MaxLPCOrder " + value.ToString());
				eparams.max_prediction_order = value;
			}
		}

		public int EstimationDepth
		{
			get
			{
				return eparams.estimation_depth;
			}
			set
			{
				if (value > 32 || value < 1)
					throw new Exception("invalid estimation_depth " + value.ToString());
				eparams.estimation_depth = value;
			}
		}

		public int MinFixedOrder
		{
			get
			{
				return eparams.min_fixed_order;
			}
			set
			{
				if (value < 0 || value > eparams.max_fixed_order)
					throw new Exception("invalid MinFixedOrder " + value.ToString());
				eparams.min_fixed_order = value;
			}
		}

		public int MaxFixedOrder
		{
			get
			{
				return eparams.max_fixed_order;
			}
			set
			{
				if (value > 4 || value < eparams.min_fixed_order)
					throw new Exception("invalid MaxFixedOrder " + value.ToString());
				eparams.max_fixed_order = value;
			}
		}

		public int MinPartitionOrder
		{
			get { return eparams.min_partition_order; }
			set
			{
				if (value < 0 || value > eparams.max_partition_order)
					throw new Exception("invalid MinPartitionOrder " + value.ToString());
				eparams.min_partition_order = value;
			}
		}

		public int MaxPartitionOrder
		{
			get { return eparams.max_partition_order; }
			set
			{
				if (value > 8 || value < eparams.min_partition_order)
					throw new Exception("invalid MaxPartitionOrder " + value.ToString());
				eparams.max_partition_order = value;
			}
		}

		public TimeSpan UserProcessorTime
		{
			get { return _userProcessorTime; }
		}

		public int BitsPerSample
		{
			get { return 16; }
		}

		unsafe uint get_wasted_bits(int* signal, int samples)
		{
			int i, shift;
			int x = 0;

			for (i = 0; i < samples && 0 == (x & 1); i++)
				x |= signal[i];

			if (x == 0)
			{
				shift = 0;
			}
			else
			{
				for (shift = 0; 0 == (x & 1); shift++)
					x >>= 1;
			}

			if (shift > 0)
			{
				for (i = 0; i < samples; i++)
					signal[i] >>= shift;
			}

			return (uint)shift;
		}

		/// <summary>
		/// Copy channel-interleaved input samples into separate subframes
		/// </summary>
		/// <param name="samples"></param>
		/// <param name="pos"></param>
		/// <param name="block"></param>
 		unsafe void copy_samples(int[,] samples, int pos, int block)
		{
			int* fsamples = (int*)samplesBufferPtr;
			fixed (int *src = &samples[pos, 0])
			{
				if (channels == 2)
					AudioSamples.Deinterlace(fsamples + samplesInBuffer, fsamples + FlaCudaWriter.MAX_BLOCKSIZE + samplesInBuffer, src, block);
				else
					for (int ch = 0; ch < channels; ch++)
					{
						int* psamples = fsamples + ch * FlaCudaWriter.MAX_BLOCKSIZE + samplesInBuffer;
						for (int i = 0; i < block; i++)
							psamples[i] = src[i * channels + ch];
					}
			}
			samplesInBuffer += block;
		}

		static uint rice_encode_count(uint sum, uint n, uint k)
		{
			return n*(k+1) + ((sum-(n>>1))>>(int)k);
		}

		//static unsafe uint find_optimal_rice_param(uint sum, uint n)
		//{
		//    uint* nbits = stackalloc uint[Flake.MAX_RICE_PARAM + 1];
		//    int k_opt = 0;

		//    nbits[0] = UINT32_MAX;
		//    for (int k = 0; k <= Flake.MAX_RICE_PARAM; k++)
		//    {
		//        nbits[k] = rice_encode_count(sum, n, (uint)k);
		//        if (nbits[k] < nbits[k_opt])
		//            k_opt = k;
		//    }
		//    return (uint)k_opt;
		//}

		static unsafe int find_optimal_rice_param(uint sum, uint n, out uint nbits_best)
		{
			int k_opt = 0;
			uint a = n;
			uint b = sum - (n >> 1);
			uint nbits = a + b;
			for (int k = 1; k <= Flake.MAX_RICE_PARAM; k++)
			{
				a += n;
				b >>= 1;
				uint nbits_k = a + b;
				if (nbits_k < nbits)
				{
					k_opt = k;
					nbits = nbits_k;
				}
			}
			nbits_best = nbits;
			return k_opt;
		}

		unsafe uint calc_decorr_score(FlacFrame frame, int ch)
		{
			int* s = frame.subframes[ch].samples;
			int n = frame.blocksize;
			ulong sum = 0;
			for (int i = 2; i < n; i++)
				sum += (ulong)Math.Abs(s[i] - 2 * s[i - 1] + s[i - 2]);
			uint nbits;
			find_optimal_rice_param((uint)(2 * sum), (uint)n, out nbits);
			return nbits;
		}

		unsafe static void channel_decorrelation(int* leftS, int* rightS, int *leftM, int *rightM, int blocksize)
		{
			for (int i = 0; i < blocksize; i++)
			{
				leftM[i] = (leftS[i] + rightS[i]) >> 1;
				rightM[i] = leftS[i] - rightS[i];
			}
		}

		unsafe void encode_residual_verbatim(int* res, int* smp, uint n)
		{
			AudioSamples.MemCpy(res, smp, (int) n);
		}

		unsafe void encode_residual_fixed(int* res, int* smp, int n, int order)
		{
			int i;
			int s0, s1, s2;
			switch (order)
			{
				case 0:
					AudioSamples.MemCpy(res, smp, n);
					return;
				case 1:
					*(res++) = s1 = *(smp++);
					for (i = n - 1; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - s1;
						s1 = s0;
					}
					return;
				case 2:
					*(res++) = s2 = *(smp++);
					*(res++) = s1 = *(smp++);
					for (i = n - 2; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - 2 * s1 + s2;
						s2 = s1;
						s1 = s0;
					}
					return;
				case 3:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					for (i = 3; i < n; i++)
					{
						res[i] = smp[i] - 3 * smp[i - 1] + 3 * smp[i - 2] - smp[i - 3];
					}
					return;
				case 4:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					res[3] = smp[3];
					for (i = 4; i < n; i++)
					{
						res[i] = smp[i] - 4 * smp[i - 1] + 6 * smp[i - 2] - 4 * smp[i - 3] + smp[i - 4];
					}
					return;
				default:
					return;
			}
		}

		static unsafe uint calc_optimal_rice_params(ref RiceContext rc, int porder, uint* sums, uint n, uint pred_order)
		{
			uint part = (1U << porder);
			uint all_bits = 0;			
			rc.rparams[0] = find_optimal_rice_param(sums[0], (n >> porder) - pred_order, out all_bits);
			uint cnt = (n >> porder);
			for (uint i = 1; i < part; i++)
			{
				uint nbits;
				rc.rparams[i] = find_optimal_rice_param(sums[i], cnt, out nbits);
				all_bits += nbits;
			}
			all_bits += (4 * part);
			rc.porder = porder;
			return all_bits;
		}

		static unsafe void calc_sums(int pmin, int pmax, uint* data, uint n, uint pred_order, uint* sums)
		{
			// sums for highest level
			int parts = (1 << pmax);
			uint* res = data + pred_order;
			uint cnt = (n >> pmax) - pred_order;
			uint sum = 0;
			for (uint j = cnt; j > 0; j--)
				sum += *(res++);
			sums[pmax * Flake.MAX_PARTITIONS + 0] = sum;
			cnt = (n >> pmax);
			for (int i = 1; i < parts; i++)
			{
				sum = 0;
				for (uint j = cnt; j > 0; j--)
					sum += *(res++);
				sums[pmax * Flake.MAX_PARTITIONS + i] = sum;
			}
			// sums for lower levels
			for (int i = pmax - 1; i >= pmin; i--)
			{
				parts = (1 << i);
				for (int j = 0; j < parts; j++)
				{
					sums[i * Flake.MAX_PARTITIONS + j] = 
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j] + 
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j + 1];
				}
			}
		}

		static unsafe uint calc_rice_params(ref RiceContext rc, int pmin, int pmax, int* data, uint n, uint pred_order)
		{
			RiceContext tmp_rc = new RiceContext(), tmp_rc2;
			uint* udata = stackalloc uint[(int)n];
			uint* sums = stackalloc uint[(pmax + 1) * Flake.MAX_PARTITIONS];
			//uint* bits = stackalloc uint[Flake.MAX_PARTITION_ORDER];

			//assert(pmin >= 0 && pmin <= Flake.MAX_PARTITION_ORDER);
			//assert(pmax >= 0 && pmax <= Flake.MAX_PARTITION_ORDER);
			//assert(pmin <= pmax);

			for (uint i = 0; i < n; i++)
				udata[i] = (uint) ((2 * data[i]) ^ (data[i] >> 31));

			calc_sums(pmin, pmax, udata, n, pred_order, sums);

			int opt_porder = pmin;
			uint opt_bits = AudioSamples.UINT32_MAX;
			for (int i = pmin; i <= pmax; i++)
			{
				uint bits = calc_optimal_rice_params(ref tmp_rc, i, sums + i * Flake.MAX_PARTITIONS, n, pred_order);
				if (bits <= opt_bits)
				{
					opt_porder = i;
					opt_bits = bits;
					tmp_rc2 = rc;
					rc = tmp_rc;
					tmp_rc = tmp_rc2;
				}
			}

			return opt_bits;
		}

		static int get_max_p_order(int max_porder, int n, int order)
		{
			int porder = Math.Min(max_porder, BitReader.log2i(n ^ (n - 1)));
			if (order > 0)
				porder = Math.Min(porder, BitReader.log2i(n / order));
			return porder;
		}

		static unsafe uint calc_rice_params_fixed(ref RiceContext rc, int pmin, int pmax,
			int* data, int n, int pred_order, uint bps)
		{
			pmin = get_max_p_order(pmin, n, pred_order);
			pmax = get_max_p_order(pmax, n, pred_order);
			uint bits = (uint)pred_order * bps + 6;
			bits += calc_rice_params(ref rc, pmin, pmax, data, (uint)n, (uint)pred_order);
			return bits;
		}

		static unsafe uint calc_rice_params_lpc(ref RiceContext rc, int pmin, int pmax,
			int* data, int n, int pred_order, uint bps, uint precision)
		{
			pmin = get_max_p_order(pmin, n, pred_order);
			pmax = get_max_p_order(pmax, n, pred_order);
			uint bits = (uint)pred_order * bps + 4 + 5 + (uint)pred_order * precision + 6;
			bits += calc_rice_params(ref rc, pmin, pmax, data, (uint)n, (uint)pred_order);
			return bits;
		}

		// select LPC precision based on block size
		static uint get_precision(int blocksize)
		{
			uint lpc_precision;
			if (blocksize <= 192) lpc_precision = 7U;
			else if (blocksize <= 384) lpc_precision = 8U;
			else if (blocksize <= 576) lpc_precision = 9U;
			else if (blocksize <= 1152) lpc_precision = 10U;
			else if (blocksize <= 2304) lpc_precision = 11U;
			else if (blocksize <= 4608) lpc_precision = 12U;
			else if (blocksize <= 8192) lpc_precision = 13U;
			else if (blocksize <= 16384) lpc_precision = 14U;
			else lpc_precision = 15;
			return lpc_precision;
		}

		unsafe void encode_residual_lpc_sub(FlacFrame frame, float * lpcs, int iWindow, int order, int ch)
		{
			// select LPC precision based on block size
			uint lpc_precision = get_precision(frame.blocksize);

			for (int i_precision = eparams.lpc_min_precision_search; i_precision <= eparams.lpc_max_precision_search && lpc_precision + i_precision < 16; i_precision++)
				// check if we already calculated with this order, window and precision
				if ((frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[i_precision] & (1U << (order - 1))) == 0)
				{
					frame.subframes[ch].lpc_ctx[iWindow].done_lpcs[i_precision] |= (1U << (order - 1));

					uint cbits = lpc_precision + (uint)i_precision;

					frame.current.type = SubframeType.LPC;
					frame.current.order = order;
					frame.current.window = iWindow;

					fixed (int* coefs = frame.current.coefs)
					{
						lpc.quantize_lpc_coefs(lpcs + (frame.current.order - 1) * lpc.MAX_LPC_ORDER,
							frame.current.order, cbits, coefs, out frame.current.shift, 15, 0);

						if (frame.current.shift < 0 || frame.current.shift > 15)
							throw new Exception("negative shift");

						ulong csum = 0;
						for (int i = frame.current.order; i > 0; i--)
							csum += (ulong)Math.Abs(coefs[i - 1]);

						if ((csum << (int)frame.subframes[ch].obits) >= 1UL << 32)
							lpc.encode_residual_long(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);
						else
							lpc.encode_residual(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);

					}

					frame.current.size = calc_rice_params_lpc(ref frame.current.rc, eparams.min_partition_order, eparams.max_partition_order,
						frame.current.residual, frame.blocksize, frame.current.order, frame.subframes[ch].obits, cbits);

					frame.ChooseBestSubframe(ch);
				}
		}

		unsafe void encode_residual_lpc_sub(FlacFrame frame, int* coefs, int shift, int iWindow, int order, int ch)
		{
			frame.current.type = SubframeType.LPC;
			frame.current.order = order;
			frame.current.window = iWindow;
			frame.current.shift = shift;
			fixed (int* fcoefs = frame.current.coefs)
				AudioSamples.MemCpy(fcoefs, coefs, order);

			ulong csum = 0;
			int cbits = 1;
			for (int i = frame.current.order; i > 0; i--)
			{
				csum += (ulong)Math.Abs(coefs[i - 1]);
				while (cbits < 16 && coefs[i - 1] != (coefs[i - 1] << (32 - cbits)) >> (32 - cbits))
					cbits++;
			}

			if ((csum << (int)frame.subframes[ch].obits) >= 1UL << 32)
				lpc.encode_residual_long(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);
			else
				lpc.encode_residual(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order, coefs, frame.current.shift);

			frame.current.size = calc_rice_params_lpc(ref frame.current.rc, eparams.min_partition_order, eparams.max_partition_order,
				frame.current.residual, frame.blocksize, frame.current.order, frame.subframes[ch].obits, (uint)cbits);

			frame.ChooseBestSubframe(ch);
		}

		unsafe void encode_residual_fixed_sub(FlacFrame frame, int order, int ch)
		{
			if ((frame.subframes[ch].done_fixed & (1U << order)) != 0)
				return; // already calculated;

			frame.current.order = order;
			frame.current.type = SubframeType.Fixed;

			encode_residual_fixed(frame.current.residual, frame.subframes[ch].samples, frame.blocksize, frame.current.order);

			frame.current.size = calc_rice_params_fixed(ref frame.current.rc, eparams.min_partition_order, eparams.max_partition_order,
				frame.current.residual, frame.blocksize, frame.current.order, frame.subframes[ch].obits);

			frame.subframes[ch].done_fixed |= (1U << order);

			frame.ChooseBestSubframe(ch);
		}

		unsafe void encode_residual(FlacFrame frame, int ch, PredictionType predict, OrderMethod omethod, int pass)
		{
			int* smp = frame.subframes[ch].samples;
			int i, n = frame.blocksize;
			// save best.window, because we can overwrite it later with fixed frame
			int best_window = frame.subframes[ch].best.type == SubframeType.LPC ? frame.subframes[ch].best.window : -1;

			// CONSTANT
			for (i = 1; i < n; i++)
			{
				if (smp[i] != smp[0]) break;
			}
			if (i == n)
			{
				frame.subframes[ch].best.type = SubframeType.Constant;
				frame.subframes[ch].best.residual[0] = smp[0];
				frame.subframes[ch].best.size = frame.subframes[ch].obits;
				return;
			}

			// VERBATIM
			frame.current.type = SubframeType.Verbatim;
			frame.current.size = frame.subframes[ch].obits * (uint)frame.blocksize;
			frame.ChooseBestSubframe(ch);

			if (n < 5 || predict == PredictionType.None)
				return;

			// FIXED
			if (predict == PredictionType.Fixed ||
				(predict == PredictionType.Search && pass != 1) ||
				//predict == PredictionType.Search ||
				//(pass == 2 && frame.subframes[ch].best.type == SubframeType.Fixed) ||
				n <= eparams.max_prediction_order)
			{
				int max_fixed_order = Math.Min(eparams.max_fixed_order, 4);
				int min_fixed_order = Math.Min(eparams.min_fixed_order, max_fixed_order);

				for (i = min_fixed_order; i <= max_fixed_order; i++)
					encode_residual_fixed_sub(frame, i, ch);
			}

			// LPC
			if (n > eparams.max_prediction_order &&
			   (predict == PredictionType.Levinson ||
				predict == PredictionType.Search)
				//predict == PredictionType.Search ||
				//(pass == 2 && frame.subframes[ch].best.type == SubframeType.LPC))
				)
			{
				float* lpcs = stackalloc float[lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER];
				int min_order = eparams.min_prediction_order;
				int max_order = eparams.max_prediction_order;

				for (int iWindow = 0; iWindow < _windowcount; iWindow++)
				{
					if (pass == 2 && iWindow != best_window)
						continue;

					LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[iWindow];
			
					lpc_ctx.GetReflection(max_order, smp, n, frame.window_buffer + iWindow * FlaCudaWriter.MAX_BLOCKSIZE * 2);
					lpc_ctx.ComputeLPC(lpcs);

					switch (omethod)
					{
						case OrderMethod.Max:
							// always use maximum order
							encode_residual_lpc_sub(frame, lpcs, iWindow, max_order, ch);
							break;
						case OrderMethod.Estimate:
							// estimated orders
							// Search at reflection coeff thresholds (where they cross 0.10)
							{
								int found = 0;
								for (i = max_order; i >= min_order && found < eparams.estimation_depth; i--)
									if (lpc_ctx.IsInterestingOrder(i, max_order))
									{
										encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
										found++;
									}
								if (0 == found)
									encode_residual_lpc_sub(frame, lpcs, iWindow, min_order, ch);
							}
							break;
						case OrderMethod.EstSearch2:
							// Search at reflection coeff thresholds (where they cross 0.10)
							{
								int found = 0;
								for (i = min_order; i <= max_order && found < eparams.estimation_depth; i++)
									if (lpc_ctx.IsInterestingOrder(i))
									{
										encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
										found++;
									}
								if (0 == found)
									encode_residual_lpc_sub(frame, lpcs, iWindow, min_order, ch);
							}
							break;
						case OrderMethod.Search:
							// brute-force optimal order search
							for (i = max_order; i >= min_order; i--)
								encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
							break;
						case OrderMethod.LogFast:
							// Try max, est, 32,16,8,4,2,1
							encode_residual_lpc_sub(frame, lpcs, iWindow, max_order, ch);
							for (i = lpc.MAX_LPC_ORDER; i >= min_order; i >>= 1)
								if (i < max_order)
									encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
							break;
						case OrderMethod.LogSearch:
							// do LogFast first
							encode_residual_lpc_sub(frame, lpcs, iWindow, max_order, ch);
							for (i = lpc.MAX_LPC_ORDER; i >= min_order; i >>= 1)
								if (i < max_order)
									encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
							// if found a good order, try to search around it
							if (frame.subframes[ch].best.type == SubframeType.LPC)
							{
								// log search (written by Michael Niedermayer for FFmpeg)
								for (int step = lpc.MAX_LPC_ORDER; step > 0; step >>= 1)
								{
									int last = frame.subframes[ch].best.order;
									if (step <= (last + 1) / 2)
										for (i = last - step; i <= last + step; i += step)
											if (i >= min_order && i <= max_order)
												encode_residual_lpc_sub(frame, lpcs, iWindow, i, ch);
								}
							}
							break;
						default:
							throw new Exception("unknown ordermethod");
					}
				}
			}
		}

		unsafe void encode_selected_residual(FlacFrame frame, int ch, PredictionType predict, OrderMethod omethod, int best_window, int best_order)
		{
			int* smp = frame.subframes[ch].samples;
			int i, n = frame.blocksize;

			// CONSTANT
			for (i = 1; i < n; i++)
			{
				if (smp[i] != smp[0]) break;
			}
			if (i == n)
			{
				frame.subframes[ch].best.type = SubframeType.Constant;
				frame.subframes[ch].best.residual[0] = smp[0];
				frame.subframes[ch].best.size = frame.subframes[ch].obits;
				return;
			}

			// VERBATIM
			frame.current.type = SubframeType.Verbatim;
			frame.current.size = frame.subframes[ch].obits * (uint)frame.blocksize;
			frame.ChooseBestSubframe(ch);

			if (n < 5 || predict == PredictionType.None)
				return;

			// FIXED
			if (predict == PredictionType.Fixed ||
				(predict == PredictionType.Search) ||
				n <= eparams.max_prediction_order)
			{
				int max_fixed_order = Math.Min(eparams.max_fixed_order, 4);
				int min_fixed_order = Math.Min(eparams.min_fixed_order, max_fixed_order);

				for (i = min_fixed_order; i <= max_fixed_order; i++)
					encode_residual_fixed_sub(frame, i, ch);
			}

			// LPC
			if (n > eparams.max_prediction_order &&
			   (predict == PredictionType.Levinson ||
				predict == PredictionType.Search)
				)
			{
				LpcContext lpc_ctx = frame.subframes[ch].lpc_ctx[best_window];
				fixed (int *coefs = lpc_ctx.coefs)
					encode_residual_lpc_sub(frame, coefs, lpc_ctx.shift, best_window, best_order, ch);
			}
		}

		unsafe void output_frame_header(FlacFrame frame, BitWriter bitwriter)
		{
			bitwriter.writebits(15, 0x7FFC);
			bitwriter.writebits(1, eparams.variable_block_size > 0 ? 1 : 0);
			bitwriter.writebits(4, frame.bs_code0);
			bitwriter.writebits(4, sr_code0);
			if (frame.ch_mode == ChannelMode.NotStereo)
				bitwriter.writebits(4, ch_code);
			else
				bitwriter.writebits(4, (int) frame.ch_mode);
			bitwriter.writebits(3, bps_code);
			bitwriter.writebits(1, 0);
			bitwriter.write_utf8(frame_count);

			// custom block size
			if (frame.bs_code1 >= 0)
			{
				if (frame.bs_code1 < 256)
					bitwriter.writebits(8, frame.bs_code1);
				else
					bitwriter.writebits(16, frame.bs_code1);
			}

			// custom sample rate
			if (sr_code1 > 0)
			{
				if (sr_code1 < 256)
					bitwriter.writebits(8, sr_code1);
				else
					bitwriter.writebits(16, sr_code1);
			}

			// CRC-8 of frame header
			bitwriter.flush();
			byte crc = crc8.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.writebits(8, crc);
		}

		unsafe void output_residual(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// rice-encoded block
			bitwriter.writebits(2, 0);

			// partition order
			int porder = sub.best.rc.porder;
			int psize = frame.blocksize >> porder;
			//assert(porder >= 0);
			bitwriter.writebits(4, porder);
			int res_cnt = psize - sub.best.order;

			// residual
			int j = sub.best.order;
			for (int p = 0; p < (1 << porder); p++)
			{
				int k = sub.best.rc.rparams[p];
				bitwriter.writebits(4, k);
				if (p == 1) res_cnt = psize;
				int cnt = Math.Min(res_cnt, frame.blocksize - j);
				bitwriter.write_rice_block_signed(k, sub.best.residual + j, cnt);
				j += cnt;
			}
		}

		unsafe void 
		output_subframe_constant(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			bitwriter.writebits_signed(sub.obits, sub.best.residual[0]);
		}

		unsafe void
		output_subframe_verbatim(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			int n = frame.blocksize;
			for (int i = 0; i < n; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]); 
			// Don't use residual here, because we don't copy samples to residual for verbatim frames.
		}

		unsafe void
		output_subframe_fixed(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]);

			// residual
			output_residual(frame, bitwriter, sub);
		}

		unsafe void
		output_subframe_lpc(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]);

			// LPC coefficients
			int cbits = 1;
			for (int i = 0; i < sub.best.order; i++)
				while (cbits < 16 && sub.best.coefs[i] != (sub.best.coefs[i] << (32 - cbits)) >> (32 - cbits))
					cbits++;
			bitwriter.writebits(4, cbits - 1);
			bitwriter.writebits_signed(5, sub.best.shift);
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(cbits, sub.best.coefs[i]);
			
			// residual
			output_residual(frame, bitwriter, sub);
		}

		unsafe void output_subframes(FlacFrame frame, BitWriter bitwriter)
		{
			for (int ch = 0; ch < channels; ch++)
			{
				FlacSubframeInfo sub = frame.subframes[ch];
				// subframe header
				int type_code = (int) sub.best.type;
				if (sub.best.type == SubframeType.Fixed)
					type_code |= sub.best.order;
				if (sub.best.type == SubframeType.LPC)
					type_code |= sub.best.order - 1;
				bitwriter.writebits(1, 0);
				bitwriter.writebits(6, type_code);
				bitwriter.writebits(1, sub.wbits != 0 ? 1 : 0);
				if (sub.wbits > 0)
					bitwriter.writebits((int)sub.wbits, 1);

				// subframe
				switch (sub.best.type)
				{
					case SubframeType.Constant:
						output_subframe_constant(frame, bitwriter, sub);
						break;
					case SubframeType.Verbatim:
						output_subframe_verbatim(frame, bitwriter, sub);
						break;
					case SubframeType.Fixed:
						output_subframe_fixed(frame, bitwriter, sub);
						break;
					case SubframeType.LPC:
						output_subframe_lpc(frame, bitwriter, sub);
						break;
				}
			}
		}

		void output_frame_footer(BitWriter bitwriter)
		{
			bitwriter.flush();
			ushort crc = crc16.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.writebits(16, crc);
			bitwriter.flush();
		}

		unsafe void encode_residual_pass1(FlacFrame frame, int ch)
		{
			int max_prediction_order = eparams.max_prediction_order;
			int max_fixed_order = eparams.max_fixed_order;
			int min_fixed_order = eparams.min_fixed_order;
			int lpc_min_precision_search = eparams.lpc_min_precision_search;
			int lpc_max_precision_search = eparams.lpc_max_precision_search;
			int max_partition_order = eparams.max_partition_order;
			int estimation_depth = eparams.estimation_depth;
			eparams.min_fixed_order = 2;
			eparams.max_fixed_order = 2;
			eparams.lpc_min_precision_search = eparams.lpc_max_precision_search;
			eparams.max_prediction_order = 8;
			eparams.estimation_depth = 1;
			encode_residual(frame, ch, eparams.prediction_type, OrderMethod.Estimate, 1);
			eparams.min_fixed_order = min_fixed_order;
			eparams.max_fixed_order = max_fixed_order;
			eparams.max_prediction_order = max_prediction_order;
			eparams.lpc_min_precision_search = lpc_min_precision_search;
			eparams.lpc_max_precision_search = lpc_max_precision_search;
			eparams.max_partition_order = max_partition_order;
			eparams.estimation_depth = estimation_depth;
		}

		unsafe void encode_residual_pass2(FlacFrame frame, int ch)
		{
			encode_residual(frame, ch, eparams.prediction_type, eparams.order_method, 2);
		}

		unsafe void encode_residual_onepass(FlacFrame frame, int ch)
		{
			if (_windowcount > 1)
			{
				encode_residual_pass1(frame, ch);
				encode_residual_pass2(frame, ch);
			} else
				encode_residual(frame, ch, eparams.prediction_type, eparams.order_method, 0);
		}

		unsafe uint measure_frame_size(FlacFrame frame, bool do_midside)
		{
			// crude estimation of header/footer size
			uint total = (uint)(32 + ((BitReader.log2i(frame_count) + 4) / 5) * 8 + (eparams.variable_block_size != 0 ? 16 : 0) + 16);

			if (do_midside)
			{
				uint bitsBest = AudioSamples.UINT32_MAX;
				ChannelMode modeBest = ChannelMode.LeftRight;

				if (bitsBest > frame.subframes[2].best.size + frame.subframes[3].best.size)
				{
					bitsBest = frame.subframes[2].best.size + frame.subframes[3].best.size;
					modeBest = ChannelMode.MidSide;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[1].best.size)
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[1].best.size;
					modeBest = ChannelMode.RightSide;
				}
				if (bitsBest > frame.subframes[3].best.size + frame.subframes[0].best.size)
				{
					bitsBest = frame.subframes[3].best.size + frame.subframes[0].best.size;
					modeBest = ChannelMode.LeftSide;
				}
				if (bitsBest > frame.subframes[0].best.size + frame.subframes[1].best.size)
				{
					bitsBest = frame.subframes[0].best.size + frame.subframes[1].best.size;
					modeBest = ChannelMode.LeftRight;
				}
				frame.ch_mode = modeBest;
				return total + bitsBest;
			}

			for (int ch = 0; ch < channels; ch++)
				total += frame.subframes[ch].best.size;
			return total;
		}

		unsafe delegate void window_function(float* window, int size);

		unsafe void calculate_window(float* window, window_function func, WindowFunction flag)
		{
			if ((eparams.window_function & flag) == 0 || _windowcount == lpc.MAX_LPC_WINDOWS)
				return;
			int sz = _windowsize;
			float* pos = window + _windowcount * FlaCudaWriter.MAX_BLOCKSIZE * 2;
			do
			{
				func(pos, sz);
				if ((sz & 1) != 0)
					break;
				pos += sz;
				sz >>= 1;
			} while (sz >= 32);
			_windowcount++;
		}

		unsafe int encode_frame(out int size)
		{
			int* s = (int*)samplesBufferPtr;
			fixed (int* r = residualBuffer)
			fixed (float* window = windowBuffer)
			{
				frame.InitSize(eparams.block_size, eparams.variable_block_size != 0);

				if (frame.blocksize != _windowsize && frame.blocksize > 4)
				{
					_windowsize = frame.blocksize;
					_windowcount = 0;
					calculate_window(window, lpc.window_welch, WindowFunction.Welch);
					calculate_window(window, lpc.window_tukey, WindowFunction.Tukey);
					calculate_window(window, lpc.window_hann, WindowFunction.Hann);
					calculate_window(window, lpc.window_flattop, WindowFunction.Flattop);
					calculate_window(window, lpc.window_bartlett, WindowFunction.Bartlett);
					if (_windowcount == 0)
						throw new Exception("invalid windowfunction");
					cuda.CopyHostToDevice<float>(cudaWindow, windowBuffer);
				}

				if (channels != 2 || frame.blocksize <= 32 || eparams.stereo_method == StereoMethod.Independent)
				{
					frame.window_buffer = window;
					frame.current.residual = r + channels * FlaCudaWriter.MAX_BLOCKSIZE;
					frame.ch_mode = channels != 2 ? ChannelMode.NotStereo : ChannelMode.LeftRight;
					for (int ch = 0; ch < channels; ch++)
						frame.subframes[ch].Init(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * FlaCudaWriter.MAX_BLOCKSIZE,
							bits_per_sample, get_wasted_bits(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, frame.blocksize));

					for (int ch = 0; ch < channels; ch++)
						encode_residual_onepass(frame, ch);
				}
				else
				{
					channel_decorrelation(s, s + FlaCudaWriter.MAX_BLOCKSIZE, s + 2 * FlaCudaWriter.MAX_BLOCKSIZE, s + 3 * FlaCudaWriter.MAX_BLOCKSIZE, frame.blocksize);
					frame.window_buffer = window;
					frame.current.residual = r + 4 * FlaCudaWriter.MAX_BLOCKSIZE;
					for (int ch = 0; ch < 4; ch++)
						frame.subframes[ch].Init(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * FlaCudaWriter.MAX_BLOCKSIZE,
							bits_per_sample + (ch == 3 ? 1U : 0U), get_wasted_bits(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, frame.blocksize));

					int orders = 8;
					while (orders < eparams.max_prediction_order + 1 && orders < 32)
						orders <<= 1;
					int threads = 512;
					int threads_x = orders;
					int threads_y = threads / threads_x;
					int blocks_y = ((threads_x - 1) * (threads_y - 1)) / threads_y;
					int blocks = (frame.blocksize + blocks_y * threads_y - 1) / (blocks_y * threads_y);
					cuda.SetParameter(cudaComputeAutocor, 0, (uint)cudaAutocor.Pointer);
					cuda.SetParameter(cudaComputeAutocor, IntPtr.Size, (uint)cudaSamples.Pointer);
					cuda.SetParameter(cudaComputeAutocor, IntPtr.Size * 2, (uint)cudaWindow.Pointer);
					cuda.SetParameter(cudaComputeAutocor, IntPtr.Size * 3, (uint)frame.blocksize);
					cuda.SetParameter(cudaComputeAutocor, IntPtr.Size * 3 + sizeof(uint), (uint)FlaCudaWriter.MAX_BLOCKSIZE);
					cuda.SetParameter(cudaComputeAutocor, IntPtr.Size * 3 + sizeof(uint) * 2, (uint)(blocks_y * threads_y));
					cuda.SetParameterSize(cudaComputeAutocor, (uint)(IntPtr.Size * 3) + sizeof(uint) * 3);
					cuda.SetFunctionBlockShape(cudaComputeAutocor, threads_x, threads_y, 1);

					int autocorBufferSize = sizeof(float) * (lpc.MAX_LPC_ORDER + 1) * 4 * _windowcount * blocks;

					// create cuda event handles
					//CUevent start = cuda.CreateEvent();
					//CUevent stop = cuda.CreateEvent();

					// asynchronously issue work to the GPU (all to stream 0)
					//cuda.RecordEvent(start, cudaStream);
					cuda.CopyHostToDeviceAsync(cudaSamples, (IntPtr)s, (uint)FlaCudaWriter.MAX_BLOCKSIZE * 4 * sizeof(int), cudaStream);
					cuda.LaunchAsync(cudaComputeAutocor, blocks, 4 * _windowcount, cudaStream);
					cuda.CopyDeviceToHostAsync(cudaAutocor, autocorBufferPtr, (uint)autocorBufferSize, cudaStream);
					//cuda.RecordEvent(stop, cudaStream);
					cuda.SynchronizeStream(cudaStream);

					//int* coefs = stackalloc int[lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER * 4 * _windowcount];
					uint cbits = get_precision(frame.blocksize) + 1;
					AudioSamples.MemSet((int*)lpcCoeffBufferPtr, 0, lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER * 4 * _windowcount);

					int nResidualTasks = 0;
					int partSize = 256 - 32;
					int partCount = (frame.blocksize + partSize - 1) / partSize;
					for (int ch = 0; ch < 4; ch++)
						for (int iWindow = 0; iWindow < _windowcount; iWindow++)
						{
							double* ac = stackalloc double[lpc.MAX_LPC_ORDER + 1];
							for (int i = 0; i < orders; i++)
							{
								ac[i] = 0;
								for (int i_block = 0; i_block < blocks; i_block++)
									//ac[i] += autocorBuffer[orders * (i_block + blocks * (ch + 4 * iWindow)) + i];
									ac[i] += ((float*)autocorBufferPtr)[orders * (i_block + blocks * (ch + 4 * iWindow)) + i];
								
							}
							frame.subframes[ch].lpc_ctx[iWindow].ComputeReflection(orders - 1, ac);
							float* lpcs = stackalloc float[lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER];
							frame.subframes[ch].lpc_ctx[iWindow].ComputeLPC(lpcs);
							for (int order = 0; order < orders - 1; order++)
							{
								int index = order + (orders - 1) * (iWindow + _windowcount * ch);
								int shift;
								lpc.quantize_lpc_coefs(lpcs + order * lpc.MAX_LPC_ORDER,
									order + 1, cbits, ((int*)lpcCoeffBufferPtr) + index * lpc.MAX_LPC_ORDER,
									out shift, 15, 0);
								
								encodeResidualTaskStruct* residualTasks = (encodeResidualTaskStruct*) residualTasksPtr;
								residualTasks[nResidualTasks].residualOrder = order;
								residualTasks[nResidualTasks].shift = shift;
								residualTasks[nResidualTasks].coefsOffs = index * lpc.MAX_LPC_ORDER;
								residualTasks[nResidualTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE;
								nResidualTasks++;
							}
						}

					int max_order = Math.Min(orders - 1, eparams.max_prediction_order);

					cuda.SetParameter(cudaEncodeResidual, 0, (uint)cudaResidualOutput.Pointer);
					cuda.SetParameter(cudaEncodeResidual, IntPtr.Size, (uint)cudaSamples.Pointer);
					cuda.SetParameter(cudaEncodeResidual, IntPtr.Size * 2, (uint)cudaCoeffs.Pointer);
					cuda.SetParameter(cudaEncodeResidual, IntPtr.Size * 3, (uint)cudaResidualTasks.Pointer);
					cuda.SetParameter(cudaEncodeResidual, IntPtr.Size * 4, (uint)frame.blocksize);
					cuda.SetParameter(cudaEncodeResidual, IntPtr.Size * 4 + sizeof(uint), (uint)partSize);
					cuda.SetParameterSize(cudaEncodeResidual, (uint)(IntPtr.Size * 4) + sizeof(uint) * 2U);
					cuda.SetFunctionBlockShape(cudaEncodeResidual, 256, 1, 1);
					int residualOutputSize = sizeof(int) * partCount * nResidualTasks;

					//cuda.DestroyEvent(start);
					//cuda.DestroyEvent(stop);

					//start = cuda.CreateEvent();
					//stop = cuda.CreateEvent();

					// asynchronously issue work to the GPU (all to stream 0)
					//cuda.RecordEvent(start, cudaStream);
					cuda.CopyHostToDeviceAsync(cudaCoeffs, lpcCoeffBufferPtr, (uint)(sizeof(int) * lpc.MAX_LPC_ORDER * (orders - 1) * 4 * _windowcount), cudaStream);
					cuda.CopyHostToDeviceAsync(cudaResidualTasks, residualTasksPtr, (uint)(sizeof(encodeResidualTaskStruct) * nResidualTasks), cudaStream);
					cuda.LaunchAsync(cudaEncodeResidual, partCount, nResidualTasks, cudaStream);
					cuda.CopyDeviceToHostAsync(cudaResidualOutput, residualOutputPtr, (uint)residualOutputSize, cudaStream);
					//cuda.RecordEvent(stop, cudaStream);
					cuda.SynchronizeStream(cudaStream);

					//cuda.DestroyEvent(start);
					//cuda.DestroyEvent(stop);

					for (int ch = 0; ch < 4; ch++)
					{
						frame.subframes[ch].best.size = AudioSamples.UINT32_MAX;
						for (int iWindow = 0; iWindow < _windowcount; iWindow++)
						{
							for (int order = 1; order <= max_order; order++)
							{
								int nbits = 0;
								int index = (order - 1) + (orders - 1) * (iWindow + _windowcount * ch);
								for (int p = 0; p < partCount; p++)
									//nbits += residualOutput[p + partCount * index];
									nbits += ((int*)residualOutputPtr)[p + partCount * index];
								nbits += order * (int)frame.subframes[ch].obits + 4 + 5 + order * (int)cbits + 6;
								if (frame.subframes[ch].best.size > nbits)
								{
									encodeResidualTaskStruct* residualTasks = (encodeResidualTaskStruct*)residualTasksPtr;
									frame.subframes[ch].best.size = (uint)nbits;
									frame.subframes[ch].best.order = order;
									frame.subframes[ch].best.window = iWindow;
									frame.subframes[ch].best.type = SubframeType.LPC;
									frame.subframes[ch].lpc_ctx[iWindow].shift = residualTasks[index].shift;
									fixed (int* lcoefs = frame.subframes[ch].lpc_ctx[iWindow].coefs)
										AudioSamples.MemCpy(lcoefs, ((int*)lpcCoeffBufferPtr) + residualTasks[index].coefsOffs, order);
								}
							}
							//uint[] sums_buf = new uint[Flake.MAX_PARTITION_ORDER * Flake.MAX_PARTITIONS];
							//fixed (uint* sums = sums_buf)
							//    for (int order = 1; order <= max_order; order++)
							//{
							//    //uint nbits;
							//    //find_optimal_rice_param(2*(uint)residualOutput[order - 1 + (ch + 4 * iWindow) * max_order], (uint)frame.blocksize, out nbits);
							//    //uint nbits = (uint)residualOutput[order - 1 + (ch + 4 * iWindow) * max_order];
							//    //nbits += (uint)order * frame.subframes[ch].obits + 4 + 5 + (uint)order * cbits + 6;
							//    for (int ip = 0; ip < 64; ip++)
							//        sums[6 * Flake.MAX_PARTITIONS + ip] = (uint)residualOutput[64 * (order - 1 + (ch + 4 * iWindow) * max_order) + ip];
							//    for (int ip = 5; ip >= 0; ip--)
							//    {
							//        int parts = (1 << ip);
							//        for (int j = 0; j < parts; j++)
							//        {
							//            sums[ip * Flake.MAX_PARTITIONS + j] =
							//                sums[(ip + 1) * Flake.MAX_PARTITIONS + 2 * j] +
							//                sums[(ip + 1) * Flake.MAX_PARTITIONS + 2 * j + 1];
							//        }
							//    }
							//    for (int ip = 0; ip <= get_max_p_order(Math.Min(6, eparams.max_partition_order), frame.blocksize, order); ip++)
							//    {
							//        uint nbits = calc_optimal_rice_params(ref frame.current.rc, ip, sums + ip * Flake.MAX_PARTITIONS, (uint)frame.blocksize, (uint)order);
							//        nbits += (uint)order * frame.subframes[ch].obits + 4 + 5 + (uint)order * cbits + 6;
							//        if (frame.subframes[ch].best.size > nbits)
							//        {
							//            frame.subframes[ch].best.size = nbits;
							//            frame.subframes[ch].best.order = order;
							//            frame.subframes[ch].best.window = iWindow;
							//            frame.subframes[ch].best.type = SubframeType.LPC;
							//        }
							//    }
							//}
						}
					}

					uint fs = measure_frame_size(frame, true);
					frame.ChooseSubframes();
					for (int ch = 0; ch < channels; ch++)
					{
						frame.subframes[ch].best.size = AudioSamples.UINT32_MAX;
						encode_selected_residual(frame, ch, eparams.prediction_type, eparams.order_method,
							frame.subframes[ch].best.window, frame.subframes[ch].best.order);
					}
				}

				BitWriter bitwriter = new BitWriter(frame_buffer, 0, max_frame_size);

				output_frame_header(frame, bitwriter);
				output_subframes(frame, bitwriter);
				output_frame_footer(bitwriter);

				if (frame_buffer != null)
				{
					if (eparams.variable_block_size > 0)
						frame_count += frame.blocksize;
					else
						frame_count++;
				}
				size = frame.blocksize;
				return bitwriter.Length;
			}
		}

		unsafe int output_frame()
		{
			if (verify != null)
			{
				int* r = (int*)samplesBufferPtr;
				fixed (int* s = verifyBuffer)
					for (int ch = 0; ch < channels; ch++)
						AudioSamples.MemCpy(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * FlaCudaWriter.MAX_BLOCKSIZE, eparams.block_size);
			}

			int fs, bs;
			//if (0 != eparams.variable_block_size && 0 == (eparams.block_size & 7) && eparams.block_size >= 128)
			//    fs = encode_frame_vbs();
			//else
			fs = encode_frame(out bs);

			if (seek_table != null && _IO.CanSeek)
			{
				for (int sp = 0; sp < seek_table.Length; sp++)
				{
					if (seek_table[sp].framesize != 0)
						continue;
					if (seek_table[sp].number > (ulong)_position + (ulong)bs)
						break;
					if (seek_table[sp].number >= (ulong)_position)
					{
						seek_table[sp].number = (ulong)_position;
						seek_table[sp].offset = (ulong)(_IO.Position - first_frame_offset);
						seek_table[sp].framesize = (uint)bs;
					}
				}
			}

			_position += bs;
			_IO.Write(frame_buffer, 0, fs);
			_totalSize += fs;

			if (verify != null)
			{
				int decoded = verify.DecodeFrame(frame_buffer, 0, fs);
				if (decoded != fs || verify.Remaining != (ulong)bs)
					throw new Exception("validation failed!");
				fixed (int* s = verifyBuffer, r = verify.Samples)
				{
					for (int ch = 0; ch < channels; ch++)
						if (AudioSamples.MemCmp(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE, bs))
							throw new Exception("validation failed!");
				}
			}

			if (bs < eparams.block_size)
			{
				int* s = (int*)samplesBufferPtr;
				for (int ch = 0; ch < channels; ch++)
					AudioSamples.MemCpy(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, s + bs + ch * FlaCudaWriter.MAX_BLOCKSIZE, eparams.block_size - bs);
			}

			samplesInBuffer -= bs;

			return bs;
		}

		public unsafe void Write(int[,] buff, int pos, int sampleCount)
		{
			if (!inited)
			{
				cuda = new CUDA(true, InitializationFlags.None);
				cuda.CreateContext(0, CUCtxFlags.SchedSpin);
				cuda.LoadModule(System.IO.Path.Combine(Environment.CurrentDirectory, "flacuda.cubin"));
				cudaComputeAutocor = cuda.GetModuleFunction("cudaComputeAutocor");
				cudaEncodeResidual = cuda.GetModuleFunction("cudaEncodeResidual");
				cudaAutocor = cuda.Allocate((uint)(sizeof(float) * (lpc.MAX_LPC_ORDER + 1) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS) * 22);
				cudaSamples = cuda.Allocate((uint)(sizeof(int) * FlaCudaWriter.MAX_BLOCKSIZE * (channels == 2 ? 4 : channels)));
				cudaWindow = cuda.Allocate((uint)sizeof(float) * FlaCudaWriter.MAX_BLOCKSIZE * 2 * lpc.MAX_LPC_WINDOWS);
				cudaCoeffs = cuda.Allocate((uint)(sizeof(int) * lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS));
				cudaResidualTasks = cuda.Allocate((uint)(sizeof(encodeResidualTaskStruct) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_ORDER * lpc.MAX_LPC_WINDOWS));
				cudaResidualOutput = cuda.Allocate((uint)(sizeof(int) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_ORDER * lpc.MAX_LPC_WINDOWS * maxResidualTasks));
				CUResult cuErr = CUDADriver.cuMemAllocHost(ref autocorBufferPtr, (uint)(sizeof(float)*(lpc.MAX_LPC_ORDER + 1) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS * 22));
				if (cuErr == CUResult.Success)
					cuErr = CUDADriver.cuMemAllocHost(ref samplesBufferPtr, (uint)(sizeof(int) * (channels == 2 ? 4 : channels) * FlaCudaWriter.MAX_BLOCKSIZE));
				if (cuErr == CUResult.Success)
					cuErr = CUDADriver.cuMemAllocHost(ref residualOutputPtr, (uint)(sizeof(int) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS * lpc.MAX_LPC_ORDER * maxResidualTasks));
				if (cuErr == CUResult.Success)
					cuErr = CUDADriver.cuMemAllocHost(ref lpcCoeffBufferPtr, (uint)(sizeof(int) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS * lpc.MAX_LPC_ORDER * lpc.MAX_LPC_ORDER));
				if (cuErr == CUResult.Success)
					cuErr = CUDADriver.cuMemAllocHost(ref residualTasksPtr, (uint)(sizeof(encodeResidualTaskStruct) * (channels == 2 ? 4 : channels) * lpc.MAX_LPC_WINDOWS * lpc.MAX_LPC_ORDER));
				if (cuErr != CUResult.Success)
				{
					if (autocorBufferPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(autocorBufferPtr);
					if (samplesBufferPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(samplesBufferPtr);
					if (residualOutputPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(residualOutputPtr);
					if (lpcCoeffBufferPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(lpcCoeffBufferPtr);
					if (residualTasksPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(residualTasksPtr);
					throw new CUDAException(cuErr);
				}				
				cudaStream = cuda.CreateStream();
				if (_IO == null)
					_IO = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
				int header_size = flake_encode_init();
				_IO.Write(header, 0, header_size);
				if (_IO.CanSeek)
					first_frame_offset = _IO.Position;
				inited = true;
			}

			int len = sampleCount;
			while (len > 0)
			{
				int block = Math.Min(len, eparams.block_size - samplesInBuffer);

				copy_samples(buff, pos, block);

				if (md5 != null)
				{
					AudioSamples.FLACSamplesToBytes(buff, pos, frame_buffer, 0, block, channels, (int)bits_per_sample);
					md5.TransformBlock(frame_buffer, 0, block * channels * ((int)bits_per_sample >> 3), null, 0);
				}

				len -= block;
				pos += block;

				while (samplesInBuffer >= eparams.block_size)
					output_frame();
			}
		}

		public string Path { get { return _path; } }

		string vendor_string = "Flake#0.1";

		int select_blocksize(int samplerate, int time_ms)
		{
			int blocksize = Flake.flac_blocksizes[1];
			int target = (samplerate * time_ms) / 1000;
			if (eparams.variable_block_size > 0)
			{
				blocksize = 1024;
				while (target >= blocksize)
					blocksize <<= 1;
				return blocksize >> 1;
			}

			for (int i = 0; i < Flake.flac_blocksizes.Length; i++)
				if (target >= Flake.flac_blocksizes[i] && Flake.flac_blocksizes[i] > blocksize)
				{
					blocksize = Flake.flac_blocksizes[i];
				}
			return blocksize;
		}

		void write_streaminfo(byte[] header, int pos, int last)
		{
			Array.Clear(header, pos, 38);
			BitWriter bitwriter = new BitWriter(header, pos, 38);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.StreamInfo);
			bitwriter.writebits(24, 34);

			if (eparams.variable_block_size > 0)
				bitwriter.writebits(16, 0);
			else
				bitwriter.writebits(16, eparams.block_size);

			bitwriter.writebits(16, eparams.block_size);
			bitwriter.writebits(24, 0);
			bitwriter.writebits(24, max_frame_size);
			bitwriter.writebits(20, sample_rate);
			bitwriter.writebits(3, channels - 1);
			bitwriter.writebits(5, bits_per_sample - 1);

			// total samples
			if (sample_count > 0)
			{
				bitwriter.writebits(4, 0);
				bitwriter.writebits(32, sample_count);
			}
			else
			{
				bitwriter.writebits(4, 0);
				bitwriter.writebits(32, 0);
			}
			bitwriter.flush();
		}

		/**
		 * Write vorbis comment metadata block to byte array.
		 * Just writes the vendor string for now.
	     */
		int write_vorbis_comment(byte[] comment, int pos, int last)
		{
			BitWriter bitwriter = new BitWriter(comment, pos, 4);
			Encoding enc = new ASCIIEncoding();
			int vendor_len = enc.GetBytes(vendor_string, 0, vendor_string.Length, comment, pos + 8);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.VorbisComment);
			bitwriter.writebits(24, vendor_len + 8);

			comment[pos + 4] = (byte)(vendor_len & 0xFF);
			comment[pos + 5] = (byte)((vendor_len >> 8) & 0xFF);
			comment[pos + 6] = (byte)((vendor_len >> 16) & 0xFF);
			comment[pos + 7] = (byte)((vendor_len >> 24) & 0xFF);
			comment[pos + 8 + vendor_len] = 0;
			comment[pos + 9 + vendor_len] = 0;
			comment[pos + 10 + vendor_len] = 0;
			comment[pos + 11 + vendor_len] = 0;
			bitwriter.flush();
			return vendor_len + 12;
		}

		int write_seekpoints(byte[] header, int pos, int last)
		{
			seek_table_offset = pos + 4;

			BitWriter bitwriter = new BitWriter(header, pos, 4 + 18 * seek_table.Length);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.Seektable);
			bitwriter.writebits(24, 18 * seek_table.Length);
			for (int i = 0; i < seek_table.Length; i++)
			{
				bitwriter.writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_SAMPLE_NUMBER_LEN, seek_table[i].number);
				bitwriter.writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_STREAM_OFFSET_LEN, seek_table[i].offset);
				bitwriter.writebits(Flake.FLAC__STREAM_METADATA_SEEKPOINT_FRAME_SAMPLES_LEN, seek_table[i].framesize);
			}
			bitwriter.flush();
			return 4 + 18 * seek_table.Length;
		}

		/**
		 * Write padding metadata block to byte array.
		 */
		int
		write_padding(byte[] padding, int pos, int last, int padlen)
		{
			BitWriter bitwriter = new BitWriter(padding, pos, 4);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.Padding);
			bitwriter.writebits(24, padlen);

			return padlen + 4;
		}

		int write_headers()
		{
			int header_size = 0;
			int last = 0;

			// stream marker
			header[0] = 0x66;
			header[1] = 0x4C;
			header[2] = 0x61;
			header[3] = 0x43;
			header_size += 4;

			// streaminfo
			write_streaminfo(header, header_size, last);
			header_size += 38;

			// seek table
			if (_IO.CanSeek && seek_table != null)
				header_size += write_seekpoints(header, header_size, last);

			// vorbis comment
			if (eparams.padding_size == 0) last = 1;
			header_size += write_vorbis_comment(header, header_size, last);

			// padding
			if (eparams.padding_size > 0)
			{
				last = 1;
				header_size += write_padding(header, header_size, last, eparams.padding_size);
			}

			return header_size;
		}

		int flake_encode_init()
		{
			int i, header_len;

			//if(flake_validate_params(s) < 0)

			ch_code = channels - 1;

			// find samplerate in table
			for (i = 4; i < 12; i++)
			{
				if (sample_rate == Flake.flac_samplerates[i])
				{
					sr_code0 = i;
					break;
				}
			}

			// if not in table, samplerate is non-standard
			if (i == 12)
				throw new Exception("non-standard samplerate");

			for (i = 1; i < 8; i++)
			{
				if (bits_per_sample == Flake.flac_bitdepths[i])
				{
					bps_code = i;
					break;
				}
			}
			if (i == 8)
				throw new Exception("non-standard bps");
			// FIXME: For now, only 16-bit encoding is supported
			if (bits_per_sample != 16)
				throw new Exception("non-standard bps");

			if (_blocksize == 0)
			{
				if (eparams.block_size == 0)
					eparams.block_size = select_blocksize(sample_rate, eparams.block_time_ms);
				_blocksize = eparams.block_size;
			}
			else
				eparams.block_size = _blocksize;

			// set maximum encoded frame size (if larger, re-encodes in verbatim mode)
			if (channels == 2)
				max_frame_size = 16 + ((eparams.block_size * (int)(bits_per_sample + bits_per_sample + 1) + 7) >> 3);
			else
				max_frame_size = 16 + ((eparams.block_size * channels * (int)bits_per_sample + 7) >> 3);

			if (_IO.CanSeek && eparams.do_seektable)
			{
				int seek_points_distance = sample_rate * 10;
				int num_seek_points = 1 + sample_count / seek_points_distance; // 1 seek point per 10 seconds
				if (sample_count % seek_points_distance == 0)
					num_seek_points--;
				seek_table = new SeekPoint[num_seek_points];
				for (int sp = 0; sp < num_seek_points; sp++)
				{
					seek_table[sp].framesize = 0;
					seek_table[sp].offset = 0;
					seek_table[sp].number = (ulong)(sp * seek_points_distance);
				}
			}

			// output header bytes
			header = new byte[eparams.padding_size + 1024 + (seek_table == null ? 0 : seek_table.Length * 18)];
			header_len = write_headers();

			// initialize CRC & MD5
			if (_IO.CanSeek && eparams.do_md5)
				md5 = new MD5CryptoServiceProvider();

			if (eparams.do_verify)
			{
				verify = new FlakeReader(channels, bits_per_sample);
				verifyBuffer = new int[FlaCudaWriter.MAX_BLOCKSIZE * channels];
			}

			frame_buffer = new byte[max_frame_size];

			return header_len;
		}
	}

	struct FlakeEncodeParams
	{
		// compression quality
		// set by user prior to calling flake_encode_init
		// standard values are 0 to 8
		// 0 is lower compression, faster encoding
		// 8 is higher compression, slower encoding
		// extended values 9 to 12 are slower and/or use
		// higher prediction orders
		public int compression;

		// prediction order selection method
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 5
		// 0 = use maximum order only
		// 1 = use estimation
		// 2 = 2-level
		// 3 = 4-level
		// 4 = 8-level
		// 5 = full search
		// 6 = log search
		public OrderMethod order_method;


		// stereo decorrelation method
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 2
		// 0 = independent L+R channels
		// 1 = mid-side encoding
		public StereoMethod stereo_method;

		// block size in samples
		// set by the user prior to calling flake_encode_init
		// if set to 0, a block size is chosen based on block_time_ms
		// can also be changed by user before encoding a frame
		public int block_size;

		// block time in milliseconds
		// set by the user prior to calling flake_encode_init
		// used to calculate block_size based on sample rate
		// can also be changed by user before encoding a frame
		public int block_time_ms;

		// padding size in bytes
		// set by the user prior to calling flake_encode_init
		// if set to less than 0, defaults to 4096
		public int padding_size;

		// minimum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int min_prediction_order;

		// maximum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32 
		public int max_prediction_order;

		// Number of LPC orders to try (for estimate mode)
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32 
		public int estimation_depth;

		// minimum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4
		public int min_fixed_order;

		// maximum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4
		public int max_fixed_order;

		// type of linear prediction
		// set by user prior to calling flake_encode_init
		public PredictionType prediction_type;

		// minimum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int min_partition_order;

		// maximum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int max_partition_order;

		// whether to use variable block sizes
		// set by user prior to calling flake_encode_init
		// 0 = fixed block size
		// 1 = variable block size
		public int variable_block_size;

		// whether to try various lpc_precisions
		// 0 - use only one precision
		// 1 - try two precisions
		public int lpc_max_precision_search;

		public int lpc_min_precision_search;

		public WindowFunction window_function;

		public bool do_md5;
		public bool do_verify;
		public bool do_seektable;

		public int flake_set_defaults(int lvl)
		{
			compression = lvl;

			if ((lvl < 0 || lvl > 12) && (lvl != 99))
			{
				return -1;
			}

			// default to level 5 params
			window_function = WindowFunction.Flattop | WindowFunction.Tukey;
			order_method = OrderMethod.Estimate;
			stereo_method = StereoMethod.Evaluate;
			block_size = 0;
			block_time_ms = 105;			
			prediction_type = PredictionType.Search;
			min_prediction_order = 1;
			max_prediction_order = 12;
			estimation_depth = 1;
			min_fixed_order = 2;
			max_fixed_order = 2;
			min_partition_order = 0;
			max_partition_order = 6;
			variable_block_size = 0;
			lpc_min_precision_search = 1;
			lpc_max_precision_search = 1;
			do_md5 = true;
			do_verify = false;
			do_seektable = true; 

			// differences from level 7
			switch (lvl)
			{
				case 0:
					block_time_ms = 53;
					prediction_type = PredictionType.Fixed;
					stereo_method = StereoMethod.Independent;
					max_partition_order = 4;
					break;
				case 1:
					prediction_type = PredictionType.Levinson;
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 8;
					max_partition_order = 4;
					break;
				case 2:
					stereo_method = StereoMethod.Independent;
					window_function = WindowFunction.Bartlett;
					max_partition_order = 4;
					break;
				case 3:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 8;
					break;
				case 4:
					stereo_method = StereoMethod.Estimate;
					window_function = WindowFunction.Bartlett;
					break;
				case 5:
					window_function = WindowFunction.Bartlett;
					break;
				case 6:
					stereo_method = StereoMethod.Estimate;
					break;
				case 7:
					break;
				case 8:
					estimation_depth = 3;
					min_fixed_order = 0;
					max_fixed_order = 4;
					lpc_max_precision_search = 2;
					break;
				case 9:
					window_function = WindowFunction.Bartlett;
					max_prediction_order = 32;
					break;
				case 10:
					min_fixed_order = 0;
					max_fixed_order = 4;
					max_prediction_order = 32;
					//lpc_max_precision_search = 2;
					break;
				case 11:
					min_fixed_order = 0;
					max_fixed_order = 4;
					max_prediction_order = 32;
					estimation_depth = 5;
					//lpc_max_precision_search = 2;
					variable_block_size = 4;
					break;
			}

			return 0;
		}
	}
	
	struct encodeResidualTaskStruct
	{
		public int residualOrder;
		public int shift;
		public int coefsOffs;
		public int samplesOffs;
	};
}