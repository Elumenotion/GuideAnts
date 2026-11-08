using ScriptExecutionAgent;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Announce startup before any env is read, exactly as the original top-level
        // statements did: the event is emitted before config resolution can throw.
        AgentLogEvent.Emit("script_agent_startup", new Dictionary<string, object?>
        {
            ["version"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        });

        // Resolve every SCRIPT_EXECUTION_* setting before the container is built so a
        // misconfiguration fails fast instead of at first request.
        var options = AgentRuntimeOptions.FromEnvironment();
        AgentStartupConfiguration.ConfigureServices(builder, options);

        var app = builder.Build();
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        options.Validate();
        options.LogStartupConfiguration(startupLogger);

        StartupFilesystemHardening.ApplyAsync(options.FileStorageRoot, startupLogger).GetAwaiter().GetResult();
        Directory.CreateDirectory(options.ScopeOptions.StateRootPath);
        if (options.AdminOptions.Enabled)
        {
            AdminStateRuntime.InitializeAsync(
                options.AdminOptions, options.ScopeOptions, startupLogger, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        AgentStartupConfiguration.ConfigurePipeline(app);

        app.MapHealthEndpoints();
        app.MapExecutionEndpoints();
        app.MapMcpStdioEndpoints();
        app.MapFileEndpoints();
        if (options.AdminOptions.Enabled)
        {
            app.MapAdminEndpoints();
        }

        app.StartAsync().GetAwaiter().GetResult();

        if (options.AdminOptions.Enabled)
        {
            // Bind the HTTP listener first, then reconcile sandbox scopes in the background so that
            // apt/pip provisioning never delays /execute or /health. Any scope an execution needs that
            // has not been reconciled yet is provisioned on demand by EnsureScopeRequirementsForExecutionAsync.
            _ = Task.Run(() => AdminStateRuntime.ReconcileStartupStateAsync(
                options.AdminOptions,
                options.ScopeOptions,
                startupLogger,
                app.Lifetime.ApplicationStopping));
        }

        app.WaitForShutdownAsync().GetAwaiter().GetResult();
    }
}
