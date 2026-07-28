using ApTutor.Narration;
using ApTutor.Platform;
using ApTutor.Scene;
using Xunit;

namespace ApTutor.Tests;

/// Fakes stand in for Piper/audio hardware, which this environment doesn't have — these tests
/// exercise the controller's stepping/narration/play-pause logic in isolation.
file sealed class FakeSynthesizer : IVoiceSynthesizer
{
    public List<string> Narrated { get; } = new();

    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken = default)
    {
        Narrated.Add(text);
        return Task.FromResult(new byte[] { 1 }); // non-empty so FakePlayer records it as "played"
    }
}

file sealed class FakePlayer : IAudioPlayer
{
    public int PlayCount { get; private set; }

    public Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes.Length > 0) PlayCount++;
        return Task.CompletedTask;
    }
}

file sealed class CallbackSynthesizer(Action onSynthesize) : IVoiceSynthesizer
{
    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken = default)
    {
        onSynthesize();
        return Task.FromResult(Array.Empty<byte>());
    }
}

public class NarratedWalkthroughControllerTests
{
    private static List<VisualStep> ThreeSteps() => new()
    {
        new VisualStep(0, "step zero", new SceneDelta(Array.Empty<SceneOp>())),
        new VisualStep(1, "step one", new SceneDelta(Array.Empty<SceneOp>())),
        new VisualStep(2, "step two", new SceneDelta(Array.Empty<SceneOp>())),
    };

    [Fact]
    public async Task StepForwardAsync_NarratesEachStepInOrder()
    {
        var synth = new FakeSynthesizer();
        var controller = new NarratedWalkthroughController(ThreeSteps(), synth, new FakePlayer());

        await controller.StepForwardAsync();
        await controller.StepForwardAsync();

        Assert.Equal(new[] { "step zero", "step one" }, synth.Narrated);
        Assert.Equal(1, controller.CurrentIndex);
    }

    [Fact]
    public async Task StepForwardAsync_WithoutNarrate_DoesNotSynthesize()
    {
        var synth = new FakeSynthesizer();
        var controller = new NarratedWalkthroughController(ThreeSteps(), synth, new FakePlayer());

        await controller.StepForwardAsync(narrate: false);

        Assert.Empty(synth.Narrated);
        Assert.Equal(0, controller.CurrentIndex);
    }

    [Fact]
    public void StepBack_NeverNarrates_AndAppliesNoFurtherThanTheStart()
    {
        var synth = new FakeSynthesizer();
        var controller = new NarratedWalkthroughController(ThreeSteps(), synth, new FakePlayer());

        controller.StepBack(); // no-op: nothing stepped forward yet
        Assert.Equal(-1, controller.CurrentIndex);
        Assert.Empty(synth.Narrated);
    }

    [Fact]
    public async Task PlayAsync_AutoAdvancesThroughAllSteps_ThenStops()
    {
        var synth = new FakeSynthesizer();
        var player = new FakePlayer();
        var controller = new NarratedWalkthroughController(ThreeSteps(), synth, player);

        await controller.PlayAsync();

        Assert.Equal(3, synth.Narrated.Count);
        Assert.Equal(3, player.PlayCount);
        Assert.Equal(2, controller.CurrentIndex);
        Assert.False(controller.IsPlaying);
        Assert.False(controller.CanStepForward);
    }

    [Fact]
    public async Task Pause_StopsAutoAdvanceBeforeReachingTheEnd()
    {
        var controller = default(NarratedWalkthroughController);
        var stepCount = 0;

        var synth = new CallbackSynthesizer(() =>
        {
            stepCount++;
            if (stepCount == 1) controller!.Pause();
        });

        controller = new NarratedWalkthroughController(ThreeSteps(), synth, new FakePlayer());

        await controller.PlayAsync();

        Assert.False(controller.IsPlaying);
        Assert.True(controller.CurrentIndex < 2); // paused before the walkthrough finished
    }
}
