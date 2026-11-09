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
    internal static readonly ConcurrentDictionary<string, DateTimeOffset> IdentityLastUse = new(StringComparer.Ordinal);

    private static int IdentityLimit = AgentEnvironment.GetPositiveIntEnvironmentVariable("SCRIPT_EXECUTION_MAX_NOTEBOOK_IDENTITIES", defaultValue: 512);

    /// <summary>
    /// Test seam: the reaper and the LRU bound read the environment at process start, and
    /// tests need to exercise different limits in one process.
    /// </summary>
    internal static void SetIdentityLimitForTests(int limit)
    {
        IdentityLimit = limit;
    }

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

        // LRU bound during long-lived runs: evict the oldest-unused identity once the live
        // count exceeds the configured maximum. Never evicts an identity this very call is
        // about to use, and never runs unless the isolation path is active (we are here).
        await EvictOldestUnusedIdentityAsync(logger, cancellationToken);

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
            IdentityLastUse[cacheKey] = DateTimeOffset.UtcNow;
            return cached;
        }

        var identityLock = IdentityLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await identityLock.WaitAsync(cancellationToken);
        try
        {
            if (IdentityCache.TryGetValue(cacheKey, out cached))
            {
                IdentityLastUse[cacheKey] = DateTimeOffset.UtcNow;
                return cached;
            }

            // 24 hex chars (nb_ + 24 = 27, under USER_NAME_MAX 32): ~2^96 collision space.
            // The legacy 12-hex name collided across notebooks sharing a prefix; a legacy
            // identity is migrated in place (uid-preserving) so one notebook keeps one user.
            var hex = notebookId.ToString("N");
            var userName = $"nb_{hex[..24]}";
            var groupName = $"nbg_{hex[..24]}";
            var legacyUserName = $"nb_{hex[..12]}";
            var legacyGroupName = $"nbg_{hex[..12]}";

            var migrated = false;
            if (await UserExistsAsync(legacyUserName, cancellationToken))
            {
                migrated = await RenameIdentityAsync(legacyUserName, userName, legacyGroupName, groupName, cancellationToken);
                if (!migrated)
                {
                    logger.LogWarning(
                        "SECURITY: legacy notebook identity {LegacyUser} could not be renamed to {User}; falling back to a new identity. notebookId={NotebookId}",
                        legacyUserName, userName, notebookId);
                }
            }

            if (!migrated)
            {
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
            }

            var uid = await ReadNumericCommandOutputAsync("id", new[] { "-u", userName }, cancellationToken);
            var gid = await ReadNumericCommandOutputAsync("id", new[] { "-g", userName }, cancellationToken);
            await EnsureSetprivReadyAsync(uid, gid, cancellationToken);

            var created = new NotebookExecutionIdentity(userName, groupName, uid, gid);
            IdentityCache[cacheKey] = created;
            IdentityLastUse[cacheKey] = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "SECURITY: created notebook execution identity cache entry for {CacheKey}{Migrated}",
                cacheKey,
                migrated ? " (migrated from legacy short name)" : string.Empty);
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

    /// <summary>
    /// Renames a legacy nb_-identity to the 24-hex name in place, preserving uid/gid and
    /// therefore file ownership. Best-effort: any failure returns false and the caller
    /// falls back to creating the new identity fresh.
    /// </summary>
    private static async Task<bool> RenameIdentityAsync(
        string oldUserName,
        string newUserName,
        string oldGroupName,
        string newGroupName,
        CancellationToken cancellationToken)
    {
        if (!await GroupExistsAsync(oldGroupName, cancellationToken))
        {
            return false;
        }

        var usermod = await RunProcessLenientAsync("usermod", $"-l {newUserName} {oldUserName}", cancellationToken);
        if (usermod.ExitCode != 0)
        {
            return false;
        }

        var groupmod = await RunProcessLenientAsync("groupmod", $"-n {newGroupName} {oldGroupName}", cancellationToken);
        if (groupmod.ExitCode != 0)
        {
            return false;
        }

        return await UserExistsAsync(newUserName, cancellationToken);
    }

    private static async Task<CliWrap.Buffered.BufferedCommandResult> RunProcessLenientAsync(
        string command,
        string arguments,
        CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(args =>
            {
                foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    args.Add(argument);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// LRU bound: once the live identity count exceeds the configured maximum, the
    /// oldest-unused identities are evicted (userdel + groupdel + cache removal). An
    /// identity currently cached is, by construction, "in use" and is never the oldest
    /// unused while it is still being served - eviction only happens when the count
    /// exceeds the cap, i.e. after at least (cap+1) distinct notebooks have been served.
    /// Eviction failures are logged and do not break the execution path.
    /// </summary>
    internal static async Task EvictOldestUnusedIdentityAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (IdentityCache.Count <= IdentityLimit)
        {
            return;
        }

        var oldestKey = IdentityLastUse
            .OrderBy(kv => kv.Value)
            .Select(kv => kv.Key)
            .First();

        if (!IdentityCache.TryRemove(oldestKey, out var identity))
        {
            return;
        }

        IdentityLastUse.TryRemove(oldestKey, out _);
        try
        {
            await RunCommandLenientAsync("userdel", identity.UserName, logger, cancellationToken);
            await RunCommandLenientAsync("groupdel", identity.GroupName, logger, cancellationToken);
            logger.LogInformation(
                "SECURITY: evicted oldest-unused notebook identity {User} (uid={Uid}) - live identities exceeded {Limit}.",
                identity.UserName, identity.Uid, IdentityLimit);
        }
        catch (Exception ex)
        {
            // Re-add on failure: a half-evicted identity (user gone, group lingering, or
            // vice versa) must not be treated as usable. GetOrCreateIdentityAsync will
            // recreate what is missing on the next access.
            IdentityCache[oldestKey] = identity;
            IdentityLastUse[oldestKey] = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "SECURITY: identity eviction failed for {User}; keeping the cache entry.", identity.UserName);
        }
    }

    private static async Task RunCommandLenientAsync(string command, string argument, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(args => args.Add(argument))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            logger.LogWarning("SECURITY: {Command} {Argument} failed (exit {ExitCode}): {StdErr}", command, argument, result.ExitCode, LogValueSanitizer.Sanitize(result.StandardError));
        }
    }

    /// <summary>
    /// Reaps notebook identities whose notebook directory no longer exists under any
    /// project-* in the storage root, and which are not in the live identity cache.
    /// Both 12-hex (legacy) and 24-hex names are matched by prefix: the username is
    /// nb_<first N hex of the notebook id>, so the notebook exists iff some notebook-<id>
    /// directory's id starts with that suffix - an upgrade never reaps a still-live
    /// notebook's user, and prefix matching can only ever be a false "exists" (conservative
    /// skip), never a false "gone". Must only run when
    /// SCRIPT_EXECUTION_REAP_ORPHAN_IDENTITIES=true (it deletes system state).
    /// </summary>
    internal static async Task ReapOrphanedIdentitiesAsync(string storageRoot, ILogger logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!Directory.Exists(storageRoot))
        {
            logger.LogWarning("SECURITY: identity reaper skipped because storage root {StorageRoot} does not exist.", LogValueSanitizer.Sanitize(storageRoot));
            return;
        }

        var passwd = await Cli.Wrap("getent")
            .WithArguments(args => args.Add("passwd"))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (passwd.ExitCode != 0)
        {
            logger.LogWarning("SECURITY: identity reaper skipped because getent passwd failed (exit {ExitCode}).", passwd.ExitCode);
            return;
        }

        var cachedUserNames = new HashSet<string>(
            IdentityCache.Values.Select(identity => identity.UserName),
            StringComparer.Ordinal);
        var reaped = 0;
        foreach (var line in (passwd.StandardOutput ?? string.Empty).Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = line.Split(':');
            if (fields.Length < 2)
            {
                continue;
            }

            var userName = fields[0];
            if (!userName.StartsWith("nb_", StringComparison.Ordinal))
            {
                continue;
            }

            if (cachedUserNames.Contains(userName))
            {
                continue;
            }

            var suffix = userName[3..];
            if (suffix.Length is not (12 or 24) || suffix.Any(c => !char.IsAsciiHexDigit(c)))
            {
                // Not a shape we recognize (new 24-hex or legacy 12-hex); leave it alone.
                continue;
            }

            // Both name shapes are prefixes of the notebook id: the username is nb_<first N
            // hex> and the notebook directory is notebook_<full 32 hex>, so the notebook
            // exists iff some directory id starts with the username's suffix. Prefix matching
            // can only ever be a false "exists" (conservative: we skip reaping), never a
            // false "gone" (which would reap a live user).
            if (NotebookSuffixExists(storageRoot, suffix))
            {
                continue;
            }

            var groupName = $"nbg_{suffix}";
            await RunCommandLenientAsync("userdel", userName, logger, cancellationToken);
            await RunCommandLenientAsync("groupdel", groupName, logger, cancellationToken);
            reaped++;
            logger.LogInformation("SECURITY: reaped orphaned notebook identity {User} (group {Group}).", userName, groupName);
        }

        logger.LogInformation("SECURITY: identity reaper complete. reaped={Reaped}", reaped);
    }

    private static bool NotebookSuffixExists(string storageRoot, string hexSuffix)
    {
        foreach (var projectDir in Directory.EnumerateDirectories(storageRoot, "project-*"))
        {
            foreach (var notebookDir in Directory.EnumerateDirectories(projectDir, "notebook-*"))
            {
                var notebookName = Path.GetFileName(notebookDir);
                var notebookHex = notebookName.StartsWith("notebook-", StringComparison.Ordinal) ? notebookName[9..] : notebookName;
                if (notebookHex.StartsWith(hexSuffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
