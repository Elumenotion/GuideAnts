using System.Collections.Concurrent;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using GuideAnts.Logging;
namespace ScriptExecutionAgent;

internal sealed record NotebookExecutionIdentity(string UserName, string GroupName, int Uid, int Gid);
internal static class NotebookExecutionIdentityProvider
{
    internal static readonly ConcurrentDictionary<string, NotebookExecutionIdentity> IdentityCache = new(StringComparer.Ordinal);
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> IdentityLocks = new(StringComparer.Ordinal);

    public static async Task<NotebookExecutionIdentity?> PrepareAsync(
        Guid projectId,
        Guid notebookId,
        string notebookRoot,
        string authorizedWorkingDirectory,
        AgentSecurityOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !options.EnableNotebookIdentityIsolation)
        {
            return null;
        }

        var (mountRegistry, _, _) = NotebookMountsRegistry.TryLoad(notebookRoot);
        mountRegistry ??= NotebookMountsRegistry.Empty;

        if (mountRegistry.Mounts.Count > 0)
        {
            logger.LogInformation(
                "SECURITY: notebook has registered mounts; using compatibility execution mode. projectId={ProjectId} notebookId={NotebookId} mountCount={MountCount}",
                projectId,
                notebookId,
                mountRegistry.Mounts.Count);
            return null;
        }

        var identity = await GetOrCreateIdentityAsync(projectId, notebookId, logger, cancellationToken);

        try
        {
            await EnsureOwnedAndRestrictedAsync(notebookRoot, identity, mountRegistry, cancellationToken);
            await EnsureOwnedAndRestrictedAsync(authorizedWorkingDirectory, identity, mountRegistry, cancellationToken);
        }
        catch (Exception ex)
        {
            if (!options.AllowOwnershipFallback)
            {
                throw new InvalidOperationException($"Notebook permission preparation failed for notebook {notebookId}.", ex);
            }

            logger.LogWarning(ex, "SECURITY: notebook ownership/permission prep failed; running in compatibility mode. notebookId={NotebookId}", notebookId);
        }

        logger.LogInformation(
            "SECURITY: execution identity resolved. projectId={ProjectId} notebookId={NotebookId} user={User} uid={Uid} gid={Gid}",
            projectId, notebookId, identity.UserName, identity.Uid, identity.Gid);
        return identity;
    }

    public static async Task PrepareScriptFileAsync(string scriptFilePath, NotebookExecutionIdentity identity, CancellationToken cancellationToken)
    {
        await RunCommandAsync("chown", new[] { $"{identity.Uid}:{identity.Gid}", scriptFilePath }, cancellationToken);
        await RunCommandAsync("chmod", new[] { "700", scriptFilePath }, cancellationToken);
    }

    internal static async Task<NotebookExecutionIdentity> GetOrCreateIdentityAsync(
        Guid projectId,
        Guid notebookId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{projectId:D}:{notebookId:D}";
        if (IdentityCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var identityLock = IdentityLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await identityLock.WaitAsync(cancellationToken);
        try
        {
            if (IdentityCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            var suffix = notebookId.ToString("N")[..12];
            var userName = $"nb_{suffix}";
            var groupName = $"nbg_{suffix}";

            if (!await GroupExistsAsync(groupName, cancellationToken))
            {
                await RunCommandAsync("groupadd", new[] { "--system", groupName }, cancellationToken, tolerateAlreadyExists: true);
            }

            if (!await UserExistsAsync(userName, cancellationToken))
            {
                await RunCommandAsync(
                    "useradd",
                    new[] { "--system", "--gid", groupName, "--home", "/nonexistent", "--shell", "/usr/sbin/nologin", userName },
                    cancellationToken,
                    tolerateAlreadyExists: true);
            }

            var uid = await ReadNumericCommandOutputAsync("id", new[] { "-u", userName }, cancellationToken);
            var gid = await ReadNumericCommandOutputAsync("id", new[] { "-g", userName }, cancellationToken);
            await EnsureSetprivReadyAsync(uid, gid, cancellationToken);

            var created = new NotebookExecutionIdentity(userName, groupName, uid, gid);
            IdentityCache[cacheKey] = created;
            logger.LogInformation("SECURITY: created notebook execution identity cache entry for {CacheKey}", cacheKey);
            return created;
        }
        finally
        {
            identityLock.Release();
        }
    }

    internal static async Task EnsureSetprivReadyAsync(int uid, int gid, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await Cli.Wrap("setpriv")
                .WithArguments(args => args
                    .Add("--reuid")
                    .Add(uid.ToString())
                    .Add("--regid")
                    .Add(gid.ToString())
                    .Add("--init-groups")
                    .Add("--")
                    .Add("true"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException($"setpriv identity warm-up failed for uid={uid} gid={gid}");
    }

    internal static async Task EnsureOwnedAndRestrictedAsync(
        string path,
        NotebookExecutionIdentity identity,
        NotebookMountsRegistry mountRegistry,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if (mountRegistry.IsUnderAnyContainerSourcePath(path))
        {
            return;
        }

        // -P: never traverse symlinks (registered mount links stay link-only; host trees are not walked).
        await RunCommandAsync("chown", new[] { "-R", "-P", $"{identity.Uid}:{identity.Gid}", path }, cancellationToken);
        await RunCommandAsync("chmod", new[] { "700", path }, cancellationToken);
    }

    internal static async Task<bool> GroupExistsAsync(string groupName, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("getent")
            .WithArguments(args => args.Add("group").Add(groupName))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result.ExitCode == 0;
    }

    internal static async Task<bool> UserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("id")
            .WithArguments(args => args.Add("-u").Add(userName))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result.ExitCode == 0;
    }

    internal static async Task<int> ReadNumericCommandOutputAsync(string command, IReadOnlyCollection<string> args, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(builder =>
            {
                foreach (var arg in args)
                {
                    builder.Add(arg);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{command}' failed: {result.StandardError}");
        }

        if (!int.TryParse(result.StandardOutput.Trim(), out var parsed))
        {
            throw new InvalidOperationException($"Command '{command}' returned non-numeric output: {result.StandardOutput}");
        }

        return parsed;
    }

    internal static async Task RunCommandAsync(
        string command,
        IReadOnlyCollection<string> args,
        CancellationToken cancellationToken,
        bool tolerateAlreadyExists = false)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(builder =>
            {
                foreach (var arg in args)
                {
                    builder.Add(arg);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var stderr = result.StandardError ?? string.Empty;
        if (tolerateAlreadyExists &&
            (stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is not unique", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InvalidOperationException($"Command '{command}' failed with exit code {result.ExitCode}: {stderr}");
    }
}

