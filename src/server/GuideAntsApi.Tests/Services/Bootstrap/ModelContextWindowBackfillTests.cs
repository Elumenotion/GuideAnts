using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class ModelContextWindowBackfillTests
{
    private static async Task<ApplicationDbContext> SeedAsync(params Model[] models)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"backfill-{Guid.NewGuid():N}");
        var db = new ApplicationDbContext(options);
        db.Models.AddRange(models);
        await db.SaveChangesAsync();
        return db;
    }

    [TestMethod]
    public async Task FillsNullWindow_ForAKnownModel()
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "claude-haiku-4-5",
            DisplayName = "Claude Haiku 4.5",
            Provider = "anthropic"
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().Be(200_000);
        row.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public async Task NeverOverwritesAnExistingValue()
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "claude-haiku-4-5",
            DisplayName = "Claude Haiku 4.5",
            Provider = "anthropic",
            ContextWindowTokens = 999,
            MaxOutputTokens = 111
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().Be(999, "an admin-entered value is authoritative");
        row.MaxOutputTokens.Should().Be(111);
    }

    [TestMethod]
    public async Task LeavesUnknownModelsNull()
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "some-private-deployment",
            DisplayName = "Private",
            Provider = "openai-chat"
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task IsIdempotent()
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "claude-haiku-4-5",
            DisplayName = "Claude Haiku 4.5",
            Provider = "anthropic"
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);
        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().Be(200_000);
    }

    [TestMethod]
    [DataRow("llama-cpp")]
    [DataRow("LLAMA-CPP")]
    public async Task SkipsLlamaCppRows_EvenWhenIdMatchesACloudModel(string provider)
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "gpt-4o",
            DisplayName = "Local gpt-4o lookalike",
            Provider = provider
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().BeNull("local models get their window from the runtime");
        row.MaxOutputTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task FillsWindow_AndPreservesExistingMaxOutput()
    {
        await using var db = await SeedAsync(new Model
        {
            ModelId = "claude-haiku-4-5",
            DisplayName = "Claude Haiku 4.5",
            Provider = "anthropic",
            ContextWindowTokens = null,
            MaxOutputTokens = 4_096
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().Be(200_000);
        row.MaxOutputTokens.Should().Be(4_096, "an existing output cap is never overwritten");
    }

    [TestMethod]
    public async Task LeavesRowUntouched_WhenWindowSetButMaxOutputNull()
    {
        // Deliberate scope: only rows with a null WINDOW are candidates.
        await using var db = await SeedAsync(new Model
        {
            ModelId = "claude-haiku-4-5",
            DisplayName = "Claude Haiku 4.5",
            Provider = "anthropic",
            ContextWindowTokens = 123_000,
            MaxOutputTokens = null
        });

        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        var row = await db.Models.SingleAsync();
        row.ContextWindowTokens.Should().Be(123_000);
        row.MaxOutputTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task MatchesClientSeedFile_ForEveryEntry()
    {
        var path = FindClientSeed();
        if (path is null)
        {
            Assert.Inconclusive("knownCloudModels.json not found walking up from the test base directory.");
            return;
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var entries = doc.RootElement.EnumerateArray().ToList();
        entries.Should().NotBeEmpty();

        var models = entries.Select(e => new Model
        {
            ModelId = e.GetProperty("modelId").GetString()!,
            DisplayName = e.GetProperty("modelId").GetString()!,
            Provider = "openai-chat"
        }).ToArray();

        await using var db = await SeedAsync(models);
        await ModelContextWindowBackfill.RunAsync(db, CancellationToken.None);

        foreach (var entry in entries)
        {
            var id = entry.GetProperty("modelId").GetString()!;
            var row = await db.Models.SingleAsync(m => m.ModelId == id);

            if (entry.TryGetProperty("contextWindowTokens", out var w) && w.ValueKind == JsonValueKind.Number)
            {
                row.ContextWindowTokens.Should().Be(w.GetInt32(), $"window for {id}");
                if (entry.TryGetProperty("maxOutputTokens", out var o) && o.ValueKind == JsonValueKind.Number)
                {
                    row.MaxOutputTokens.Should().Be(o.GetInt32(), $"max output for {id}");
                }
            }
            else
            {
                row.ContextWindowTokens.Should().BeNull($"{id} has no verified window in the client seed");
            }
        }
    }

    private static string? FindClientSeed()
    {
        var relative = Path.Combine("src", "client", "src", "pages", "settings", "data", "knownCloudModels.json");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
