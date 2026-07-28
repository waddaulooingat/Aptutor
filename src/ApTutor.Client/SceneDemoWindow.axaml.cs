// PHASE2-HANDOFF.md UI wiring, adapted from WPF's SKElement to Avalonia's SceneCanvas, extended in
// Phase 4 with narration: Step/Back/Play drive a NarratedWalkthroughController, which owns
// stepping through the CS A module's real tracer-backed IStepProvider (node u2.1) and narrating
// each forward step via Piper (or silently no-ops if Piper isn't configured — see
// VoiceSynthesizerFactory). SceneState application stays here, driven by the controller's events.

using ApTutor.Narration;
using ApTutor.Platform;
using ApTutor.Scene;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ApTutor.Client;

public partial class SceneDemoWindow : Window
{
    private readonly SceneState _state = new();
    private readonly NarratedWalkthroughController _walkthrough;
    private readonly bool _narrationAvailable;

    public SceneDemoWindow(ICourseModule course)
    {
        InitializeComponent();
        Canvas.SceneState = _state;
        CaptionText.Text = "(start)";

        var steps = course.StepProvider.GetSteps("u2.1", "default");
        var synthesizer = VoiceSynthesizerFactory.CreateFromEnvironment();
        _narrationAvailable = synthesizer is not NullVoiceSynthesizer;
        _walkthrough = new NarratedWalkthroughController(steps, synthesizer, AudioPlayerFactory.Create());
        _walkthrough.StepApplied += OnStepApplied;
        _walkthrough.SteppedBack += OnSteppedBack;
        _walkthrough.PlaybackStateChanged += OnPlaybackStateChanged;

        NarrationStatusText.Text = _narrationAvailable
            ? ""
            : $"(narration off — set {VoiceSynthesizerFactory.ExecutablePathVar}/{VoiceSynthesizerFactory.ModelPathVar}/{VoiceSynthesizerFactory.ConfigPathVar} to enable)";
        UpdateButtons();
    }

    private void OnStepApplied(VisualStep step)
    {
        _state.ClearFlashes();
        _state.Apply(step.Delta);
        CaptionText.Text = step.Caption;
        Canvas.InvalidateVisual();
        UpdateButtons();
    }

    private void OnSteppedBack()
    {
        _state.StepBack();
        CaptionText.Text = _walkthrough.CurrentIndex >= 0 ? CaptionText.Text : "(start)";
        Canvas.InvalidateVisual();
        UpdateButtons();
    }

    private void OnPlaybackStateChanged()
    {
        PlayPauseButton.Content = _walkthrough.IsPlaying ? "⏸ Pause" : "▶▶ Play narrated";
        NarrationStatusText.Text = _walkthrough.IsPlaying && _narrationAvailable ? "🔊 narrating…" : "";
        UpdateButtons();
    }

    private async void OnStepClick(object? sender, RoutedEventArgs e) => await _walkthrough.StepForwardAsync();

    private void OnBackClick(object? sender, RoutedEventArgs e) => _walkthrough.StepBack();

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_walkthrough.IsPlaying)
            _walkthrough.Pause();
        else
            await _walkthrough.PlayAsync();
    }

    private void UpdateButtons()
    {
        StepButton.IsEnabled = _walkthrough.CanStepForward && !_walkthrough.IsPlaying;
        BackButton.IsEnabled = _walkthrough.CanStepBack && !_walkthrough.IsPlaying;
        PlayPauseButton.IsEnabled = _walkthrough.CanStepForward || _walkthrough.IsPlaying;
    }
}
