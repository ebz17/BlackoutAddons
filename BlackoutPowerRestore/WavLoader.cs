using System;
using System.IO;
using UnityEngine;

namespace BlackoutPowerRestore
{
    /// <summary>
    /// Minimal RIFF/WAVE reader (PCM 8/16/24/32-bit or IEEE float).
    /// Used to read each file's real duration (clip.length can't be trusted in-game) and as a
    /// fallback loader if Unity's own decoder fails.
    /// </summary>
    internal static class WavLoader
    {
        private struct Header
        {
            public int Channels, Rate, Bits, Format, DataOffset, DataLength;
        }

        /// <summary>Duration in seconds straight from the WAV header.</summary>
        public static float Duration(string path)
        {
            try
            {
                var h = ReadHeader(File.ReadAllBytes(path));
                int frameBytes = h.Channels * (h.Bits / 8);
                return frameBytes > 0 && h.Rate > 0 ? (float)h.DataLength / frameBytes / h.Rate : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        public static AudioClip Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var h = ReadHeader(data);
            int bytesPerSample = h.Bits / 8;
            int count = h.DataLength / bytesPerSample;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int o = h.DataOffset + i * bytesPerSample;
                switch (h.Bits)
                {
                    case 8:
                        samples[i] = (data[o] - 128) / 128f;
                        break;
                    case 16:
                        samples[i] = BitConverter.ToInt16(data, o) / 32768f;
                        break;
                    case 24:
                        samples[i] = ((data[o] << 8 | data[o + 1] << 16 | data[o + 2] << 24) >> 8) / 8388608f;
                        break;
                    case 32:
                        samples[i] = h.Format == 3 ? BitConverter.ToSingle(data, o) : BitConverter.ToInt32(data, o) / 2147483648f;
                        break;
                    default:
                        throw new InvalidDataException($"unsupported bit depth {h.Bits}");
                }
            }

            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), count / h.Channels, h.Channels, h.Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static Header ReadHeader(byte[] data)
        {
            if (data.Length < 44 || ReadTag(data, 0) != "RIFF" || ReadTag(data, 8) != "WAVE")
            {
                throw new InvalidDataException("not a RIFF/WAVE file");
            }
            var h = new Header { DataOffset = -1 };
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string id = ReadTag(data, pos);
                int size = BitConverter.ToInt32(data, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    h.Format = BitConverter.ToUInt16(data, body);
                    h.Channels = BitConverter.ToUInt16(data, body + 2);
                    h.Rate = BitConverter.ToInt32(data, body + 4);
                    h.Bits = BitConverter.ToUInt16(data, body + 14);
                    if (h.Format == 0xFFFE && size >= 40) h.Format = BitConverter.ToUInt16(data, body + 24);
                }
                else if (id == "data")
                {
                    h.DataOffset = body;
                    h.DataLength = Math.Min(size, data.Length - body);
                    break;
                }
                pos = body + size + (size & 1);
            }
            if (h.DataOffset < 0 || h.Channels <= 0 || h.Rate <= 0 || h.Bits <= 0)
            {
                throw new InvalidDataException("missing fmt/data chunk");
            }
            return h;
        }

        private static string ReadTag(byte[] d, int o) =>
            o + 4 <= d.Length ? new string(new[] { (char)d[o], (char)d[o + 1], (char)d[o + 2], (char)d[o + 3] }) : "";
    }
}
