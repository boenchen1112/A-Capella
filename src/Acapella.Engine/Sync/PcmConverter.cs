using NAudio.Wave;

namespace Acapella.Engine.Sync;

public static class PcmConverter
{
    public static float[] BytesToFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;
        var samples = new float[sampleCount];

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRecorded);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * 3;
                int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000); // sign-extend
                samples[i] = value / 8388608f;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
        }
        else
        {
            throw new NotSupportedException($"Unsupported capture format: {format.Encoding}, {format.BitsPerSample}-bit.");
        }

        return samples;
    }

    public static float[] DownmixToMono(float[] samples, int channels)
    {
        if (channels <= 1) return samples;
        var mono = new float[samples.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    public static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate) return samples;
        int newLength = (int)((long)samples.Length * toRate / fromRate);
        var result = new float[newLength];
        for (int i = 0; i < newLength; i++)
        {
            double srcPos = i * (double)fromRate / toRate;
            int idx = (int)srcPos;
            result[i] = idx < samples.Length ? samples[idx] : 0f;
        }
        return result;
    }
}
