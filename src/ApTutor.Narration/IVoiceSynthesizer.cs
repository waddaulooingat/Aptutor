// AP Tutor — Phase 4: TTS narration. There's no PHASE4-HANDOFF.md and no GeoTutor source in this
// repo to port from (checked — only mentions of "TTS" in comments/docs, no actual code), so this
// is a from-scratch pick: Piper (github.com/rhasspy/piper, MIT), an offline neural TTS engine.
// Chosen over a cloud TTS specifically because build-plan.md's Core tier is "$49 one-time, fully
// offline... no server, no API key" — a cloud voice API would break that promise outright.

namespace ApTutor.Narration;

public interface IVoiceSynthesizer
{
    /// Synthesizes `text` to 16-bit PCM WAV bytes. Throws if synthesis fails.
    Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken = default);
}

/// Always "succeeds" with zero-length audio — the fallback when Piper isn't installed/configured,
/// so the walkthrough UI degrades to silent (caption-only) stepping instead of failing to start.
public sealed class NullVoiceSynthesizer : IVoiceSynthesizer
{
    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(Array.Empty<byte>());
}
