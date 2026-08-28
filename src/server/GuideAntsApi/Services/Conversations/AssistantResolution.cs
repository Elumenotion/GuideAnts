using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Single definition of "which assistant does this name refer to": active assistants only,
/// preferring project-scoped (non-global) over global. Used by both the send endpoint's runtime
/// preflight and the conversation stream setup so the two can never disagree.
/// </summary>
public static class AssistantResolution
{
    public static Task<Guid?> ResolveActiveAssistantIdAsync(
        ApplicationDbContext db,
        string assistantName,
        CancellationToken ct) =>
        db.Assistants
            .Where(a => a.Name == assistantName && a.IsActive)
            .OrderBy(a => a.IsGlobal)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
}
