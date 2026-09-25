namespace ApTutor.ContentAdmin.Services;

/// Configuration for the AI Content Agent interim stopgap (see the AI-content-agent handoff) — a
/// single switch covering BOTH halves of the pipeline (autonomous gap-scan-and-generate, and the AI
/// review pass that resolves any generation completion, scan-triggered or manual) since the handoff
/// frames them as one interim pipeline to be turned on/off together, not two independent features.
/// Enabled defaults false: deploying this code must never silently start auto-approving content —
/// a human operator has to explicitly opt in via appsettings/environment once they've decided to
/// accept the trust-tier tradeoff, and can flip it back off the moment a real human SME is in place —
/// a config change and restart, same as any other setting here (ContentStore:Bucket etc.), never a
/// code change.
public sealed class AiContentAgentOptions
{
    public bool Enabled { get; set; } = false;

    /// Sanity cap on how many generation calls one scan-and-fill pass can trigger — a scan across a
    /// whole course could otherwise find dozens of gaps and fire that many billed API calls from one
    /// click with no chance to reconsider. A capped pass simply leaves the rest as gaps for the next
    /// run rather than refusing outright.
    public int MaxGenerationsPerScan { get; set; } = 20;
}
