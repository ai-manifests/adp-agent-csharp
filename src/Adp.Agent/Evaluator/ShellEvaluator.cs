using System.Diagnostics;
using System.Text.Json;
using Adp.Manifest;

namespace Adp.Agent.Evaluator;

/// <summary>
/// Default <see cref="IEvaluator"/> that runs an external shell command
/// and interprets the result as a vote. Configured via
/// <see cref="EvaluatorConfig"/> on <see cref="AgentConfig"/>.
/// </summary>
/// <remarks>
/// Two parse modes, selected by <see cref="EvaluatorConfig.ParseOutput"/>:
/// <list type="bullet">
///   <item>
///     <term><c>exit-code</c> (default)</term>
///     <description>Exit 0 → Approve at <see cref="AgentConfig.DefaultConfidence"/>;
///     any non-zero exit → Reject at the same confidence. Stdout is captured
///     into the rationale. Useful for wrapping existing test scripts.</description>
///   </item>
///   <item>
///     <term><c>json</c></term>
///     <description>Expects stdout to contain a JSON object with at least
///     <c>vote</c> (string) and <c>confidence</c> (number). Optional
///     <c>rationale</c> (string) and <c>evidenceRefs</c> (string array)
///     are preserved on the resulting proposal.</description>
///   </item>
/// </list>
/// <para>
/// The shell command runs with a timeout of
/// <see cref="EvaluatorConfig.TimeoutMs"/>. If it times out the evaluator
/// returns an abstain with rationale <c>"evaluator timed out"</c>; this
/// matches the TypeScript runtime's behavior and is the reason
/// <c>tests/unit/evaluator.test.ts</c> has a "timeout produces abstain" case.
/// </para>
/// </remarks>
public sealed class ShellEvaluator : IEvaluator
{
    private readonly AgentConfig _config;
    private readonly EvaluatorConfig _evalConfig;

    public ShellEvaluator(AgentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _evalConfig = config.Evaluator
            ?? throw new InvalidOperationException(
                "ShellEvaluator requires AgentConfig.Evaluator to be configured.");
        if (_evalConfig.Kind != "shell")
            throw new InvalidOperationException(
                $"ShellEvaluator does not support evaluator kind '{_evalConfig.Kind}'.");
        if (string.IsNullOrWhiteSpace(_evalConfig.Command))
            throw new InvalidOperationException(
                "ShellEvaluator requires EvaluatorConfig.Command to be set.");
    }

    /// <inheritdoc />
    public async ValueTask<EvaluationResult> EvaluateAsync(
        EvaluationRequest request,
        CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows()
                    ? $"/c {_evalConfig.Command}"
                    : $"-c \"{_evalConfig.Command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["ADP_DELIBERATION_ID"] = request.DeliberationId,
                    ["ADP_ACTION_KIND"] = request.Action.Kind,
                    ["ADP_ACTION_TARGET"] = request.Action.Target,
                    ["ADP_REVERSIBILITY_TIER"] = request.Tier.ToString(),
                    ["ADP_DECISION_CLASS"] = request.DecisionClass,
                },
            },
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return EvaluationResult.Abstain($"evaluator failed to start: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_evalConfig.TimeoutMs));

        string stdout;
        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return EvaluationResult.Abstain("evaluator timed out");
        }

        return _evalConfig.ParseOutput switch
        {
            "exit-code" => ParseExitCode(process.ExitCode, stdout, stderr),
            "json" => ParseJson(stdout, stderr),
            _ => EvaluationResult.Abstain(
                $"unknown ParseOutput mode '{_evalConfig.ParseOutput}'"),
        };
    }

    private EvaluationResult ParseExitCode(int exitCode, string stdout, string stderr)
    {
        var rationale = string.IsNullOrWhiteSpace(stdout)
            ? (string.IsNullOrWhiteSpace(stderr) ? $"exit {exitCode}" : stderr.Trim())
            : stdout.Trim();

        return exitCode == 0
            ? new EvaluationResult(Vote.Approve, _config.DefaultConfidence, rationale)
            : new EvaluationResult(Vote.Reject, _config.DefaultConfidence, rationale);
    }

    private EvaluationResult ParseJson(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return EvaluationResult.Abstain(
                string.IsNullOrWhiteSpace(stderr) ? "evaluator produced empty output" : stderr.Trim());
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            return EvaluationResult.Abstain($"evaluator output was not valid JSON: {ex.Message}");
        }

        using var _ = doc;
        var root = doc.RootElement;

        if (!root.TryGetProperty("vote", out var voteEl) || voteEl.ValueKind != JsonValueKind.String)
            return EvaluationResult.Abstain("evaluator JSON missing 'vote' field");

        var voteStr = voteEl.GetString();
        var vote = voteStr?.ToLowerInvariant() switch
        {
            "approve" => Vote.Approve,
            "reject" => Vote.Reject,
            "abstain" => Vote.Abstain,
            _ => (Vote?)null,
        };
        if (vote is null)
            return EvaluationResult.Abstain($"evaluator JSON had unknown vote '{voteStr}'");

        var confidence = _config.DefaultConfidence;
        if (root.TryGetProperty("confidence", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
            confidence = cEl.GetDouble();

        var rationale = "evaluator output";
        if (root.TryGetProperty("rationale", out var rEl) && rEl.ValueKind == JsonValueKind.String)
            rationale = rEl.GetString() ?? rationale;

        IReadOnlyList<string>? evidenceRefs = null;
        if (root.TryGetProperty("evidenceRefs", out var eEl) && eEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var el in eEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                    list.Add(el.GetString()!);
            }
            evidenceRefs = list;
        }

        return new EvaluationResult(vote.Value, confidence, rationale, evidenceRefs);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* process already exited or can't be killed — ignore */ }
    }
}

/// <summary>
/// A trivial evaluator that returns <see cref="AgentConfig.DefaultVote"/>
/// at <see cref="AgentConfig.DefaultConfidence"/> for every request. Used
/// when no <see cref="EvaluatorConfig"/> is supplied, and as a drop-in
/// stub during development.
/// </summary>
public sealed class StaticEvaluator : IEvaluator
{
    private readonly AgentConfig _config;

    public StaticEvaluator(AgentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public ValueTask<EvaluationResult> EvaluateAsync(EvaluationRequest request, CancellationToken ct = default) =>
        ValueTask.FromResult(new EvaluationResult(
            Vote: _config.DefaultVote,
            Confidence: _config.DefaultConfidence,
            Rationale: "static default vote"));
}
