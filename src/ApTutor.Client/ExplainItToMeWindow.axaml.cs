// "explain it to me" experiment (explain-it-to-me-experiment branch only — see its own handoff) —
// a live, grounded, spoken conversation about whatever node the student had open. Opens already
// talking (the initial explanation fires as soon as the window opens, no extra click needed), then
// accepts follow-up questions either typed or spoken (hold the mic button, release to send).
//
// Deliberately a single, fairly direct window class rather than split into a separate ViewModel —
// consistent with how every other auxiliary window in this Shell (MockExamWindow,
// ProgressChartWindow) is built, and appropriate for an experiment that may be thrown away.

using ApTutor.Client.Services;
using ApTutor.ContentFactory;
using ApTutor.Narration;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ApTutor.Client;

public partial class ExplainItToMeWindow : Window
{
    private readonly TutorChatService _chat;
    private readonly IVoiceSynthesizer _synthesizer;
    private readonly IAudioPlayer _audioPlayer;
    private readonly string _voiceModelPath;
    private TutorVoiceInputService? _voiceInput;
    private bool _busy;

    public ExplainItToMeWindow(string apiKey, string nodeTitle, string lessonContent)
    {
        InitializeComponent();
        HeaderText.Text = $"Explain it to me — {nodeTitle}";

        var client = new ClaudeClient(apiKey, "claude-sonnet-5");
        _chat = new TutorChatService(client, nodeTitle, lessonContent);
        _synthesizer = VoiceSynthesizerFactory.CreateFromEnvironment();
        _audioPlayer = AudioPlayerFactory.Create();
        _voiceModelPath = Path.Combine(AppSettingsStore.DefaultDir, "whisper-base.bin");

        Opened += (_, _) =>
        {
            _ = RunInitialExplanationAsync();
            _ = PrewarmVoiceInputAsync(); // in parallel — voice-in being slow to warm up shouldn't delay the opening explanation
        };
    }

    private async Task PrewarmVoiceInputAsync()
    {
        try
        {
            _voiceInput = await TutorVoiceInputService.CreateAsync(_voiceModelPath);
        }
        catch (Exception ex)
        {
            // Voice-in is best-effort for this experiment — typed questions still work if this
            // fails (no model download, no microphone, etc.), so this never surfaces as a blocking
            // error, just a disabled mic button.
            Console.Error.WriteLine($"[ExplainItToMeWindow] voice input unavailable: {ex}");
            RecordButton.IsEnabled = false;
            RecordButton.Content = "🎤 Unavailable";
        }
    }

    private async Task RunInitialExplanationAsync()
    {
        SetBusy(true, "Thinking...");
        try
        {
            var reply = await _chat.ExplainAsync();
            AddTurn("Tutor", reply, isUser: false);
            await SpeakAsync(reply);
        }
        catch (Exception ex)
        {
            AddTurn("Error", $"Couldn't reach Claude: {ex.Message}", isUser: false, isError: true);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async void OnAskClick(object? sender, RoutedEventArgs e) => await AskTypedQuestionAsync();

    private async void OnQuestionBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await AskTypedQuestionAsync();
    }

    private async Task AskTypedQuestionAsync()
    {
        var question = QuestionBox.Text?.Trim();
        if (string.IsNullOrEmpty(question) || _busy) return;

        QuestionBox.Text = "";
        await AskAsync(question);
    }

    private void OnRecordPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_busy || _voiceInput is not { } voiceInput) return;

        voiceInput.StartRecording();
        RecordButton.Content = "🎤 Recording... release to send";
    }

    private async void OnRecordPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_voiceInput is not { IsRecording: true } voiceInput) return;

        RecordButton.Content = "🎤 Hold to talk";
        SetBusy(true, "Transcribing...");

        string transcript;
        try
        {
            transcript = await voiceInput.StopRecordingAndTranscribeAsync();
        }
        catch (Exception ex)
        {
            SetBusy(false, "");
            AddTurn("Error", $"Transcription failed: {ex.Message}", isUser: false, isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            SetBusy(false, "Didn't catch that — try again.");
            return;
        }

        await AskAsync(transcript);
    }

    private async Task AskAsync(string question)
    {
        AddTurn("You", question, isUser: true);
        SetBusy(true, "Thinking...");
        try
        {
            var reply = await _chat.AskFollowUpAsync(question);
            AddTurn("Tutor", reply, isUser: false);
            await SpeakAsync(reply);
        }
        catch (Exception ex)
        {
            AddTurn("Error", $"Couldn't reach Claude: {ex.Message}", isUser: false, isError: true);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    /// Narration is best-effort, same posture as NarratedWalkthroughController's own use of these
    /// two interfaces — a synthesis/playback failure (Piper not configured, no audio device) still
    /// leaves the text response fully readable, it just doesn't get spoken aloud.
    private async Task SpeakAsync(string text)
    {
        SetStatus("Speaking...");
        try
        {
            var wav = await _synthesizer.SynthesizeWavAsync(text);
            await _audioPlayer.PlayAsync(wav);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ExplainItToMeWindow] speech synthesis/playback failed: {ex}");
        }
        finally
        {
            SetStatus("");
        }
    }

    private void AddTurn(string label, string text, bool isUser, bool isError = false)
    {
        var color = isError ? Brushes.DarkRed : (isUser ? Brushes.DarkBlue : Brushes.Black);
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Bold, Foreground = color });
        panel.Children.Add(MathTextRenderer.Build(text, foreground: isError ? Brushes.DarkRed : Brushes.Black));
        ConversationPanel.Children.Add(panel);
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        QuestionBox.IsEnabled = !busy;
        AskButton.IsEnabled = !busy;
        RecordButton.IsEnabled = !busy && _voiceInput is not null;
        SetStatus(status);
    }

    private void SetStatus(string status) => StatusText.Text = status;
}
