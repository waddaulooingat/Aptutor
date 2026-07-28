using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ApTutor.Narration;

public interface IAudioPlayer
{
    /// Plays WAV bytes to completion. A zero-length buffer (NullVoiceSynthesizer's output, or a
    /// synthesis failure the caller chose to swallow) completes immediately — narration is
    /// best-effort, never a hard dependency for stepping through a walkthrough.
    Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default);
}

/// No-op — used when no supported playback backend is found for the current OS/tooling.
public sealed class NullAudioPlayer : IAudioPlayer
{
    public Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class AudioPlayerFactory
{
    /// Cross-platform playback with no extra native audio library dependency: shell out to each
    /// OS's own built-in player. Deliberately lightweight for a first cut — Phase 12 packaging
    /// may want something more robust (device selection, volume, etc.) but this is enough to
    /// prove per-step narration end-to-end.
    public static IAudioPlayer Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsAudioPlayer();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new ProcessAudioPlayer("afplay", QuotedPathOnly);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var player = FindOnPath("paplay") ?? FindOnPath("aplay");
            if (player != null) return new ProcessAudioPlayer(player, QuotedPathOnly);
        }
        return new NullAudioPlayer();
    }

    private static string QuotedPathOnly(string path) => $"\"{path}\"";

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// Shells out to a command-line player (afplay on macOS, paplay/aplay on Linux) against a temp
/// WAV file, awaiting process exit as "playback finished" — good enough since none of these
/// tools return before the audio has finished playing.
internal sealed class ProcessAudioPlayer(string executablePath, Func<string, string> formatArgs) : IAudioPlayer
{
    public async Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes.Length == 0) return;

        var path = Path.Combine(Path.GetTempPath(), $"aptutor-play-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(path, wavBytes, cancellationToken);
            var psi = new ProcessStartInfo(executablePath, formatArgs(path)) { UseShellExecute = false };
            using var process = Process.Start(psi);
            if (process != null) await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

/// P/Invoke winmm's PlaySound against an in-memory buffer — no temp file, no extra package.
internal sealed class WindowsAudioPlayer : IAudioPlayer
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(byte[] data, IntPtr hMod, uint flags);

    private const uint SndMemory = 0x0004;
    private const uint SndSync = 0x0000;

    public Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes.Length == 0) return Task.CompletedTask;
        // SND_SYNC blocks the calling thread until playback finishes, matching the other
        // backends' "PlayAsync completes when audio is done" contract — run it off the UI thread.
        return Task.Run(() => PlaySound(wavBytes, IntPtr.Zero, SndMemory | SndSync), cancellationToken);
    }
}
