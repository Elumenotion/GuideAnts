using CliWrap;
using CliWrap.Buffered;
using GuideAnts.Logging;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScriptExecutionAgent;

internal static class AdminStateRuntime
{
    public static async Task InitializeAsync(
        AdminApiOptions adminOptions,
        ScriptExecutionScopeOptions scopeOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(adminOptions.StateDirectoryPath);

            var requirementsPath = GetGlobalRequirementsPath(adminOptions);
            if (!File.Exists(requirementsPath))
            {
                await AtomicFile.WriteAllTextAsync(requirementsPath, string.Empty, cancellationToken);
            }

            var aptPackagesPath = GetAptPackagesPath(adminOptions);
            if (!File.Exists(aptPackagesPath))
            {
                await AtomicFile.WriteAllTextAsync(aptPackagesPath, string.Empty, cancellationToken);
            }

            var requirementsValidation = ScriptExecutionScopeRuntime.ValidateRequirements(
                await File.ReadAllTextAsync(requirementsPath, cancellationToken));
            if (!requirementsValidation.IsValid)
            {
                throw new InvalidOperationException(requirementsValidation.ErrorMessage);
            }

            var aptPackagesValidation = ValidateAptPackages(await File.ReadAllTextAsync(aptPackagesPath, cancellationToken));
            if (!aptPackagesValidation.IsValid)
            {
                throw new InvalidOperationException(aptPackagesValidation.ErrorMessage);
            }

        }
        catch (Exception ex)
        {
            if (!adminOptions.FailOpen)
            {
                throw;
            }

            logger.LogWarning(ex, "ScriptExecutionAgent admin state validation failed; continuing because SCRIPT_EXECUTION_ADMIN_FAIL_OPEN=true.");
        }
    }

    // Applies global apt packages onto the container rootfs. Intentionally NOT awaited during
    // startup: apt work must never gate the HTTP listener. Scoped pip venvs live on the durable
    // admin volume and are provisioned on demand by EnsureScopeRequirementsForExecutionAsync.
    public static async Task ReconcileStartupStateAsync(
        AdminApiOptions adminOptions,
        ScriptExecutionScopeOptions scopeOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var aptResult = await ApplyGlobalAptPackagesAsync(adminOptions, logger, cancellationToken);
            logger.LogInformation(
                "ScriptExecutionAgent admin startup reconcile complete. aptStatus={AptStatus}",
                aptResult.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("ScriptExecutionAgent admin startup reconcile canceled during shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "ScriptExecutionAgent admin startup reconcile failed; affected scopes will be provisioned on first execution.");
        }
    }

    public static string GetGlobalRequirementsPath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "requirements.txt");

    public static string GetAptPackagesPath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "apt-packages.txt");

    public static string GetGlobalAppliedStatePath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "applied-state.json");

    public static string GetRequirementsPath(AdminApiOptions options, ScriptExecutionScope? scope) =>
        scope is null ? GetGlobalRequirementsPath(options) : scope.RequirementsFilePath;

    public static ValidationResult ValidateAptPackages(string packageText)
    {
        var lineNumber = 0;
        foreach (var rawLine in packageText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var packageName = line.Split('#', 2)[0].Trim();
            if (!Regex.IsMatch(packageName, "^[a-z0-9][a-z0-9+.-]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                return ValidationResult.Failure($"apt-packages.txt line {lineNumber} is not a valid package name.");
            }
        }

        return ValidationResult.Success();
    }

    public static async Task PreflightGlobalAptPackagesAsync(
        AdminApiOptions adminOptions,
        CancellationToken cancellationToken)
    {
        var aptPackagesPath = GetAptPackagesPath(adminOptions);
        var packageText = File.Exists(aptPackagesPath)
            ? await File.ReadAllTextAsync(aptPackagesPath, cancellationToken)
            : string.Empty;
        var validation = ValidateAptPackages(packageText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredPackages = ParseAptPackages(packageText)
            .Select(NormalizeAptPackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();
        var hash = ComputeSha256(packageText);
        var appliedState = ReadGlobalAppliedState(adminOptions);
        var previouslyManagedPackages = ReadManagedAptPackages(appliedState);
        var packagesToRemove = previouslyManagedPackages
            .Except(desiredPackages, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();

        if (appliedState.TryGetValue("aptPackagesHash", out var previousHash)
            && string.Equals(previousHash, hash, StringComparison.Ordinal)
            && packagesToRemove.Length == 0)
        {
            return;
        }

        if (desiredPackages.Length == 0 && packagesToRemove.Length == 0)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("apt package apply is supported only on Linux containers.");
        }

        if (desiredPackages.Length > 0)
        {
            var update = await Cli.Wrap("apt-get")
                .WithArguments(args => args.Add("update"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
            if (update.ExitCode != 0)
            {
                throw new InvalidOperationException($"apt-get update failed with exit code {update.ExitCode}: {update.StandardError}");
            }

            var dryRun = await Cli.Wrap("apt-get")
                .WithArguments(args =>
                {
                    args.Add("install").Add("--dry-run").Add("-y").Add("--no-install-recommends");
                    foreach (var package in desiredPackages)
                    {
                        args.Add(package);
                    }
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
            if (dryRun.ExitCode != 0)
            {
                throw new InvalidOperationException($"apt-get install dry-run failed with exit code {dryRun.ExitCode}: {dryRun.StandardError}");
            }
        }
    }

    public static async Task<AdminApplyResult> ApplyGlobalAptPackagesAsync(
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var aptPackagesPath = GetAptPackagesPath(adminOptions);
        var packageText = File.Exists(aptPackagesPath)
            ? await File.ReadAllTextAsync(aptPackagesPath, cancellationToken)
            : string.Empty;
        var validation = ValidateAptPackages(packageText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredPackages = ParseAptPackages(packageText)
            .Select(NormalizeAptPackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();
        var hash = ComputeSha256(packageText);
        var appliedState = ReadGlobalAppliedState(adminOptions);
        var previouslyManagedPackages = ReadManagedAptPackages(appliedState);
        var packagesToRemove = previouslyManagedPackages
            .Except(desiredPackages, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();

        if (appliedState.TryGetValue("aptPackagesHash", out var previousHash)
            && string.Equals(previousHash, hash, StringComparison.Ordinal)
            && packagesToRemove.Length == 0)
        {
            return new AdminApplyResult("skipped", 0, 1, Array.Empty<string>());
        }

        if (desiredPackages.Length > 0 || packagesToRemove.Length > 0)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException("apt package apply is supported only on Linux containers.");
            }

            if (packagesToRemove.Length > 0)
            {
                var remove = await Cli.Wrap("apt-get")
                    .WithArguments(args =>
                    {
                        args.Add("remove").Add("-y");
                        foreach (var package in packagesToRemove)
                        {
                            args.Add(package);
                        }
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (remove.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get remove failed with exit code {remove.ExitCode}: {remove.StandardError}");
                }
            }

            if (desiredPackages.Length > 0)
            {
                var update = await Cli.Wrap("apt-get")
                    .WithArguments(args => args.Add("update"))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (update.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get update failed with exit code {update.ExitCode}: {update.StandardError}");
                }

                var install = await Cli.Wrap("apt-get")
                    .WithArguments(args =>
                    {
                        args.Add("install").Add("-y").Add("--no-install-recommends");
                        foreach (var package in desiredPackages)
                        {
                            args.Add(package);
                        }
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (install.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get install failed with exit code {install.ExitCode}: {install.StandardError}");
                }
            }
        }

        appliedState["version"] = "1";
        appliedState["aptPackagesHash"] = hash;
        appliedState["aptManagedPackages"] = string.Join('\n', desiredPackages);
        appliedState["aptPackagesAppliedAt"] = DateTimeOffset.UtcNow.ToString("O");
        await WriteGlobalAppliedStateAsync(adminOptions, appliedState, cancellationToken);
        logger.LogInformation(
            "Applied admin apt packages. installedCount={InstalledCount} removedCount={RemovedCount} hash={Hash}",
            desiredPackages.Length,
            packagesToRemove.Length,
            hash);
        return new AdminApplyResult("applied", 1, 0, Array.Empty<string>());
    }

    internal static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static IEnumerable<string> ParseAptPackages(string packageText) =>
        packageText.Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

    internal static IReadOnlySet<string> ReadManagedAptPackages(IReadOnlyDictionary<string, string> state)
    {
        if (!state.TryGetValue("aptManagedPackages", out var serialized) || string.IsNullOrWhiteSpace(serialized))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ParseAptPackages(serialized)
            .Select(NormalizeAptPackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static string NormalizeAptPackageName(string packageName) => packageName.Trim().ToLowerInvariant();

    internal static Dictionary<string, string> ReadGlobalAppliedState(AdminApiOptions options)
    {
        var path = GetGlobalAppliedStatePath(options);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static Task WriteGlobalAppliedStateAsync(
        AdminApiOptions options,
        Dictionary<string, string> state,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        return AtomicFile.WriteAllTextAsync(GetGlobalAppliedStatePath(options), json, cancellationToken);
    }
}

