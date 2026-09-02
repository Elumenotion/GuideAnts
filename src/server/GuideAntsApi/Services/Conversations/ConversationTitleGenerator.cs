using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations;

public static class ConversationTitleGenerator
{
    public sealed record Result(bool Found, string Title, bool AttemptedGeneration = false);

    private static readonly string[] DefaultTitles = { "New Conversation", "Untitled" };

    private static bool IsDefaultTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ||
        DefaultTitles.Any(t => string.Equals(t, title.Trim(), StringComparison.OrdinalIgnoreCase));

    public static async Task<Result> GenerateAndApplyAsync(
        ApplicationDbContext db,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await db.NotebookConversations
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Project)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation == null)
        {
            return new Result(false, string.Empty);
        }

        if (!IsDefaultTitle(conversation.Title))
        {
            // Conversation already has a user-set or previously generated title — never
            // clobber it on subsequent turns.
            return new Result(true, conversation.Title);
        }

        var dialogBuilder = new StringBuilder();
        foreach (var message in conversation.Messages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (message.Role != ChatRole.User && message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var role = message.Role == ChatRole.User ? "User" : "Assistant";
            dialogBuilder.Append(role).Append(": ").AppendLine(message.Content.Trim());
        }

        var dialogText = dialogBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(dialogText))
        {
            return new Result(true, conversation.Title);
        }

        var context = new AntRunner.ToolCalling.InvocationContext(
            ProjectId: conversation.Notebook.ProjectId,
            NotebookId: conversation.NotebookId,
            ConversationId: conversation.Id);

        var instructions = "Create a concise 4–8 word, title case, single-line title without punctuation.\n\nConversation:\n" + dialogText;
        var generationResult = await AntRunner.Chat.Agent.Invoke("Conversation Title Generator", instructions, context);

        var title = (generationResult?.StandardOutput ?? string.Empty).Trim().Trim('\"').Trim();
        if (title.EndsWith('.') || title.EndsWith('!') || title.EndsWith('?'))
        {
            title = title.TrimEnd('.', '!', '?').TrimEnd();
        }

        if (title.Length > 255)
        {
            title = title[..255].Trim();
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            conversation.Title = title;
            await db.SaveChangesAsync(cancellationToken);
            return new Result(true, title, AttemptedGeneration: true);
        }

        return new Result(true, conversation.Title, AttemptedGeneration: true);
    }
}
