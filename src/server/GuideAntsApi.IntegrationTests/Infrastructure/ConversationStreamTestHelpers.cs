using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Seeding and SSE helpers for tests that drive the real conversation streaming path
/// (fake or real chat provider, real SQL persistence).
/// </summary>
internal static class ConversationStreamTestHelpers
{
    /// <summary>Distinctive fragment of CompactionSummaryRenderer's framing line.</summary>
    public const string HandoffFramingFragment = "condensed handoff briefing";

    public static async Task<(Guid ProjectId, Guid NotebookId)> SeedProjectNotebookAsync(
        ApplicationDbContext db, string label)
    {
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"{label} Project {Guid.NewGuid():N}",
            Slug = $"it-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"{label} Notebook {Guid.NewGuid():N}",
            Slug = $"it-nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        return (project.Id, notebook.Id);
    }

    public static async Task<Guid> SeedConversationAsync(ApplicationDbContext db, Guid notebookId, string title)
    {
        var conv = new NotebookConversation { NotebookId = notebookId, Title = title };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    public static async Task<List<(string EventType, string Payload)>> SendMessageStreamAsync(
        HttpClient client,
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                events.Add((currentEvent, line["data:".Length..].Trim()));
            }
        }

        return events;
    }

    /// <summary>Every persisted message's content, keyed by id -- the "nothing was mutated" baseline.</summary>
    public static Task<Dictionary<Guid, string>> SnapshotMessageContentAsync(ApplicationDbContext db, Guid conversationId) =>
        db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId)
            .ToDictionaryAsync(m => m.Id, m => m.Content);

    /// <summary>The same rule CompactionService uses for the boundary (CompactionService.cs:104).</summary>
    public static Task<int> LastCompletedTurnIndexAsync(ApplicationDbContext db, Guid conversationId) =>
        db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.Status == "completed")
            .MaxAsync(t => t.TurnIndex);
}
