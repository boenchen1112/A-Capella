namespace Acapella.Engine.Pitch;

/// <summary>
/// Pitch-correction backend the Mix Engine calls for any layer not using Melodyne's manual
/// (Phase 2A) path. AutoPitchCorrector is the only implementation for now; Phase 2A will add a
/// Melodyne-ARA-backed implementation that instead returns audio rendered from the user's manual
/// edits.
/// </summary>
public interface IPitchCorrectionBackend
{
    float[] Correct(float[] samples, int sampleRate);
}
