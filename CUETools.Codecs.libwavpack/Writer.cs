using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CUETools.Codecs;

namespace CUETools.Codecs.libwavpack
{
    public class WriterSettings : AudioEncoderSettings
    {
        public WriterSettings()
            : base("fast normal high high+", "normal")
        {
        }

        [DefaultValue(0)]
		[DisplayName("ExtraMode")]
        public int ExtraMode { 
            get => m_extraMode; 
            set {
				if ((value < 0) || (value > 6)) {
					throw new Exception("Invalid extra mode.");
				}
				m_extraMode = value;
            }
        }

        [DefaultValue(true)]
        [DisplayName("MD5")]
        [Description("Calculate MD5 hash for audio stream")]
        public bool MD5Sum { get; set; }

        [DisplayName("Version")]
        [Description("Library version")]
        public string Version => Marshal.PtrToStringAnsi(wavpackdll.WavpackGetLibraryVersionString());

        private int m_extraMode;
    };

    [AudioEncoderClass("libwavpack", "wv", true, 1, typeof(WriterSettings))]
    public unsafe class Writer : IAudioDest
    {
        public Writer(string path, Stream output, WriterSettings settings)
        {
            m_path = path;
            m_stream = output;
            m_settings = settings;
            m_streamGiven = output != null;
            m_initialized = false;
            m_finalSampleCount = 0;
            m_samplesWritten = 0;
            m_blockOutput = BlockOutputCallback;
            if (m_settings.PCM.BitsPerSample < 16 || m_settings.PCM.BitsPerSample > 24)
                throw new Exception("bits per sample must be 16..24");
        }

        public Writer(string path, WriterSettings settings)
            : this(path, null, settings)
        {
        }

        public AudioEncoderSettings Settings => m_settings;

        public string Path { get => m_path; }

        public long FinalSampleCount
        {
            get => m_finalSampleCount;
            set
            {
                if (value < 0)
                    throw new Exception("invalid final sample count");
                if (m_initialized)
                    throw new Exception("final sample count cannot be changed after encoding begins");
                m_finalSampleCount = value;
            }
        }

        public void Close()
        {
            if (m_initialized)
            {
    		    if (m_settings.MD5Sum)
	    	    {
        			_md5hasher.TransformFinalBlock (new byte[1], 0, 0);
        			fixed (byte* md5_digest = &_md5hasher.Hash[0])
        			    wavpackdll.WavpackStoreMD5Sum (_wpc, md5_digest);
        		}
                wavpackdll.WavpackFlushSamples(_wpc);
    		    _wpc = wavpackdll.WavpackCloseFile(_wpc);
                m_initialized = false;
            }
            if (m_stream != null)
            {
                m_stream.Close();
                m_stream = null;
            }
            if ((m_finalSampleCount != 0) && (m_samplesWritten != m_finalSampleCount))
                throw new Exception("samples written differs from the expected sample count");
        }

        public void Delete()
        {
            try
            {
                if (m_initialized)
                {
        		    _wpc = wavpackdll.WavpackCloseFile(_wpc);
                    m_initialized = false;
                }
                if (m_stream != null)
                {
                    m_stream.Close();
                    m_stream = null;
                }
            }
            catch (Exception)
            {
            }
            if (m_path != "")
                File.Delete(m_path);
        }

		private void UpdateHash(byte[] buff, int len) 
		{
            if (!m_settings.MD5Sum) throw new Exception("MD5 not enabled.");
            if (!m_initialized) Initialize();
			_md5hasher.TransformBlock (buff, 0, len,  buff, 0);
		}

        public void Write(AudioBuffer sampleBuffer)
        {
            if (!m_initialized) Initialize();

            sampleBuffer.Prepare(this);

			if (m_settings.MD5Sum)
				UpdateHash(sampleBuffer.Bytes, sampleBuffer.ByteLength);

            int[,] samples = sampleBuffer.Samples;
            if ((m_settings.PCM.BitsPerSample & 7) != 0)
            {
                if (_shiftedSampleBuffer == null || _shiftedSampleBuffer.GetLength(0) < sampleBuffer.Length)
                    _shiftedSampleBuffer = new int[sampleBuffer.Length, m_settings.PCM.ChannelCount];
                int shift = 8 - (m_settings.PCM.BitsPerSample & 7);
                int ch = m_settings.PCM.ChannelCount;
                for (int i = 0; i < sampleBuffer.Length; i++)
                    for (int c = 0; c < ch; c++)
                        _shiftedSampleBuffer[i, c] = sampleBuffer.Samples[i, c] << shift;
                samples = _shiftedSampleBuffer;
            }

            fixed (int* pSampleBuffer = &samples[0, 0])
                if (0 == wavpackdll.WavpackPackSamples(_wpc, pSampleBuffer, (uint)sampleBuffer.Length))
                    throw new Exception("An error occurred while encoding: " + wavpackdll.WavpackGetErrorMessage(_wpc));

            m_samplesWritten += sampleBuffer.Length;
        }

        private int BlockOutputCallback(void* id, byte[] data, int bcount)
        {
            m_stream.Write(data, 0, bcount);
            return 1;
        }

        void Initialize()
        {
            if (m_stream == null)
                m_stream = new FileStream(m_path, FileMode.Create, FileAccess.Write, FileShare.Read, 0x10000);

            WavpackConfig config = new WavpackConfig();
			config.bits_per_sample = m_settings.PCM.BitsPerSample;
			config.bytes_per_sample = (m_settings.PCM.BitsPerSample + 7) / 8;
			config.num_channels = m_settings.PCM.ChannelCount;
			config.channel_mask = (int)m_settings.PCM.ChannelMask;
			config.sample_rate = m_settings.PCM.SampleRate;
            config.flags = ConfigFlags.CONFIG_COMPATIBLE_WRITE;
            Int32 _compressionMode = m_settings.EncoderModeIndex;
            if (_compressionMode == 0) config.flags |= ConfigFlags.CONFIG_FAST_FLAG;
			if (_compressionMode == 2) config.flags |= ConfigFlags.CONFIG_HIGH_FLAG;
			if (_compressionMode == 3) config.flags |= ConfigFlags.CONFIG_HIGH_FLAG | ConfigFlags.CONFIG_VERY_HIGH_FLAG;
			if (m_settings.ExtraMode != 0) 
			{
			    config.flags |= ConfigFlags.CONFIG_EXTRA_MODE;
			    config.xmode = m_settings.ExtraMode;
			}
			if (m_settings.MD5Sum)
			{
			    _md5hasher = new MD5CryptoServiceProvider ();
			    config.flags |= ConfigFlags.CONFIG_MD5_CHECKSUM;
			}
			config.block_samples = (int)m_settings.BlockSize;
			if (m_settings.BlockSize > 0 && m_settings.BlockSize < 2048)
				config.flags |= ConfigFlags.CONFIG_MERGE_BLOCKS;

            _wpc = wavpackdll.WavpackOpenFileOutput(m_blockOutput, null, null);
            if (_wpc == null)
                throw new Exception("Unable to create the encoder.");
            if (0 == wavpackdll.WavpackSetConfiguration64(_wpc, &config, (m_finalSampleCount == 0) ? -1 : m_finalSampleCount, null))
				throw new Exception("Invalid configuration setting:" + wavpackdll.WavpackGetErrorMessage(_wpc));
			if (0 == wavpackdll.WavpackPackInit(_wpc))
				throw new Exception("Unable to initialize the encoder: " + wavpackdll.WavpackGetErrorMessage(_wpc));

			m_initialized = true;
        }

        int[,] _shiftedSampleBuffer;
        WavpackContext* _wpc;
        WriterSettings m_settings;
        Stream m_stream;
        MD5 _md5hasher;
        bool m_streamGiven;
        bool m_initialized;
        string m_path;
        Int64 m_finalSampleCount, m_samplesWritten;
        EncoderBlockOutput m_blockOutput;
    }
}
