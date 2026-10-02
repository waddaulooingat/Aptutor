// "explain it to me" experiment (explain-it-to-me-experiment branch only — see its own handoff) —
// records a follow-up question from the microphone and transcribes it locally via Whisper.net,
// rather than a cloud speech-to-text API: this experiment already needs one live API key
// (Anthropic's) for the conversation itself, and there's no reason to require a second account just
// to prove the voice-in/voice-out loop feels right. NAudio's WaveInEvent only runs on Windows (it
// wraps Win32 audio APIs) — an accepted constraint for an experiment branch nobody ships to other
// platforms; see the csproj's own remarks.

using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace ApTutor.Client.Services;

public sealed class TutorVoiceInputService : IDisposable
{
    private readonly WhisperFactory _whisperFactory;
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;

    private TutorVoiceInputService(WhisperFactory factory) => _whisperFactory = factory;

    /// Downloads the (small, ~75MB) GGML model to modelPath on first use if it isn't already there —
    /// a one-time cost for this experiment, not something a real Plus-tier feature would do silently
    /// on every machine (that's exactly the kind of production packaging/distribution question the
    /// handoff explicitly defers). GgmlType.Base is a reasonable speed/accuracy balance for a
    /// feel-test; swap to Tiny for faster (less accurate) or Small for slower (more accurate) if the
    /// experiment's own testing calls for it.
    public static async Task<TutorVoiceInputService> CreateAsync(string modelPath, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            using var httpClient = new HttpClient();
            await using var modelStream = await new WhisperGgmlDownloader(httpClient).GetGgmlModelAsync(GgmlType.Base, cancellationToken: ct);
            await using var fileStream = File.Create(modelPath);
            await modelStream.CopyToAsync(fileStream, ct);
        }

        return new TutorVoiceInputService(WhisperFactory.FromPath(modelPath));
    }

    public bool IsRecording => _waveIn is not null;

    /// 16kHz mono 16-bit PCM — the sample rate Whisper's own models are trained on; recording at
    /// anything else would just make Whisper resample internally for no benefit.
    public void StartRecording()
    {
        if (_waveIn is not null) return;

        _buffer = new MemoryStream();
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _writer = new WaveFileWriter(_buffer, _waveIn.WaveFormat);
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) => _writer!.Write(e.Buffer, 0, e.BytesRecorded);

    /// Stops recording, transcribes whatever was captured, and returns the transcript text (empty if
    /// nothing was recorded or Whisper produced no segments — never null, so callers don't need a
    /// separate "nothing was said" branch).
    public async Task<string> StopRecordingAndTranscribeAsync(CancellationToken ct = default)
    {
        if (_waveIn is not { } waveIn) return "";

        waveIn.StopRecording();
        waveIn.DataAvailable -= OnDataAvailable;
        _writer!.Flush();
        var wavBytes = _buffer!.ToArray();
        waveIn.Dispose();
        _writer.Dispose();
        _buffer.Dispose();
        _waveIn = null;
        _writer = null;
        _buffer = null;

        if (wavBytes.Length == 0) return "";

        using var processor = _whisperFactory.CreateBuilder().WithLanguage("en").Build();
        await using var audioStream = new MemoryStream(wavBytes);

        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audioStream, ct))
            transcript.Append(segment.Text);

        return transcript.ToString().Trim();
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _writer?.Dispose();
        _buffer?.Dispose();
        _whisperFactory.Dispose();
    }
}
