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
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
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
