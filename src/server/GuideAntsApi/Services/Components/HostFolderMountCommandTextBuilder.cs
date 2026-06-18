namespace GuideAntsApi.Services.Components;

/// <summary>
/// Builds sanitized host helper command text (plan §6/§11, §20).
/// </summary>
public static class HostFolderMountCommandTextBuilder
{
    private const string PowerShellScript = @".\scripts\guideants-host-mount.ps1";
    private const string BashScript = "./scripts/guideants-host-mount.sh";

    // The API runs in a Linux container and cannot know the admin's host OS
    // (the helper script reads .installer_state.env on the host, not in the
    // container). Emit both commands so the admin runs the one for their shell.
    public static string BuildApplyCommand(Guid mountId, Guid projectId, string hostPath) =>
        CombineForBothShells(
            BuildPowerShellApplyCommand(mountId, projectId, hostPath),
            BuildBashApplyCommand(mountId, projectId, hostPath));

    public static string BuildRemoveCommand(Guid mountId) =>
        CombineForBothShells(
            BuildPowerShellRemoveCommand(mountId),
            BuildBashRemoveCommand(mountId));

    private static string CombineForBothShells(string powerShellCommand, string bashCommand) =>
        string.Join(
            '\n',
            "# Windows (PowerShell) — run from the repository root:",
            powerShellCommand,
            "",
            "# Linux / macOS (bash) — run from the repository root:",
            bashCommand);

    public static string QuoteForPowerShellArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    public static string QuoteForBashArgument(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    public static string FormatHostPathForCompose(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return string.Empty;
        }

        if (hostPath.Length >= 3
            && char.IsAsciiLetter(hostPath[0])
            && hostPath[1] == ':'
            && (hostPath[2] == '\\' || hostPath[2] == '/'))
        {
            var drive = char.ToUpperInvariant(hostPath[0]);
            var rest = hostPath[2..].Replace('\\', '/').TrimStart('/');
            return $"{drive}:/{rest}";
        }

        return hostPath.Replace('\\', '/');
    }

    private static string BuildPowerShellApplyCommand(Guid mountId, Guid projectId, string hostPath)
    {
        var mountIdArg = QuoteForPowerShellArgument(mountId.ToString());
        var projectIdArg = QuoteForPowerShellArgument(projectId.ToString());
        var hostPathArg = QuoteForPowerShellArgument(hostPath);
        return $"{PowerShellScript} apply -MountId {mountIdArg} -ProjectId {projectIdArg} -HostPath {hostPathArg}";
    }

    private static string BuildPowerShellRemoveCommand(Guid mountId)
    {
        var mountIdArg = QuoteForPowerShellArgument(mountId.ToString());
        return $"{PowerShellScript} remove -MountId {mountIdArg}";
    }

    private static string BuildBashApplyCommand(Guid mountId, Guid projectId, string hostPath)
    {
        var mountIdArg = QuoteForBashArgument(mountId.ToString());
        var projectIdArg = QuoteForBashArgument(projectId.ToString());
        var hostPathArg = QuoteForBashArgument(hostPath);
        return $"{BashScript} apply --mount-id {mountIdArg} --project-id {projectIdArg} --host-path {hostPathArg}";
    }

    private static string BuildBashRemoveCommand(Guid mountId)
    {
        var mountIdArg = QuoteForBashArgument(mountId.ToString());
        return $"{BashScript} remove --mount-id {mountIdArg}";
    }
}
