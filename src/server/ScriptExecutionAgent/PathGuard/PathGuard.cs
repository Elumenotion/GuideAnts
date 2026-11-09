using System.Text.Json;

namespace ScriptExecutionAgent;

internal static class PathGuard
{
    public static bool TryResolveAndAuthorizePath(
        string storageRoot,
        string candidatePath,
        Guid projectId,
        Guid notebookId,
        PathAccessMode accessMode,
        out string authorizedPath,
        out string notebookRoot,
        out string rejectionReason)
    {
        authorizedPath = string.Empty;
        notebookRoot = string.Empty;
        rejectionReason = string.Empty;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            rejectionReason = "path is missing";
            return false;
        }

        string fullStorageRoot;
        string logicalTargetPath;
        try
        {
            fullStorageRoot = NormalizeDirectoryPath(storageRoot);
        }
        catch (Exception ex)
        {
            rejectionReason = $"path normalization failed: {ex.Message}";
            return false;
        }

        if (!TryNormalizeLogicalPathUnderRoot(fullStorageRoot, candidatePath, out logicalTargetPath))
        {
            rejectionReason = "path escapes FILE_STORAGE_ROOT";
            return false;
        }

        if (!TryResolveNotebookRootFromMetadata(fullStorageRoot, logicalTargetPath, projectId, notebookId, out var extractedNotebookRoot))
        {
            rejectionReason = "path is not notebook-scoped";
            return false;
        }

        if (!IsStrictChildOrSamePath(fullStorageRoot, extractedNotebookRoot))
        {
            rejectionReason = "notebook root escapes FILE_STORAGE_ROOT";
            return false;
        }

        if (!IsStrictChildOrSamePath(extractedNotebookRoot, logicalTargetPath))
        {
            rejectionReason = "path escapes notebook root";
            return false;
        }

        var (registry, registryStatus, registryError) = NotebookMountsRegistry.TryLoad(extractedNotebookRoot);
        if (registryStatus == MountsRegistryLoadStatus.Malformed)
        {
            rejectionReason = registryError ?? "mounts registry is malformed";
            return false;
        }

        registry ??= NotebookMountsRegistry.Empty;

        if (!TryResolveWithRegisteredCrossings(
                extractedNotebookRoot,
                logicalTargetPath,
                registry,
                out var resolvedPath,
                out var crossedMount,
                out rejectionReason))
        {
            return false;
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(resolvedPath);
        }
        catch (Exception ex)
        {
            rejectionReason = $"path normalization failed after link resolution: {ex.Message}";
            return false;
        }

        var underNotebookRoot = IsStrictChildOrSamePath(extractedNotebookRoot, canonicalPath);
        var underMountSource = crossedMount is not null &&
            IsStrictChildOrSamePath(crossedMount.ContainerSourcePath, canonicalPath);

        if (!underNotebookRoot && !underMountSource)
        {
            rejectionReason = "resolved path escapes authorized scope";
            return false;
        }

        if (underNotebookRoot && !IsStrictChildOrSamePath(fullStorageRoot, canonicalPath))
        {
            rejectionReason = "path escapes FILE_STORAGE_ROOT";
            return false;
        }

        if (crossedMount is not null &&
            HasUnregisteredReparsePointBetween(
                crossedMount.ContainerSourcePath,
                canonicalPath,
                out var mountReparsePath))
        {
            rejectionReason = $"reparse point encountered at '{mountReparsePath}'";
            return false;
        }

        if (accessMode == PathAccessMode.Write && crossedMount is not null && !crossedMount.Writable)
        {
            rejectionReason = "mount is read-only";
            return false;
        }

        authorizedPath = canonicalPath;
        notebookRoot = extractedNotebookRoot;
        return true;
    }

    private static bool TryResolveWithRegisteredCrossings(
        string notebookRoot,
        string fullTargetPath,
        NotebookMountsRegistry registry,
        out string resolvedPath,
        out MountRegistryEntry? crossedMount,
        out string rejectionReason)
    {
        resolvedPath = string.Empty;
        crossedMount = null;
        rejectionReason = string.Empty;

        var normalizedNotebookRoot = NormalizeDirectoryPath(notebookRoot);
        var normalizedTarget = NormalizeDirectoryPath(fullTargetPath);

        if (!IsStrictChildOrSamePath(normalizedNotebookRoot, normalizedTarget))
        {
            rejectionReason = "path escapes notebook root";
            return false;
        }

        var relative = Path.GetRelativePath(normalizedNotebookRoot, normalizedTarget);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            resolvedPath = normalizedNotebookRoot;
            return true;
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = normalizedNotebookRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var next = Path.Combine(current, segment);

            if (!Directory.Exists(next) && !File.Exists(next))
            {
                current = next;
                for (var remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    current = Path.Combine(current, segments[remaining]);
                }

                break;
            }

            if (!TryGetReparsePointStatus(next, out var isReparsePoint, out var attributeError))
            {
                rejectionReason = attributeError ?? $"failed to inspect path segment '{next}'";
                return false;
            }

            if (!isReparsePoint)
            {
                current = next;
                continue;
            }

            var linkRelativePath = Path.GetRelativePath(normalizedNotebookRoot, next)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            var registeredMount = registry.FindByLinkRelativePath(linkRelativePath);
            if (registeredMount is null)
            {
                rejectionReason = $"unregistered reparse point at '{next}'";
                return false;
            }

            if (!TryResolveRegisteredLinkTarget(next, registeredMount.ContainerSourcePath, out var resolvedLinkTarget, out var resolveError))
            {
                rejectionReason = resolveError ?? $"failed to resolve registered link at '{next}'";
                return false;
            }

            crossedMount = registeredMount;
            current = resolvedLinkTarget;
        }

        try
        {
            resolvedPath = Path.GetFullPath(current);
        }
        catch (Exception ex)
        {
            rejectionReason = $"path normalization failed: {ex.Message}";
            return false;
        }

        return true;
    }

    private static bool TryResolveRegisteredLinkTarget(
        string linkPath,
        string registeredContainerSourcePath,
        out string resolvedTarget,
        out string? error)
    {
        resolvedTarget = string.Empty;
        error = null;

        string canonicalLinkTarget;
        try
        {
            var resolvedLink = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: true);
            if (resolvedLink is null)
            {
                error = $"registered link at '{linkPath}' has no target";
                return false;
            }

            canonicalLinkTarget = Path.GetFullPath(resolvedLink.FullName);
        }
        catch (Exception ex)
        {
            error = $"failed to resolve registered link at '{linkPath}': {ex.Message}";
            return false;
        }

        string canonicalContainerSource;
        try
        {
            canonicalContainerSource = Path.GetFullPath(registeredContainerSourcePath);
        }
        catch (Exception ex)
        {
            error = $"registered containerSourcePath is invalid: {ex.Message}";
            return false;
        }

        if (!IsStrictChildOrSamePath(canonicalContainerSource, canonicalLinkTarget))
        {
            error = "registered link target is outside containerSourcePath";
            return false;
        }

        resolvedTarget = canonicalLinkTarget;
        return true;
    }

    private static bool HasUnregisteredReparsePointBetween(string root, string target, out string reparsePath)
    {
        reparsePath = string.Empty;
        string fullRoot;
        string fullTarget;
        try
        {
            fullRoot = NormalizeDirectoryPath(root);
            fullTarget = NormalizeDirectoryPath(target);
        }
        catch
        {
            reparsePath = target;
            return true;
        }

        if (!IsStrictChildOrSamePath(fullRoot, fullTarget))
        {
            reparsePath = fullTarget;
            return true;
        }

        var relative = Path.GetRelativePath(fullRoot, fullTarget);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            return false;
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = fullRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            if (!TryGetReparsePointStatus(current, out var isReparsePoint, out _))
            {
                reparsePath = current;
                return true;
            }

            if (isReparsePoint)
            {
                reparsePath = current;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetReparsePointStatus(string path, out bool isReparsePoint, out string? error)
    {
        isReparsePoint = false;
        error = null;

        try
        {
            var attrs = File.GetAttributes(path);
            isReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read attributes for '{path}': {ex.Message}";
            return false;
        }
    }

    private static bool TryResolveNotebookRootFromMetadata(
        string fullStorageRoot,
        string fullPath,
        Guid projectId,
        Guid notebookId,
        out string notebookRoot)
    {
        notebookRoot = string.Empty;

        var current = NormalizeDirectoryPath(fullPath);
        var normalizedStorageRoot = NormalizeDirectoryPath(fullStorageRoot);

        while (IsStrictChildOrSamePath(normalizedStorageRoot, current))
        {
            var metadataPath = Path.Combine(current, ".guideants", "notebook.json");
            if (TryReadNotebookAssociationMetadata(metadataPath, out var metadataProjectId, out var metadataNotebookId))
            {
                if (metadataProjectId == projectId && metadataNotebookId == notebookId)
                {
                    notebookRoot = current;
                    return true;
                }
            }

            if (string.Equals(current, normalizedStorageRoot, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            current = NormalizeDirectoryPath(parent);
        }

        return false;
    }

    private static bool TryReadNotebookAssociationMetadata(string metadataPath, out Guid projectId, out Guid notebookId)
    {
        projectId = Guid.Empty;
        notebookId = Guid.Empty;

        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("ProjectId", out var projectIdElement) ||
                !doc.RootElement.TryGetProperty("NotebookId", out var notebookIdElement))
            {
                return false;
            }

            if (!Guid.TryParse(projectIdElement.GetString(), out projectId) ||
                !Guid.TryParse(notebookIdElement.GetString(), out notebookId))
            {
                return false;
            }

            return projectId != Guid.Empty && notebookId != Guid.Empty;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizeLogicalPathUnderRoot(string storageRoot, string candidatePath, out string logicalPath)
    {
        logicalPath = string.Empty;
        var fullStorageRoot = NormalizeDirectoryPath(storageRoot);
        var absoluteCandidate = Path.IsPathRooted(candidatePath)
            ? candidatePath
            : Path.Combine(fullStorageRoot, candidatePath);

        var candidateSegments = absoluteCandidate.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var rootSegments = fullStorageRoot.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (candidateSegments.Length < rootSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < rootSegments.Length; index++)
        {
            if (!string.Equals(candidateSegments[index], rootSegments[index], comparison))
            {
                return false;
            }
        }

        var relativeSegments = candidateSegments.Skip(rootSegments.Length).ToArray();
        var current = fullStorageRoot;
        foreach (var segment in relativeSegments)
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || !IsStrictChildOrSamePath(fullStorageRoot, parent))
                {
                    return false;
                }

                current = NormalizeDirectoryPath(parent);
                continue;
            }

            current = Path.Combine(current, segment);
            if (!IsStrictChildOrSamePath(fullStorageRoot, current))
            {
                return false;
            }
        }

        logicalPath = NormalizeDirectoryPath(current);
        return true;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsStrictChildOrSamePath(string root, string path)
    {
        root = NormalizeDirectoryPath(root);
        path = NormalizeDirectoryPath(path);
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
