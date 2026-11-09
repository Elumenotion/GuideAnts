namespace ScriptExecutionAgent;

internal sealed record AdminApplyRequest(string[]? Targets);

internal sealed class AdminApplyTargets
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "apt",
        "pip",
        "installScripts"
    };

    public bool Apt { get; private init; }

    public bool Pip { get; private init; }

    public bool InstallScripts { get; private init; }

    public static AdminApplyTargets Resolve(bool hasScope, IReadOnlyList<string>? requestedTargets)
    {
        if (requestedTargets is not null && requestedTargets.Count == 0)
        {
            throw new InvalidOperationException("Apply targets cannot be empty.");
        }

        var targets = requestedTargets is { Count: > 0 }
            ? requestedTargets
            : hasScope
                ? new[] { "pip", "installScripts" }
                : new[] { "apt" };

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("Apply targets cannot contain empty values.");
            }

            if (!Allowed.Contains(target))
            {
                throw new InvalidOperationException(
                    $"Apply target '{target}' is not supported. Allowed targets: apt, pip, installScripts.");
            }

            normalized.Add(target);
        }

        if (!hasScope)
        {
            if (normalized.Contains("pip") || normalized.Contains("installScripts"))
            {
                throw new InvalidOperationException(
                    "Global apply supports only apt. Use scoped apply for pip and installScripts.");
            }

            if (!normalized.Contains("apt"))
            {
                throw new InvalidOperationException("Global apply requires target apt.");
            }
        }
        else if (normalized.Contains("apt"))
        {
            throw new InvalidOperationException(
                "Scoped apply cannot target apt. Use global apply with targets [\"apt\"].");
        }
        else if (!normalized.Contains("pip") && !normalized.Contains("installScripts"))
        {
            throw new InvalidOperationException(
                "Scoped apply requires at least one of pip or installScripts.");
        }

        return new AdminApplyTargets
        {
            Apt = normalized.Contains("apt"),
            Pip = normalized.Contains("pip"),
            InstallScripts = normalized.Contains("installScripts")
        };
    }
}
