// AP Tutor — Phase 4: guided walkthroughs. Steps through VisualStep[] (platform-level, course-
// agnostic — build-plan.md: "narrate each VisualStep.Caption"), narrating forward steps and
// exposing Play/Pause/Step/Back for the shell to drive. Deliberately has no dependency on
// ApTutor.Scene or any UI framework: it hands VisualSteps to the caller via events and lets the
// caller own SceneState/rendering, so this is unit-testable with fake synthesizer/player and no
// audio hardware.

using ApTutor.Platform;

namespace ApTutor.Narration;

public sealed class NarratedWalkthroughController
{
    private readonly IReadOnlyList<VisualStep> _steps;
    private readonly IVoiceSynthesizer _synthesizer;
    private readonly IAudioPlayer _player;
    private CancellationTokenSource? _playCts;

    public NarratedWalkthroughController(IReadOnlyList<VisualStep> steps, IVoiceSynthesizer synthesizer, IAudioPlayer player)
    {
        _steps = steps;
        _synthesizer = synthesizer;
        _player = player;
    }

    /// -1 = before the first step (nothing applied yet).
    public int CurrentIndex { get; private set; } = -1;

    public bool IsPlaying { get; private set; }
    public bool CanStepForward => CurrentIndex + 1 < _steps.Count;
    public bool CanStepBack => CurrentIndex >= 0;

    /// Fires the moment a forward step is applied — *before* narration starts, so a UI handler
    /// can update the highlight first and let the caption's audio follow it, per build-plan.md's
    /// "per-step audio synced to the highlight."
    public event Action<VisualStep>? StepApplied;

    public event Action? SteppedBack;
    public event Action? PlaybackStateChanged;

    public async Task StepForwardAsync(bool narrate = true, CancellationToken cancellationToken = default)
    {
        if (!CanStepForward) return;

        CurrentIndex++;
        var step = _steps[CurrentIndex];
        StepApplied?.Invoke(step);

        if (!narrate) return;
        var wav = await _synthesizer.SynthesizeWavAsync(step.Caption, cancellationToken);
        await _player.PlayAsync(wav, cancellationToken);
    }

    /// Stepping back is a scrub action, not a re-teach — narration only ever plays going forward.
    public void StepBack()
    {
        if (!CanStepBack) return;
        CurrentIndex--;
        SteppedBack?.Invoke();
    }

    /// Auto-advances with narration until the end of the walkthrough or Pause() is called.
    public async Task PlayAsync(CancellationToken externalCancellationToken = default)
    {
        if (IsPlaying) return;

        IsPlaying = true;
        PlaybackStateChanged?.Invoke();
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        try
        {
            while (CanStepForward)
            {
                _playCts.Token.ThrowIfCancellationRequested();
                await StepForwardAsync(narrate: true, _playCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Pause() was called (or the external token fired) — not an error.
        }
        finally
        {
            IsPlaying = false;
            _playCts = null;
            PlaybackStateChanged?.Invoke();
        }
    }

    public void Pause() => _playCts?.Cancel();
}
