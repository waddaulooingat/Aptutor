// PHASE2-HANDOFF.md UI wiring, adapted from WPF's SKElement to Avalonia's SceneCanvas:
// "Step" applies the next fixture delta and invalidates; "Back" steps the SceneState back
// (guarded on CanStepBack) and invalidates. Feeds the hand-written reference-vs-value fixture —
// the real tracer arrives in Phase 3.

using ApTutor.Scene;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ApTutor.Client;

public partial class SceneDemoWindow : Window
{
    private readonly SceneState _state = new();
    private int _nextStepIndex;

    public SceneDemoWindow()
    {
        InitializeComponent();
        Canvas.SceneState = _state;
        CaptionText.Text = "(start)";
        UpdateButtons();
    }

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (_nextStepIndex >= ReferenceVsValueDemo.Steps.Count) return;

        var (caption, delta) = ReferenceVsValueDemo.Steps[_nextStepIndex];
        _state.ClearFlashes();
        _state.Apply(delta);
        _nextStepIndex++;

        CaptionText.Text = caption;
        Canvas.InvalidateVisual();
        UpdateButtons();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (!_state.CanStepBack) return;

        _state.StepBack();
        _nextStepIndex--;

        CaptionText.Text = _nextStepIndex > 0
            ? ReferenceVsValueDemo.Steps[_nextStepIndex - 1].Caption
            : "(start)";
        Canvas.InvalidateVisual();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        StepButton.IsEnabled = _nextStepIndex < ReferenceVsValueDemo.Steps.Count;
        BackButton.IsEnabled = _state.CanStepBack;
    }
}
