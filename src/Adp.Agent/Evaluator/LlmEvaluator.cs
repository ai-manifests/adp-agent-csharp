using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Adp.Manifest;

namespace Adp.Agent.Evaluator;

/// <summary>
/// Calls an LLM provider (Anthropic / OpenAI) with structured-output forcing
/// to produce a guaranteed-shape <see cref="EvaluationResult"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <term>Anthropic</term>
///     <description>Uses tool_use forced output
///     (<c>tool_choice: { type: "tool", name: "submit_vote" }</c>) which is
///     the supported pattern for guaranteed JSON. The system prompt is marked
///     <c>cache_control: { type: "ephemeral" }</c> so identical system
///     prompts across actions hit the prompt cache.</description>
///   </item>
///   <item>
///     <term>OpenAI</term>
///     <description>Uses Structured Outputs
///     (<c>response_format: { type: "json_schema", strict: true }</c>).</description>
///   </item>
/// </list>
/// API keys come from environment variables — <c>ANTHROPIC_API_KEY</c> for
/// <c>provider: anthropic</c>, <c>OPENAI_API_KEY</c> for <c>provider: openai</c>.
/// They are intentionally not part of <see cref="EvaluatorConfig"/> so config
/// JSON can be committed without secrets.
/// </remarks>
public sealed class LlmEvaluator : IEvaluator
{
    private readonly AgentConfig _config;
    private readonly EvaluatorConfig _evalConfig;
    private readonly HttpClient _http;

    public LlmEvaluator(AgentConfig config, HttpClient? http = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _evalConfig = config.Evaluator
            ?? throw new InvalidOperationException(
                "LlmEvaluator requires AgentConfig.Evaluator to be configured.");
        if (_evalConfig.Kind != "llm")
            throw new InvalidOperationException(
                $"LlmEvaluator does not support evaluator kind '{_evalConfig.Kind}'.");
        _http = http ?? new HttpClient();
    }

    public async ValueTask<EvaluationResult> EvaluateAsync(
        EvaluationRequest request,
        CancellationToken ct = default)
    {
        var provider = _evalConfig.Provider;
        if (provider != "anthropic" && provider != "openai")
            return EvaluationResult.Abstain($"llm evaluator: unsupported provider '{provider ?? "<missing>"}'");
        if (string.IsNullOrEmpty(_evalConfig.Model))
            return EvaluationResult.Abstain("llm evaluator: model is required");
        if (string.IsNullOrEmpty(_evalConfig.SystemPrompt))
            return EvaluationResult.Abstain("llm evaluator: SystemPrompt is required");
        if (string.IsNullOrEmpty(_evalConfig.UserTemplate))
            return EvaluationResult.Abstain("llm evaluator: UserTemplate is required");

        var userMessage = RenderTemplate(_evalConfig.UserTemplate, request, _config);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_evalConfig.TimeoutMs));

        try
        {
            return provider == "anthropic"
                ? await CallAnthropicAsync(userMessage, timeoutCts.Token)
                : await CallOpenAiAsync(userMessage, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return EvaluationResult.Abstain("llm evaluator: timed out");
        }
        catch (Exception ex)
        {
            return EvaluationResult.Abstain($"llm evaluator ({provider}) failed: {ex.Message}");
        }
    }

    private async Task<EvaluationResult> CallAnthropicAsync(string userMessage, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("ANTHROPIC_API_KEY not set in environment");

        var body = new
        {
            model = _evalConfig.Model,
            max_tokens = _evalConfig.MaxTokens,
            temperature = _evalConfig.Temperature,
            system = new[] { new { type = "text", text = _evalConfig.SystemPrompt!, cache_control = new { type = "ephemeral" } } },
            tools = new[]
            {
                new
                {
                    name = "submit_vote",
                    description = "Submit your judgement on this action with confidence and dissent conditions.",
                    input_schema = VoteSchema(),
                },
            },
            tool_choice = new { type = "tool", name = "submit_vote" },
            messages = new[] { new { role = "user", content = userMessage } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"anthropic {(int)res.StatusCode}: {Truncate(text, 240)}");
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("anthropic: response had no content array");
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                typeEl.GetString() == "tool_use" &&
                block.TryGetProperty("input", out var input))
            {
                return ShapeFromRaw(input);
            }
        }
        throw new InvalidOperationException("anthropic: response had no tool_use block");
    }

    private async Task<EvaluationResult> CallOpenAiAsync(string userMessage, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY not set in environment");

        var body = new
        {
            model = _evalConfig.Model,
            temperature = _evalConfig.Temperature,
            max_completion_tokens = _evalConfig.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = _evalConfig.SystemPrompt! },
                new { role = "user", content = userMessage },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "submit_vote", schema = VoteSchema(), strict = true },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"openai {(int)res.StatusCode}: {Truncate(text, 240)}");
        }

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("openai: response had no choices");
        if (!choices[0].TryGetProperty("message", out var msg) ||
            !msg.TryGetProperty("content", out var contentEl) ||
            contentEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("openai: response had no message.content string");

        var contentStr = contentEl.GetString()!;
        try
        {
            using var inner = JsonDocument.Parse(contentStr);
            return ShapeFromRaw(inner.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"openai: response was not valid JSON: {Truncate(contentStr, 240)}");
        }
    }

    /// <summary>Render placeholders in <paramref name="template"/> against the action and agent context.</summary>
    public static string RenderTemplate(string template, EvaluationRequest request, AgentConfig config)
    {
        var paramsString = request.Action.Parameters is { Count: > 0 } parameters
            ? string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}"))
            : string.Empty;

        return template
            .Replace("{action.kind}", request.Action.Kind)
            .Replace("{action.target}", request.Action.Target)
            .Replace("{action.parameters}", paramsString)
            .Replace("{agent.id}", config.AgentId)
            .Replace("{agent.decisionClass}", request.DecisionClass);
    }

    /// <summary>JSON Schema describing a vote payload — used by both providers' structured-output forcing.</summary>
    public static object VoteSchema() => new
    {
        type = "object",
        properties = new
        {
            vote = new { type = "string", @enum = new[] { "approve", "reject", "abstain" } },
            confidence = new { type = "number", minimum = 0, maximum = 1 },
            summary = new { type = "string" },
            dissent_conditions = new { type = "array", items = new { type = "string" } },
            evidence_refs = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "vote", "confidence", "summary", "dissent_conditions", "evidence_refs" },
        additionalProperties = false,
    };

    private static EvaluationResult ShapeFromRaw(JsonElement raw)
    {
        var vote = NormaliseVote(raw.TryGetProperty("vote", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
        var confidence = raw.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
            ? Math.Clamp(c.GetDouble(), 0, 1)
            : 0.5;
        var summary = raw.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? string.Empty
            : string.Empty;

        IReadOnlyList<string>? evidenceRefs = null;
        if (raw.TryGetProperty("evidence_refs", out var er) && er.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in er.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            evidenceRefs = list;
        }

        return new EvaluationResult(vote, confidence, summary, evidenceRefs);
    }

    private static Vote NormaliseVote(string? raw) => raw switch
    {
        "approve" => Vote.Approve,
        "reject" => Vote.Reject,
        "abstain" => Vote.Abstain,
        _ => Vote.Abstain,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
