using RubberBandSharp;

namespace Acapella.Engine.Pitch;

/// <summary>
/// Detects the input's pitch, snaps it to the nearest note in the target scale, and applies a
/// constant pitch-shift ratio via Rubber Band. v1 uses a single detected pitch and a single
/// correction ratio for the whole buffer (matches the build plan's acceptance test: a recording
/// with one known, deliberately-flattened pitch). Time-varying, per-note correction is a later
/// refinement, not required by Phase 2B's acceptance criteria.
/// </summary>
public class AutoPitchCorrector : IPitchCorrectionBackend
{
    public float[] Correct(float[] samples, int sampleRate)
    {
        double? detectedHz = DetectOverallPitch(samples, sampleRate);
        if (detectedHz is null)
            return samples; // nothing periodic detected; pass through unmodified

        double targetHz = ScaleQuantizer.NearestNoteFrequency(detectedHz.Value);
        double pitchScale = targetHz / detectedHz.Value;

        return ApplyPitchShift(samples, sampleRate, pitchScale);
    }

    /// <summary>
    /// Detects an overall pitch estimate by taking the median of per-frame YIN detections across
    /// the buffer (robust to a few unvoiced/noisy frames without needing a full note-segmentation
    /// pass).
    /// </summary>
    public static double? DetectOverallPitch(float[] samples, int sampleRate, int frameSize = 2048, int hopSize = 1024)
    {
        var detections = new List<double>();
        for (int start = 0; start + frameSize <= samples.Length; start += hopSize)
        {
            var frame = new ReadOnlySpan<float>(samples, start, frameSize);
            var hz = PitchDetector.DetectPitchHz(frame, sampleRate);
            if (hz.HasValue)
                detections.Add(hz.Value);
        }

        if (detections.Count == 0)
            return null;

        detections.Sort();
        return detections[detections.Count / 2];
    }

    public static float[] ApplyPitchShift(float[] samples, int sampleRate, double pitchScale)
    {
        var stretcher = new RubberBandStretcherMono(sampleRate,
            RubberBandStretcher.Options.ProcessRealTime |
            RubberBandStretcher.Options.PitchHighConsistency,
            initialTimeRatio: 1.0,
            initialPitchScale: pitchScale);

        const int chunkSize = 1024;
        var output = new List<float>(samples.Length);
        var outputBuffer = new float[chunkSize];

        int inputOffset = 0;
        bool finalSent = false;

        // Terminates deterministically: input is strictly bounded (inputOffset only advances
        // until finalSent), and once finalSent is true with nothing left available, we stop
        // rather than polling indefinitely for more output that a synchronous realtime-mode
        // Process() call would already have produced.
        while (true)
        {
            int avail = stretcher.Available();
            if (avail > 0)
            {
                int toRead = Math.Min(avail, chunkSize);
                uint read = stretcher.Retrieve(outputBuffer.AsSpan(0, toRead), (uint)toRead);
                for (int i = 0; i < read; i++)
                    output.Add(outputBuffer[i]);
                continue;
            }

            if (finalSent)
                break;

            uint required = stretcher.GetSamplesRequired();
            int toProcess = (int)Math.Min(required == 0 ? chunkSize : required, samples.Length - inputOffset);
            toProcess = Math.Max(toProcess, 0);
            bool isFinal = inputOffset + toProcess >= samples.Length;

            stretcher.Process(samples.AsSpan(inputOffset, toProcess), (uint)toProcess, isFinal);
            inputOffset += toProcess;
            if (isFinal)
                finalSent = true;
        }

        return output.ToArray();
    }
}
