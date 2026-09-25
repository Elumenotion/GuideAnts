namespace AntRunner.Chat.Compaction;

internal sealed record ArtifactsSection(IReadOnlyList<string> Created, IReadOnlyList<string> Modified);

internal static class ArtifactsExtractor
{
    public static ArtifactsSection Extract(IReadOnlyList<TurnFacts> turnFacts)
    {
        var orderedTurns = turnFacts.OrderBy(t => t.TurnIndex).ToList();

        var created = new List<string>();
        var createdSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in orderedTurns)
        {
            foreach (var path in turn.FilesCreated ?? [])
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (createdSet.Add(path))
                {
                    created.Add(path);
                }
            }
        }

        var modified = new List<string>();
        var modifiedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in orderedTurns)
        {
            foreach (var path in turn.FilesModified ?? [])
            {
                if (string.IsNullOrWhiteSpace(path) || createdSet.Contains(path))
                {
                    continue;
                }

                if (modifiedSet.Add(path))
                {
                    modified.Add(path);
                }
            }
        }

        return new ArtifactsSection(created, modified);
    }
}
