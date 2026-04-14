using Adp.Agent.Deliberation;
using Adp.Agent.Evaluator;
using Adp.Agent.Journal;
using Adp.Agent.Mcp;
using Adp.Agent.Middleware;
using Adp.Agent.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Adp.Agent;

/// <summary>
/// The entry-point class adopters instantiate to run an ADP agent on the
/// C# / .NET 10 runtime. Mirrors the TypeScript <c>AdpAgent</c> class.
/// </summary>
/// <remarks>
/// <para>
/// Minimal usage:
/// </para>
/// <code>
/// var config = new AgentConfig
/// {
///     AgentId = "did:adp:my-agent-v1",
///     Port = 3000,
///     Domain = "my-agent.example.com",
///     // ... etc
/// };
/// var host = new AdpAgentHost(config);
/// await host.StartAsync();
/// </code>
/// <para>
/// Advanced usage: pass a custom <see cref="IEvaluator"/>, a custom
/// <see cref="IRuntimeJournalStore"/>, or register lifecycle hooks via
/// <see cref="AfterStart"/> and <see cref="BeforeStop"/>. The built
/// <see cref="WebApplication"/> is available via <see cref="App"/> for
/// adopters who want to mount their own additional routes.
/// </para>
/// </remarks>
public sealed class AdpAgentHost
{
    private readonly AgentConfig _config;
    private readonly IRuntimeJournalStore _journal;
    private readonly IEvaluator _evaluator;
    private readonly WebApplication _app;
    private readonly List<Func<Task>> _afterStart = new();
    private readonly List<Func<Task>> _beforeStop = new();
    private bool _started;

    public AdpAgentHost(AgentConfig config, AdpAgentHostOptions? options = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        options ??= new AdpAgentHostOptions();

        _journal = options.Journal ?? BuildDefaultJournal(_config);
        _evaluator = options.Evaluator ?? BuildDefaultEvaluator(_config);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{_config.Port}");

        // Register DI services — adopters can resolve these from their own routes.
        builder.Services.AddSingleton(_config);
        builder.Services.AddSingleton(_journal);
        builder.Services.AddSingleton(_evaluator);
        builder.Services.AddSingleton<RuntimeDeliberation>();

        // Allow options.ConfigureServices to add custom services on top.
        options.ConfigureServices?.Invoke(builder.Services);

        _app = builder.Build();

        // Middleware pipeline.
        _app.UseAdpRateLimit();
        _app.UseAdpAuth(_config);

        // Route groups.
        var runtime = _app.Services.GetRequiredService<RuntimeDeliberation>();
        _app.MapAdpManifestEndpoints(_config, _journal);
        _app.MapAdjQueryEndpoints(_journal);
        _app.MapAdpDeliberationEndpoints(_config, _journal, runtime);
        _app.MapAcbEndpoints(_config, _journal);
        _app.MapAdpMcpEndpoints(_config, _journal);

        // Adopter routes (e.g. webhook handlers) can be added before Start.
        options.ConfigureRoutes?.Invoke(_app);
    }

    /// <summary>The underlying <see cref="WebApplication"/>. Mount additional routes on it before calling Start.</summary>
    public WebApplication App => _app;

    /// <summary>The journal store this host is persisting to.</summary>
    public IRuntimeJournalStore Journal => _journal;

    /// <summary>The active config (readonly).</summary>
    public AgentConfig Config => _config;

    /// <summary>Register a callback to run after the host has started listening.</summary>
    public AdpAgentHost AfterStart(Func<Task> callback)
    {
        _afterStart.Add(callback);
        return this;
    }

    /// <summary>Register a callback to run before the host shuts down.</summary>
    public AdpAgentHost BeforeStop(Func<Task> callback)
    {
        _beforeStop.Add(callback);
        return this;
    }

    /// <summary>Start the HTTP server. Returns after the server is listening and all after-start hooks have fired.</summary>
    public async Task StartAsync()
    {
        if (_started) throw new InvalidOperationException("AdpAgentHost already started.");
        _started = true;

        await _app.StartAsync();

        Console.WriteLine($"[{_config.AgentId}] listening on :{_config.Port}");
        Console.WriteLine($"  manifest:    http://localhost:{_config.Port}/.well-known/adp-manifest.json");
        Console.WriteLine($"  calibration: http://localhost:{_config.Port}/.well-known/adp-calibration.json");
        Console.WriteLine($"  journal:     http://localhost:{_config.Port}/adj/v0/");

        foreach (var hook in _afterStart)
        {
            await hook();
        }
    }

    /// <summary>Run the host and block until process shutdown. The typical <c>await host.RunAsync()</c> pattern.</summary>
    public async Task RunAsync()
    {
        await StartAsync();
        await _app.WaitForShutdownAsync();
        await StopAsync();
    }

    /// <summary>Gracefully stop the server, running all before-stop hooks first.</summary>
    public async Task StopAsync()
    {
        foreach (var hook in _beforeStop)
        {
            try { await hook(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AdpAgentHost] before-stop hook failed: {ex.Message}");
            }
        }
        await _app.StopAsync();
        if (_journal is IDisposable d) d.Dispose();
    }

    private static IRuntimeJournalStore BuildDefaultJournal(AgentConfig config) =>
        config.JournalBackend switch
        {
            JournalBackend.Sqlite => new SqliteJournalStore(config.JournalDir),
            _ => new JsonlJournalStore(config.JournalDir),
        };

    private static IEvaluator BuildDefaultEvaluator(AgentConfig config) =>
        config.Evaluator switch
        {
            { Kind: "shell" } => new ShellEvaluator(config),
            { Kind: "static" } => new StaticEvaluator(config),
            null => new StaticEvaluator(config),
            var other => throw new InvalidOperationException(
                $"Unknown evaluator kind '{other.Kind}'. Implement IEvaluator yourself and pass it via AdpAgentHostOptions.Evaluator."),
        };
}

/// <summary>Customization hooks for <see cref="AdpAgentHost"/>.</summary>
public sealed class AdpAgentHostOptions
{
    /// <summary>Override the default journal backend selection.</summary>
    public IRuntimeJournalStore? Journal { get; init; }

    /// <summary>Override the default evaluator (shell/static). Adopters use this to plug in their real evaluator.</summary>
    public IEvaluator? Evaluator { get; init; }

    /// <summary>Register additional DI services before the host builds its WebApplication.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Register additional HTTP routes before the host starts listening.</summary>
    public Action<WebApplication>? ConfigureRoutes { get; init; }
}
