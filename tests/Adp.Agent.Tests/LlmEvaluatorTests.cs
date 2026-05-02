using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Adp.Agent.Evaluator;
using Adp.Manifest;
using Xunit;

namespace Adp.Agent.Tests;

/// <summary>
/// Tests for <see cref="LlmEvaluator"/>: prompt-template substitution, both
/// providers' structured-output paths, missing API keys, and HTTP-error
/// fallback. No live API calls — provider HTTP is faked via
/// <see cref="MockHttpHandler"/>.
/// </summary>
public class LlmEvaluatorTests
{
    private static AgentConfig BuildConfig(string provider, string? prompt = null, string? template = null) =>
        new()
        {
            AgentId = "did:adp:claude-tester-v1",
            Port = 3011,
            Domain = "claude-tester.adp-federation.dev",
            DecisionClasses = ImmutableList.Create("code.correctness"),
            Authorities = ImmutableDictionary<string, double>.Empty.Add("code.correctness", 0.8),
            StakeMagnitude = StakeMagnitude.High,
            DefaultVote = Vote.Abstain,
            DefaultConfidence = 0.5,
            DissentConditions = ImmutableList<string>.Empty,
            JournalDir = "/tmp/x",
            Auth = new AuthConfig { BearerToken = "x" },
            Evaluator = new EvaluatorConfig
            {
                Kind = "llm",
                Provider = provider,
                Model = provider == "anthropic" ? "claude-opus-4-7" : "gpt-5",
                SystemPrompt = prompt ?? "You evaluate code correctness.",
                UserTemplate = template ?? "Vote on {action.target} (you are {agent.id})",
                TimeoutMs = 30_000,
            },
        };

    private static EvaluationRequest BuildRequest() =>
        new(
            DeliberationId: "dlb_test_1",
            Action: new ProposalAction("merge_pull_request", "ai-manifests/adp-dogfood#5", null),
            Tier: ReversibilityTier.PartiallyReversible,
            DecisionClass: "code.correctness");

    [Fact]
    public void RenderTemplate_SubstitutesAllPlaceholders()
    {
        var config = BuildConfig("anthropic");
        var req = new EvaluationRequest(
            "dlb_x",
            new ProposalAction("merge_pull_request", "foo/bar#1", ImmutableDictionary<string, string>.Empty.Add("branch", "main")),
            ReversibilityTier.PartiallyReversible,
            "code.correctness");

        var result = LlmEvaluator.RenderTemplate(
            "Action {action.kind} on {action.target} with {action.parameters}; you are {agent.id} judging {agent.decisionClass}.",
            req, config);

        Assert.Equal(
            "Action merge_pull_request on foo/bar#1 with branch=main; you are did:adp:claude-tester-v1 judging code.correctness.",
            result);
    }

    [Fact]
    public async Task UnsupportedProvider_AbstainsWithExplicitMessage()
    {
        var config = BuildConfig("ollama");
        var evaluator = new LlmEvaluator(config);
        var result = await evaluator.EvaluateAsync(BuildRequest());
        Assert.Equal(Vote.Abstain, result.Vote);
        Assert.Contains("unsupported provider", result.Rationale);
    }

    [Fact]
    public async Task MissingSystemPrompt_AbstainsWithExplicitMessage()
    {
        var config = BuildConfig("anthropic", prompt: "");
        var evaluator = new LlmEvaluator(config);
        var result = await evaluator.EvaluateAsync(BuildRequest());
        Assert.Equal(Vote.Abstain, result.Vote);
        Assert.Contains("SystemPrompt", result.Rationale);
    }

    [Fact]
    public async Task Anthropic_HappyPath_ParsesToolUseBlock()
    {
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        try
        {
            string? capturedUri = null;
            string? capturedBody = null;
            var handler = new MockHttpHandler(async (req, _) =>
            {
                capturedUri = req.RequestUri!.ToString();
                capturedBody = await req.Content!.ReadAsStringAsync();
                var body = """
                    {
                      "content": [
                        { "type": "text", "text": "thinking…" },
                        {
                          "type": "tool_use",
                          "name": "submit_vote",
                          "input": {
                            "vote": "reject",
                            "confidence": 0.91,
                            "summary": "Tests are missing.",
                            "dissent_conditions": ["no test added"],
                            "evidence_refs": ["ci.log"]
                          }
                        }
                      ]
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            });

            var evaluator = new LlmEvaluator(BuildConfig("anthropic"), new HttpClient(handler));
            var result = await evaluator.EvaluateAsync(BuildRequest());

            Assert.Equal(Vote.Reject, result.Vote);
            Assert.InRange(result.Confidence, 0.90, 0.92);
            Assert.Equal("Tests are missing.", result.Rationale);
            Assert.NotNull(result.EvidenceRefs);
            Assert.Single(result.EvidenceRefs!);

            Assert.Equal("https://api.anthropic.com/v1/messages", capturedUri);
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("submit_vote", doc.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
            Assert.Equal("ephemeral",
                doc.RootElement.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task Anthropic_NoToolUseBlock_AbstainsWithExplicitMessage()
    {
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        try
        {
            var handler = new MockHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "content": [ { "type": "text", "text": "I refuse." } ] }""",
                    Encoding.UTF8, "application/json"),
            }));
            var evaluator = new LlmEvaluator(BuildConfig("anthropic"), new HttpClient(handler));
            var result = await evaluator.EvaluateAsync(BuildRequest());
            Assert.Equal(Vote.Abstain, result.Vote);
            Assert.Contains("tool_use", result.Rationale);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task Anthropic_HttpError_AbstainsWithStatus()
    {
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        try
        {
            var handler = new MockHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("rate limited", Encoding.UTF8, "text/plain"),
            }));
            var evaluator = new LlmEvaluator(BuildConfig("anthropic"), new HttpClient(handler));
            var result = await evaluator.EvaluateAsync(BuildRequest());
            Assert.Equal(Vote.Abstain, result.Vote);
            Assert.Contains("anthropic 429", result.Rationale);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task Anthropic_MissingApiKey_AbstainsWithExplicitMessage()
    {
        var oldKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null!);
        try
        {
            var evaluator = new LlmEvaluator(BuildConfig("anthropic"));
            var result = await evaluator.EvaluateAsync(BuildRequest());
            Assert.Equal(Vote.Abstain, result.Vote);
            Assert.Contains("ANTHROPIC_API_KEY", result.Rationale);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task OpenAi_HappyPath_ParsesContentJson()
    {
        var oldKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var inner = """{"vote":"approve","confidence":0.78,"summary":"Looks good","dissent_conditions":[],"evidence_refs":[]}""";
            var outer = $$"""
                { "choices": [ { "message": { "content": {{JsonSerializer.Serialize(inner)}} } } ] }
                """;
            string? capturedBody = null;
            var handler = new MockHttpHandler(async (req, _) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(outer, Encoding.UTF8, "application/json"),
                };
            });

            var evaluator = new LlmEvaluator(BuildConfig("openai"), new HttpClient(handler));
            var result = await evaluator.EvaluateAsync(BuildRequest());

            Assert.Equal(Vote.Approve, result.Vote);
            Assert.InRange(result.Confidence, 0.77, 0.79);

            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("json_schema", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.True(doc.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldKey);
        }
    }

    [Fact]
    public async Task OpenAi_InvalidJsonContent_AbstainsWithExplicitMessage()
    {
        var oldKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var outer = """{ "choices": [ { "message": { "content": "not json" } } ] }""";
            var handler = new MockHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(outer, Encoding.UTF8, "application/json"),
            }));
            var evaluator = new LlmEvaluator(BuildConfig("openai"), new HttpClient(handler));
            var result = await evaluator.EvaluateAsync(BuildRequest());
            Assert.Equal(Vote.Abstain, result.Vote);
            Assert.Contains("not valid JSON", result.Rationale);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldKey);
        }
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _handler(request, ct);
    }
}
