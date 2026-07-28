using ApTutor.Narration;
using Xunit;

namespace ApTutor.Tests;

// No audio hardware in this environment (or most CI) to actually *hear* output, so this validates
// the WAV structurally: a real header, a non-zero duration, and genuine (non-silent) samples.
// Skips itself (soft pass, no assertions run) when Piper isn't configured via the
// VoiceSynthesizerFactory environment variables — xUnit v2 has no first-class runtime skip, and
// this keeps the test honest about being opt-in rather than failing CI machines with no Piper install.
public class PiperVoiceSynthesizerTests
{
    [Fact]
    public async Task SynthesizeWavAsync_WithConfiguredPiper_ProducesNonSilentWav()
    {
        var options = VoiceSynthesizerFactory.TryReadOptionsFromEnvironment();
        if (options is not { IsAvailable: true }) return; // Piper not configured in this environment — skip

        var synthesizer = new PiperVoiceSynthesizer(options);

        var wav = await synthesizer.SynthesizeWavAsync("Reference and value are not the same thing.");

        Assert.True(wav.Length > 44, "WAV should have more than just a header."); // canonical WAV header is 44 bytes
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));

        // 16-bit PCM samples start right after the canonical 44-byte header for this format.
        var maxAmplitude = 0;
        for (var i = 44; i + 1 < wav.Length; i += 2)
        {
            var sample = Math.Abs((short)(wav[i] | (wav[i + 1] << 8)));
            if (sample > maxAmplitude) maxAmplitude = sample;
        }
        Assert.True(maxAmplitude > 1000, $"Expected genuine audio, got near-silence (max amplitude {maxAmplitude}).");
    }
}
