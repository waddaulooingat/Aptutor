using System.Diagnostics;

namespace ApTutor.Narration;

public sealed record PiperOptions(string ExecutablePath, string ModelPath, string ConfigPath, string? EspeakDataPath = null)
{
    /// True if the executable and model/config files this instance points at actually exist —
    /// callers use this to decide between a real PiperVoiceSynthesizer and NullVoiceSynthesizer
    /// rather than constructing one that's guaranteed to fail on first use.
    public bool IsAvailable =>
        File.Exists(ExecutablePath) && File.Exists(ModelPath) && File.Exists(ConfigPath);
}

/// Shells out to the `piper` CLI per call. Piper has no .NET binding — this is the documented,
/// supported way to drive it (stdin: raw text; -f: WAV output path).
public sealed class PiperVoiceSynthesizer(PiperOptions options) : IVoiceSynthesizer
{
    public async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"aptutor-tts-{Guid.NewGuid():N}.wav");
        var psi = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(options.ModelPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(options.ConfigPath);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(outputPath);
        if (options.EspeakDataPath != null)
        {
            psi.ArgumentList.Add("--espeak_data");
            psi.ArgumentList.Add(options.EspeakDataPath);
        }

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start piper at '{options.ExecutablePath}'.");

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"piper exited with code {process.ExitCode}: {await stderrTask}");

            return await File.ReadAllBytesAsync(outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
