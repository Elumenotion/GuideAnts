using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ScriptExecutionAgent;

/// <summary>
/// Spawns MCP stdio children with the same isolated process environment as <c>/execute</c>.
/// Uses <see cref="StreamClientTransport"/> so we control <see cref="ProcessStartInfo.Environment"/>
/// directly instead of delegating spawn semantics to <see cref="StdioClientTransport"/>.
/// </summary>
internal sealed class McpIsolatedChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stderrCts = new();
    private readonly Task _stderrPump;

    public static McpIsolatedChildProcess Start(
        IsolatedProcessLaunchPlan launch,
        string workingDirectory,
        ILogger? logger = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Command,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        if (launch.ProcessEnvironment is not null)
        {
            foreach (var (key, value) in launch.ProcessEnvironment)
            {
                if (value is not null)
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP child process '{launch.Command}'.");

        logger?.LogDebug(
            "Started isolated MCP child process. command={Command} argumentCount={ArgumentCount} workingDirectory={WorkingDirectory}",
            LogValueSanitizer.Sanitize(launch.Command),
            launch.Arguments.Length,
            LogValueSanitizer.Sanitize(workingDirectory));

        return new McpIsolatedChildProcess(process);
    }

    private McpIsolatedChildProcess(Process process)
    {
        _process = process;
        _stderrPump = PumpStderrAsync();
    }

    public IClientTransport CreateTransport(ILoggerFactory? loggerFactory = null) =>
        new StreamClientTransport(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            loggerFactory);

    public async ValueTask DisposeAsync()
    {
        _stderrCts.Cancel();
        try
        {
            await _stderrPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        _process.Dispose();
        _stderrCts.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_stderrCts.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_stderrCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
