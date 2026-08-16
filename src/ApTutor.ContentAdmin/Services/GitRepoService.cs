using System.Diagnostics;
using System.Text;

namespace ApTutor.ContentAdmin.Services;

public sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public sealed class GitOperationException : Exception
{
    public GitOperationException(string message) : base(message) { }
}

/// Owns the single local clone this app mutates. Every repo-mutating operation (approve's commit,
/// the rejection-log commit) goes through CommitAndPushAsync, serialized behind one semaphore —
/// git operates on the whole clone's index/HEAD/remote-tracking branch, not per-file, so two
/// concurrent approvals must never interleave.
///
/// The PAT is never written to disk. It is NOT embedded in the remote URL (that would persist it
/// in plaintext in .git/config, readable by anything with container filesystem access — a real
/// regression against "backend holds all credentials, nothing leaks"). Instead it's injected as a
/// one-off `-c http.extraHeader` value on each invocation that needs to talk to the remote, read
/// from an environment variable at call time only.
public sealed class GitRepoService
{
    private const int MaxPushAttempts = 4;

    private readonly string _cloneDir;
    private readonly string _branch;
    private readonly ILogger<GitRepoService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitRepoService(IConfiguration config, ILogger<GitRepoService> logger)
    {
        var section = config.GetSection("ContentAdmin");
        _cloneDir = section["RepoCloneDir"]
            ?? throw new InvalidOperationException("ContentAdmin:RepoCloneDir is not configured.");
        _branch = section["RepoBranch"]
            ?? throw new InvalidOperationException("ContentAdmin:RepoBranch is not configured.");
        _logger = logger;
    }

    /// Absolute path to the local working copy. Course DAG/content paths are resolved against this.
    public string CloneDir => _cloneDir;

    public string Branch => _branch;

    /// Clones on first run (needs CONTENT_ADMIN_REPO_URL set), or fetches + hard-resets to the
    /// tracked branch's current tip on every later call — keeps the local working copy from
    /// drifting from what human developers have pushed since this process last looked.
    public async Task EnsureUpToDateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(_cloneDir, ".git")))
            {
                var remoteUrl = Environment.GetEnvironmentVariable("CONTENT_ADMIN_REPO_URL");
                if (string.IsNullOrWhiteSpace(remoteUrl))
                    throw new InvalidOperationException(
                        $"No git clone found at '{_cloneDir}' and CONTENT_ADMIN_REPO_URL is not set to create one.");

                Directory.CreateDirectory(_cloneDir);
                var parent = Directory.GetParent(_cloneDir)?.FullName
                    ?? throw new InvalidOperationException($"'{_cloneDir}' has no parent directory.");
                await RunOrThrowAsync(parent,
                    new[] { "clone", "--branch", _branch, "--single-branch", remoteUrl, _cloneDir }, usePat: true, ct);
                await ConfigureIdentityAsync(ct);
                return;
            }

            await RunOrThrowAsync(_cloneDir, new[] { "fetch", "origin", _branch }, usePat: true, ct);
            await RunOrThrowAsync(_cloneDir, new[] { "checkout", _branch }, usePat: false, ct);
            await RunOrThrowAsync(_cloneDir, new[] { "reset", "--hard", $"origin/{_branch}" }, usePat: false, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Stages the given paths (relative to CloneDir), commits, and pushes to the tracked branch.
    /// No-ops quietly (no commit, no push) if nothing is actually staged — this makes the whole
    /// pipeline safe to call from an already-idempotent caller without duplicating the check, and
    /// avoids `git commit`'s "nothing to commit" non-zero exit ever surfacing as a false failure.
    /// Retries a bounded number of times on a non-fast-forward push rejection: this app pushes to
    /// the same branch human developers commit to, so a concurrent push is a real race, not a
    /// hypothetical one.
    public async Task CommitAndPushAsync(IReadOnlyList<string> relativePaths, string commitMessage, CancellationToken ct = default)
    {
        if (relativePaths.Count == 0)
            throw new ArgumentException("At least one path must be given.", nameof(relativePaths));

        await _gate.WaitAsync(ct);
        try
        {
            await RunOrThrowAsync(_cloneDir, new[] { "add" }.Concat(relativePaths).ToArray(), usePat: false, ct);

            var staged = await RunOrThrowAsync(_cloneDir, new[] { "diff", "--cached", "--name-only" }, usePat: false, ct);
            if (string.IsNullOrWhiteSpace(staged.StdOut))
            {
                _logger.LogInformation("Nothing staged for [{Paths}]; skipping commit.", string.Join(", ", relativePaths));
                return;
            }

            await RunOrThrowAsync(_cloneDir, new[] { "commit", "-m", commitMessage }, usePat: false, ct);

            for (var attempt = 1; attempt <= MaxPushAttempts; attempt++)
            {
                var push = await RunAsync(_cloneDir, new[] { "push", "origin", $"HEAD:{_branch}" }, usePat: true, ct);
                if (push.Success) return;

                if (attempt == MaxPushAttempts)
                {
                    _logger.LogError("git push failed after {Attempts} attempts: {StdErr}", MaxPushAttempts, push.StdErr);
                    throw new GitOperationException("Push failed after repeated retries.");
                }

                _logger.LogWarning(
                    "Push rejected (attempt {Attempt}/{Max}); rebasing onto origin/{Branch} and retrying. {StdErr}",
                    attempt, MaxPushAttempts, _branch, push.StdErr);

                await RunOrThrowAsync(_cloneDir, new[] { "fetch", "origin", _branch }, usePat: true, ct);
                var rebase = await RunAsync(_cloneDir, new[] { "rebase", $"origin/{_branch}" }, usePat: false, ct);
                if (!rebase.Success)
                {
                    await RunAsync(_cloneDir, new[] { "rebase", "--abort" }, usePat: false, ct);
                    _logger.LogError("Rebase onto origin/{Branch} failed, aborted: {StdErr}", _branch, rebase.StdErr);
                    throw new GitOperationException($"Could not reconcile with origin/{_branch}.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConfigureIdentityAsync(CancellationToken ct)
    {
        var name = Environment.GetEnvironmentVariable("CONTENT_ADMIN_COMMIT_NAME") ?? "Tutor AI Content Admin";
        var email = Environment.GetEnvironmentVariable("CONTENT_ADMIN_COMMIT_EMAIL") ?? "content-admin@localhost";
        await RunOrThrowAsync(_cloneDir, new[] { "config", "user.name", name }, usePat: false, ct);
        await RunOrThrowAsync(_cloneDir, new[] { "config", "user.email", email }, usePat: false, ct);
    }

    private async Task<GitCommandResult> RunOrThrowAsync(string workingDir, IReadOnlyList<string> args, bool usePat, CancellationToken ct)
    {
        var result = await RunAsync(workingDir, args, usePat, ct);
        if (!result.Success)
        {
            _logger.LogError("git {Args} failed (exit {ExitCode}): {StdErr}", string.Join(' ', args), result.ExitCode, result.StdErr);
            throw new GitOperationException($"git {args[0]} failed: {result.StdErr}");
        }
        return result;
    }

    private static async Task<GitCommandResult> RunAsync(string workingDir, IReadOnlyList<string> args, bool usePat, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (usePat)
        {
            var pat = Environment.GetEnvironmentVariable("CONTENT_ADMIN_REPO_PAT");
            if (!string.IsNullOrWhiteSpace(pat))
            {
                // GitHub accepts any username with the PAT as the password over HTTPS Basic auth.
                // Read from the environment fresh on every call and passed only as an in-memory git
                // config override for this one invocation — never persisted to .git/config.
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{pat}"));
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add($"http.extraHeader=AUTHORIZATION: basic {basic}");
            }
        }

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new GitCommandResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
