using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Fills context-window values on catalog rows that have none, for models we know. Runs at
/// startup and is idempotent. Never overwrites a non-null value — an admin-entered number is
/// authoritative over this table. Metadata only: it does not affect compaction or request shaping.
/// </summary>
public static class ModelContextWindowBackfill
{
    private const string LocalProvider = "llama-cpp";

    public static async Task RunAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // Only rows with a null WINDOW are candidates. A row with a window but a null output cap
        // is left alone (deliberate: the window is what the meter needs).
        var candidates = await db.Models
            .Where(m => m.ContextWindowTokens == null)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var model in candidates)
        {
            // Local models get their window live from the runtime; never give them a cloud value.
            if (string.Equals(model.Provider, LocalProvider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!KnownModelContextWindows.TryGet(model.ModelId, out var window, out var maxOutput))
            {
                continue;
            }

            model.ContextWindowTokens = window;

            // Only fill the output cap if it is also unset — the two are independent.
            model.MaxOutputTokens ??= maxOutput;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
