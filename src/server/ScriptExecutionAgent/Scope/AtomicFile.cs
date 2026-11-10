namespace ScriptExecutionAgent;

internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
