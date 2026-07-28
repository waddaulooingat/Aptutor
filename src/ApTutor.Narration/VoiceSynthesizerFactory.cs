// Piper's binary + voice model (tens of MB, native shared libraries) are deliberately NOT
// vendored into this repo — that belongs in the installer (build-plan.md Phase 12: "Packaging,
// signing, licensing"), which is also where a properly-licensed, production-quality voice gets
// picked (the one used for development testing came from an old Piper release for reachability
// reasons in a sandboxed environment and is not vetted for shipping - see the Phase 4 handoff
// notes). Until then, callers point at a local Piper install via environment variables.

namespace ApTutor.Narration;

public static class VoiceSynthesizerFactory
{
    public const string ExecutablePathVar = "APTUTOR_PIPER_EXE";
    public const string ModelPathVar = "APTUTOR_PIPER_MODEL";
    public const string ConfigPathVar = "APTUTOR_PIPER_CONFIG";
    public const string EspeakDataPathVar = "APTUTOR_PIPER_ESPEAK_DATA";

    /// Real Piper synthesis if fully configured and the files exist; otherwise a silent
    /// NullVoiceSynthesizer so the app still runs (narration just no-ops) rather than crashing.
    public static IVoiceSynthesizer CreateFromEnvironment()
    {
        var options = TryReadOptionsFromEnvironment();
        return options is { IsAvailable: true } ? new PiperVoiceSynthesizer(options) : new NullVoiceSynthesizer();
    }

    public static PiperOptions? TryReadOptionsFromEnvironment()
    {
        var exe = Environment.GetEnvironmentVariable(ExecutablePathVar);
        var model = Environment.GetEnvironmentVariable(ModelPathVar);
        var config = Environment.GetEnvironmentVariable(ConfigPathVar);
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(config))
            return null;

        return new PiperOptions(exe, model, config, Environment.GetEnvironmentVariable(EspeakDataPathVar));
    }
}
