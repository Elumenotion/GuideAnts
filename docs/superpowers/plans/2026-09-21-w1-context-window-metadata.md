# W1: Context-Window Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Teach GuideAnts what each model's context window is, so a later workstream can show users how full their conversation is.

**Architecture:** Two nullable columns on the `Model` catalog entity flow through the existing DTO layers to the Settings UI for editing, and through `ChatTarget` → `ResolvedExecutionPolicy` into the chat runtime. A resolution service picks the best available value per model, preferring a live llama.cpp runtime value over the catalog, and falling back to a value learned from a context-overflow rejection. When nothing is known the value stays null and callers render an explicit unknown state.

**Tech Stack:** ASP.NET Core 8, EF Core 8 (SQL Server; EF InMemory in tests), MSTest + FluentAssertions + Moq, React 19 + TypeScript + Vitest.

**Spec:** [`docs/superpowers/specs/2026-09-21-context-compaction-design.md`](../specs/2026-09-21-context-compaction-design.md)

## Global Constraints

- **Null is safe; wrong is harmful.** A null context window renders as "unknown" in the UI. A wrong value silently misleads a meter users trust. Never guess a value to avoid a null.
- This workstream drives a **meter only**. It must not trigger compaction, alter request shaping, or change any existing chat behavior. (Spec D1: manual trigger only.)
- `AntRunner.Chat` and `AntRunner.Chat.Abstractions` must not reference `GuideAntsApi` or `GuideAntsApi.DataModel`.
- EF migrations run from `src/server`.
- Server tests: MSTest (`[TestClass]` / `[TestMethod]`) with FluentAssertions. In-memory DbContext via `BackgroundJobTestHelpers.CreateInMemoryOptions(name)`.
- Client tests: Vitest, ≥85% line coverage enforced in CI.
- Never co-author commits (no `Co-Authored-By` trailer) — per `CLAUDE.md`.

---

## File Structure

**Created:**
- `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs` — interface + `ContextWindowInfo` + `ContextWindowSource`
- `src/server/GuideAntsApi/Services/Routing/ContextWindowResolver.cs` — resolution precedence
- `src/server/GuideAntsApi/Services/Routing/LearnedContextWindowCache.cs` — values learned from overflow rejections
- `src/server/GuideAntsApi/Services/Bootstrap/KnownModelContextWindows.cs` — server-side value table
- `src/server/GuideAntsApi/Services/Bootstrap/ModelContextWindowBackfill.cs` — startup back-fill
- `src/server/GuideAntsApi/Services/Routing/IModelContextWindowProbe.cs` — probe contract + result
- `src/server/GuideAntsApi/Services/Routing/ModelContextWindowProbe.cs` — Anthropic + OpenRouter probes
- `src/server/GuideAntsApi.Tests/DataModel/ModelContextWindowTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Routing/ChatModelResolverContextWindowTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Routing/LearnedContextWindowCacheTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Routing/ContextWindowResolverTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Bootstrap/ModelContextWindowBackfillTests.cs`
- `src/server/GuideAntsApi.Tests/Services/Routing/ModelContextWindowProbeTests.cs`
- `src/server/GuideAntsApi.Tests/Settings/SettingsModelContextWindowTests.cs`

**Modified:**
- `src/server/GuideAntsApi/Program.cs` — DI registrations + back-fill call
- `src/server/GuideAntsApi/Endpoints/Settings/SettingsModelsEndpoints.cs` — probe route
- `src/server/GuideAntsApi.DataModel/Models/Model.cs` — two columns
- `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ResolvedExecutionPolicy.cs` — two fields
- `src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs` — `ChatTarget` carries the values
- `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:79` — pass into policy
- `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs` — three records
- `src/server/GuideAntsApi/Settings/ApplicationSettingsService.Models.cs` — create/update/map
- `src/server/GuideAntsApi/Models/Guides/CatalogDto.cs` — `ModelDto`
- `src/server/GuideAntsApi/Services/Guides/CatalogService.cs:123`
- `src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs:655`
- `src/client/src/pages/settings/data/knownCloudModels.json` — seed values
- `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx` — seed application + inputs
- `src/client/src/pages/settings/components/ModelsTab.tsx` — edit inputs

---

## Task 1: Model entity columns and migration

**Files:**
- Modify: `src/server/GuideAntsApi.DataModel/Models/Model.cs`
- Create: `src/server/GuideAntsApi.Tests/DataModel/ModelContextWindowTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Model.ContextWindowTokens` (`int?`), `Model.MaxOutputTokens` (`int?`)

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/DataModel/ModelContextWindowTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class ModelContextWindowTests
{
    [TestMethod]
    public async Task ContextWindowColumns_RoundTrip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"ctxwin-{Guid.NewGuid():N}");

        await using (var db = new ApplicationDbContext(options))
        {
            db.Models.Add(new Model
            {
                ModelId = "test-model",
                DisplayName = "Test Model",
                Provider = "openai-chat",
                ContextWindowTokens = 200_000,
                MaxOutputTokens = 64_000
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var saved = await db.Models.SingleAsync(m => m.ModelId == "test-model");
            saved.ContextWindowTokens.Should().Be(200_000);
            saved.MaxOutputTokens.Should().Be(64_000);
        }
    }

    [TestMethod]
    public async Task ContextWindowColumns_DefaultToNull()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"ctxwin-null-{Guid.NewGuid():N}");

        await using (var db = new ApplicationDbContext(options))
        {
            db.Models.Add(new Model
            {
                ModelId = "unknown-model",
                DisplayName = "Unknown Model",
                Provider = "openrouter-chat"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var saved = await db.Models.SingleAsync(m => m.ModelId == "unknown-model");
            saved.ContextWindowTokens.Should().BeNull();
            saved.MaxOutputTokens.Should().BeNull();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowTests"`
Expected: FAIL — compile error, `Model` has no definition for `ContextWindowTokens`.

- [ ] **Step 3: Add the columns**

In `src/server/GuideAntsApi.DataModel/Models/Model.cs`, add after the `Description` property:

```csharp
        /// <summary>
        /// Maximum total tokens this model accepts in one request (prompt + output).
        /// Null when unknown — callers must render an explicit unknown state rather than
        /// assume a default. A wrong value silently misleads the context meter.
        /// </summary>
        public int? ContextWindowTokens { get; set; }

        /// <summary>
        /// Maximum tokens this model can produce in one response. Reserved from
        /// <see cref="ContextWindowTokens"/> when computing remaining prompt headroom.
        /// Null when unknown.
        /// </summary>
        public int? MaxOutputTokens { get; set; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Generate the migration**

Run from `src/server`:

```bash
dotnet ef migrations add AddModelContextWindow \
  --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj \
  --startup-project GuideAntsApi/GuideAntsApi.csproj
```

- [ ] **Step 6: Verify the migration adds two nullable int columns**

Open the generated file under `src/server/GuideAntsApi.DataModel/Migrations/`. Confirm `Up()` contains two `AddColumn<int>` calls against `Models` with `nullable: true`, and `Down()` contains the matching `DropColumn` calls. If the migration contains anything else, delete it, revert the stray model change that caused it, and regenerate.

- [ ] **Step 7: Commit**

```bash
git add src/server/GuideAntsApi.DataModel/Models/Model.cs \
        src/server/GuideAntsApi.DataModel/Migrations/ \
        src/server/GuideAntsApi.Tests/DataModel/ModelContextWindowTests.cs
git commit -m "Adds nullable context window columns to the model catalog"
```

---

## Task 2: Carry the values into the chat runtime

`ResolvedExecutionPolicy` is declared the single source of truth for model parameters and is already threaded into every run, so it is the carrier. `ChatTarget` is built directly from the catalog row, so it is the source.

**Files:**
- Modify: `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ResolvedExecutionPolicy.cs`
- Modify: `src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs:13-17,108`
- Modify: `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs:79`
- Create: `src/server/GuideAntsApi.Tests/Services/Routing/ChatModelResolverContextWindowTests.cs`

**Interfaces:**
- Consumes: `Model.ContextWindowTokens`, `Model.MaxOutputTokens` (Task 1)
- Produces: `ResolvedExecutionPolicy.ContextWindowTokens` (`int?`), `ResolvedExecutionPolicy.MaxOutputTokens` (`int?`), `ChatTarget.ContextWindowTokens` (`int?`), `ChatTarget.MaxOutputTokens` (`int?`)

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Routing/ChatModelResolverContextWindowTests.cs`:

```csharp
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ChatModelResolverContextWindowTests
{
    [TestMethod]
    public void ResolvedExecutionPolicy_CarriesContextWindow()
    {
        var policy = new ResolvedExecutionPolicy(
            "test-model",
            "openai-chat",
            ParameterAuthority.AssistantDefinition,
            new Dictionary<string, System.Text.Json.JsonElement>(),
            ContextWindowTokens: 200_000,
            MaxOutputTokens: 64_000);

        policy.ContextWindowTokens.Should().Be(200_000);
        policy.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void ResolvedExecutionPolicy_DefaultsToNull_WhenNotSupplied()
    {
        var policy = new ResolvedExecutionPolicy(
            "test-model",
            "openai-chat",
            ParameterAuthority.AssistantDefinition,
            new Dictionary<string, System.Text.Json.JsonElement>());

        policy.ContextWindowTokens.Should().BeNull();
        policy.MaxOutputTokens.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ChatModelResolverContextWindowTests"`
Expected: FAIL — `ResolvedExecutionPolicy` does not take those arguments.

- [ ] **Step 3: Extend the policy record**

Replace the record in `src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ResolvedExecutionPolicy.cs`:

```csharp
public sealed record ResolvedExecutionPolicy(
    string ModelId,
    string Provider,
    ParameterAuthority Authority,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null);
```

Optional trailing parameters keep every existing construction site compiling unchanged — including the six in `NotebookHeaderToolbarServiceTests` and the others across the test suite.

- [ ] **Step 4: Extend `ChatTarget` and populate it**

In `src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs`, change the record (line 13):

```csharp
public sealed record ChatTarget(
    string ModelId,
    string Provider,
    string? RuntimeConfigJson,
    GuideAntsApi.Services.LlamaCpp.RuntimeProfileData? ChatBehavior = null,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null);
```

At line 108, pass the catalog row's values through:

```csharp
        return new ChatTarget(
            row.ModelId,
            provider,
            row.RuntimeConfigJson,
            chatBehavior,
            row.ContextWindowTokens,
            row.MaxOutputTokens);
```

- [ ] **Step 5: Pass them into the policy**

In `src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs`, replace line 79:

```csharp
        var policy = new ResolvedExecutionPolicy(
            modelId,
            target.Provider,
            authority,
            parameters,
            target.ContextWindowTokens,
            target.MaxOutputTokens);
```

- [ ] **Step 6: Run the test and the full unit suite**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ChatModelResolverContextWindowTests"`
Expected: PASS (2 tests).

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS — no existing test breaks, because both new parameters are optional.

- [ ] **Step 7: Commit**

```bash
git add src/server/AntRunner.Chat/AntRunner.Chat.Abstractions/ResolvedExecutionPolicy.cs \
        src/server/GuideAntsApi/Services/Routing/IChatTargetResolver.cs \
        src/server/GuideAntsApi/Services/Routing/ChatModelResolver.cs \
        src/server/GuideAntsApi.Tests/Services/Routing/ChatModelResolverContextWindowTests.cs
git commit -m "Carries model context window through the execution policy"
```

---

## Task 3: Settings DTOs and create/update plumbing

**Files:**
- Modify: `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs:180-224`
- Modify: `src/server/GuideAntsApi/Settings/ApplicationSettingsService.Models.cs`
- Create: `src/server/GuideAntsApi.Tests/Settings/SettingsModelContextWindowTests.cs`

**Interfaces:**
- Consumes: `Model.ContextWindowTokens`, `Model.MaxOutputTokens` (Task 1)
- Produces: `SettingsModelDto.ContextWindowTokens` / `.MaxOutputTokens`, and the same two on `CreateSettingsModelRequest` and `UpdateSettingsModelRequest`

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Settings/SettingsModelContextWindowTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class SettingsModelContextWindowTests
{
    [TestMethod]
    public void CreateRequest_AcceptsContextWindow()
    {
        var request = new CreateSettingsModelRequest(
            ModelId: "test-model",
            DisplayName: "Test Model",
            Provider: "openai-chat",
            Description: null,
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: null,
            ContextWindowTokens: 200_000,
            MaxOutputTokens: 64_000);

        request.ContextWindowTokens.Should().Be(200_000);
        request.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void CreateRequest_DefaultsContextWindowToNull()
    {
        var request = new CreateSettingsModelRequest(
            ModelId: "test-model",
            DisplayName: "Test Model",
            Provider: "openai-chat",
            Description: null,
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: null);

        request.ContextWindowTokens.Should().BeNull();
        request.MaxOutputTokens.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~SettingsModelContextWindowTests"`
Expected: FAIL — `CreateSettingsModelRequest` has no `ContextWindowTokens` parameter.

- [ ] **Step 3: Add the fields to all three records**

In `src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs`, append two parameters to the end of `SettingsModelDto`, `CreateSettingsModelRequest`, and `UpdateSettingsModelRequest`. For each, add after `RequestFieldsWhenToolsPresentJson`:

```csharp
    string RequestFieldsWhenToolsPresentJson = "{}",
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null);
```

Appending as optional trailing parameters keeps the existing positional construction in `LocalModelOperationService.cs:586` compiling unchanged.

- [ ] **Step 4: Map them in `ToSettingsModelDto`**

In `src/server/GuideAntsApi/Settings/ApplicationSettingsService.Models.cs`, extend the constructor call in `ToSettingsModelDto` (around line 318) with two more arguments after `model.RequestFieldsWhenToolsPresentJson`:

```csharp
            model.RequestFieldsWhenToolsPresentJson,
            model.ContextWindowTokens,
            model.MaxOutputTokens);
```

- [ ] **Step 5: Assign them on create**

In `CreateModelAsync`, inside the `new Model { ... }` initializer, add after `DisplayOrder = request.DisplayOrder,`:

```csharp
            ContextWindowTokens = request.ContextWindowTokens,
            MaxOutputTokens = request.MaxOutputTokens,
```

- [ ] **Step 6: Assign them on update**

In `UpdateModelAsync`, add after `model.DisplayOrder = request.DisplayOrder;`:

```csharp
        model.ContextWindowTokens = request.ContextWindowTokens;
        model.MaxOutputTokens = request.MaxOutputTokens;
```

- [ ] **Step 7: Run tests**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~SettingsModelContextWindowTests"`
Expected: PASS (2 tests).

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/server/GuideAntsApi/Models/Settings/SettingsDtos.cs \
        src/server/GuideAntsApi/Settings/ApplicationSettingsService.Models.cs \
        src/server/GuideAntsApi.Tests/Settings/SettingsModelContextWindowTests.cs
git commit -m "Exposes model context window through the settings model API"
```

---

## Task 4: Catalog DTO plumbing

`ModelDto` is the catalog shape consumed by Guide Builder and the notebook runtime. It has two construction sites.

**Files:**
- Modify: `src/server/GuideAntsApi/Models/Guides/CatalogDto.cs:23-34`
- Modify: `src/server/GuideAntsApi/Services/Guides/CatalogService.cs:123`
- Modify: `src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs:655`

**Interfaces:**
- Consumes: `Model.ContextWindowTokens`, `Model.MaxOutputTokens` (Task 1)
- Produces: `ModelDto.ContextWindowTokens` (`int?`), `ModelDto.MaxOutputTokens` (`int?`)

- [ ] **Step 1: Add the fields to `ModelDto`**

In `src/server/GuideAntsApi/Models/Guides/CatalogDto.cs`, append to the `ModelDto` record:

```csharp
    IReadOnlyList<string>? ReasoningChoices,
    string? DefaultReasoningChoice,
    int? ContextWindowTokens = null,
    int? MaxOutputTokens = null
);
```

- [ ] **Step 2: Populate at both construction sites**

At `src/server/GuideAntsApi/Services/Guides/CatalogService.cs:123` and
`src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs:655`, append the two values to each `new ModelDto(...)` call, reading from the catalog row already in scope. In `CatalogService` the row variable is the `Model` being projected; in `NotebookModelRuntimeService` it is the `m` lambda parameter:

```csharp
            m.ContextWindowTokens,
            m.MaxOutputTokens
```

If either site projects from an anonymous type or a `Select` that does not already include these columns, add them to that projection first — otherwise EF will throw at query translation.

- [ ] **Step 3: Build and run the unit suite**

Run: `dotnet build src/server/GuideAntsApi/GuideAntsApi.csproj`
Expected: succeeds.

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/server/GuideAntsApi/Models/Guides/CatalogDto.cs \
        src/server/GuideAntsApi/Services/Guides/CatalogService.cs \
        src/server/GuideAntsApi/Services/LlamaCpp/NotebookModelRuntimeService.cs
git commit -m "Adds context window to the model catalog DTO"
```

---

## Task 5: Learned context-window cache

When a provider rejects a request for exceeding its window, `ChatContextOverflowException` already carries `ContextSize`. Remembering it gives unknown models a real value after one encounter. This is metadata learning only — it must never trigger compaction.

**Files:**
- Create: `src/server/GuideAntsApi/Services/Routing/LearnedContextWindowCache.cs`
- Create: `src/server/GuideAntsApi.Tests/Services/Routing/LearnedContextWindowCacheTests.cs`

**Interfaces:**
- Consumes: `AntRunner.Chat.Abstractions.ChatContextOverflowException.ContextSize` (`int?`)
- Produces: `ILearnedContextWindowCache` with `void Record(string modelId, int? contextSize)` and `int? Get(string modelId)`

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Routing/LearnedContextWindowCacheTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class LearnedContextWindowCacheTests
{
    [TestMethod]
    public void Get_ReturnsNull_WhenNothingLearned()
    {
        var cache = new LearnedContextWindowCache();
        cache.Get("unseen-model").Should().BeNull();
    }

    [TestMethod]
    public void Record_ThenGet_ReturnsLearnedValue()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("some-model", 8192);
        cache.Get("some-model").Should().Be(8192);
    }

    [TestMethod]
    public void Record_IgnoresNullAndNonPositiveValues()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("m", null);
        cache.Record("m", 0);
        cache.Record("m", -1);
        cache.Get("m").Should().BeNull();
    }

    [TestMethod]
    public void Record_KeepsSmallestObservedWindow()
    {
        // A larger later value may come from a different deployment of the same id.
        // The smallest observed window is the safe one for a headroom estimate.
        var cache = new LearnedContextWindowCache();
        cache.Record("m", 32_000);
        cache.Record("m", 8_192);
        cache.Record("m", 16_000);
        cache.Get("m").Should().Be(8_192);
    }

    [TestMethod]
    public void Get_IsCaseInsensitiveOnModelId()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("Some-Model", 4096);
        cache.Get("some-model").Should().Be(4096);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~LearnedContextWindowCacheTests"`
Expected: FAIL — `LearnedContextWindowCache` does not exist.

- [ ] **Step 3: Implement the cache**

Create `src/server/GuideAntsApi/Services/Routing/LearnedContextWindowCache.cs`:

```csharp
using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Remembers context-window sizes reported by provider rejections, so a model with no
/// catalog value still yields a usable estimate after one overflow. Metadata only —
/// recording a value never triggers compaction or alters request shaping.
/// </summary>
public interface ILearnedContextWindowCache
{
    void Record(string modelId, int? contextSize);
    int? Get(string modelId);
}

/// <inheritdoc />
public sealed class LearnedContextWindowCache : ILearnedContextWindowCache
{
    private readonly ConcurrentDictionary<string, int> _learned =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string modelId, int? contextSize)
    {
        if (string.IsNullOrWhiteSpace(modelId) || contextSize is not > 0)
        {
            return;
        }

        // Keep the smallest observed window. The same model id can front deployments with
        // different limits (notably llama.cpp), and the smallest is the one that rejects.
        _learned.AddOrUpdate(
            modelId,
            contextSize.Value,
            (_, existing) => Math.Min(existing, contextSize.Value));
    }

    public int? Get(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && _learned.TryGetValue(modelId, out var value)
            ? value
            : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~LearnedContextWindowCacheTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Register as a singleton**

Find the DI registration block in `src/server/GuideAntsApi/Program.cs` where other routing services are registered (search for `IChatTargetResolver`) and add alongside them:

```csharp
builder.Services.AddSingleton<ILearnedContextWindowCache, LearnedContextWindowCache>();
```

- [ ] **Step 6: Build**

Run: `dotnet build src/server/GuideAntsApi/GuideAntsApi.csproj`
Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/server/GuideAntsApi/Services/Routing/LearnedContextWindowCache.cs \
        src/server/GuideAntsApi/Program.cs \
        src/server/GuideAntsApi.Tests/Services/Routing/LearnedContextWindowCacheTests.cs
git commit -m "Remembers context window sizes reported by provider rejections"
```

---

## Task 6: Context-window resolution precedence

Resolution order from the spec: live llama.cpp runtime → catalog → learned → unknown.

**Files:**
- Create: `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs`
- Create: `src/server/GuideAntsApi/Services/Routing/ContextWindowResolver.cs`
- Create: `src/server/GuideAntsApi.Tests/Services/Routing/ContextWindowResolverTests.cs`

**Interfaces:**
- Consumes: `ILearnedContextWindowCache` (Task 5), `ChatTarget.ContextWindowTokens` / `.MaxOutputTokens` (Task 2)
- Produces: `IContextWindowResolver.Resolve(string modelId, int? liveRuntimeContextSize)` returning `ContextWindowInfo(int? ContextWindowTokens, int? MaxOutputTokens, ContextWindowSource Source)`

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Routing/ContextWindowResolverTests.cs`:

```csharp
using FluentAssertions;
using GuideAntsApi.Services.Routing;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ContextWindowResolverTests
{
    private static IContextWindowResolver Build(
        int? catalogWindow,
        int? catalogMaxOutput = null,
        int? learned = null)
    {
        var targets = new Mock<IChatTargetResolver>();
        targets.Setup(t => t.Resolve(It.IsAny<string>()))
            .Returns(new ChatTarget(
                "m", "openai-chat", null, null, catalogWindow, catalogMaxOutput));

        var cache = new Mock<ILearnedContextWindowCache>();
        cache.Setup(c => c.Get(It.IsAny<string>())).Returns(learned);

        return new ContextWindowResolver(targets.Object, cache.Object);
    }

    [TestMethod]
    public void LiveRuntimeValue_WinsOverCatalog()
    {
        var result = Build(catalogWindow: 128_000).Resolve("m", liveRuntimeContextSize: 8_192);

        result.ContextWindowTokens.Should().Be(8_192);
        result.Source.Should().Be(ContextWindowSource.LiveRuntime);
    }

    [TestMethod]
    public void Catalog_WinsOverLearned()
    {
        var result = Build(catalogWindow: 128_000, learned: 8_192)
            .Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().Be(128_000);
        result.Source.Should().Be(ContextWindowSource.Catalog);
    }

    [TestMethod]
    public void Learned_UsedWhenCatalogIsNull()
    {
        var result = Build(catalogWindow: null, learned: 8_192)
            .Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().Be(8_192);
        result.Source.Should().Be(ContextWindowSource.Learned);
    }

    [TestMethod]
    public void Unknown_WhenNoSourceHasAValue()
    {
        var result = Build(catalogWindow: null).Resolve("m", liveRuntimeContextSize: null);

        result.ContextWindowTokens.Should().BeNull();
        result.MaxOutputTokens.Should().BeNull();
        result.Source.Should().Be(ContextWindowSource.Unknown);
    }

    [TestMethod]
    public void MaxOutputTokens_AlwaysComesFromCatalog()
    {
        // Only the catalog knows the output cap; a runtime window says nothing about it.
        var result = Build(catalogWindow: 128_000, catalogMaxOutput: 64_000)
            .Resolve("m", liveRuntimeContextSize: 8_192);

        result.ContextWindowTokens.Should().Be(8_192);
        result.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void NonPositiveLiveValue_IsIgnored()
    {
        var result = Build(catalogWindow: 128_000).Resolve("m", liveRuntimeContextSize: 0);

        result.ContextWindowTokens.Should().Be(128_000);
        result.Source.Should().Be(ContextWindowSource.Catalog);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ContextWindowResolverTests"`
Expected: FAIL — `IContextWindowResolver` does not exist.

- [ ] **Step 3: Define the interface**

Create `src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs`:

```csharp
namespace GuideAntsApi.Services.Routing;

/// <summary>Where a resolved context-window value came from.</summary>
public enum ContextWindowSource
{
    Unknown,
    Learned,
    Catalog,
    LiveRuntime
}

/// <summary>
/// A resolved context window. <see cref="ContextWindowTokens"/> is null when no source knows
/// the value — callers must render an explicit unknown state rather than assume a default.
/// </summary>
public sealed record ContextWindowInfo(
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    ContextWindowSource Source);

public interface IContextWindowResolver
{
    /// <param name="liveRuntimeContextSize">
    /// The loaded context size reported by a local runtime, when one is serving this model.
    /// Null for cloud models. This is the loaded window, not <c>n_ctx_train</c>.
    /// </param>
    ContextWindowInfo Resolve(string modelId, int? liveRuntimeContextSize);
}
```

- [ ] **Step 4: Implement the resolver**

Create `src/server/GuideAntsApi/Services/Routing/ContextWindowResolver.cs`:

```csharp
namespace GuideAntsApi.Services.Routing;

/// <inheritdoc />
public sealed class ContextWindowResolver : IContextWindowResolver
{
    private readonly IChatTargetResolver _targets;
    private readonly ILearnedContextWindowCache _learned;

    public ContextWindowResolver(
        IChatTargetResolver targets,
        ILearnedContextWindowCache learned)
    {
        _targets = targets;
        _learned = learned;
    }

    public ContextWindowInfo Resolve(string modelId, int? liveRuntimeContextSize)
    {
        var target = _targets.Resolve(modelId);

        // The output cap is only ever known by the catalog — a runtime window says nothing
        // about how much the model is willing to generate.
        var maxOutput = target.MaxOutputTokens;

        if (liveRuntimeContextSize is > 0)
        {
            return new ContextWindowInfo(
                liveRuntimeContextSize, maxOutput, ContextWindowSource.LiveRuntime);
        }

        if (target.ContextWindowTokens is > 0)
        {
            return new ContextWindowInfo(
                target.ContextWindowTokens, maxOutput, ContextWindowSource.Catalog);
        }

        var learned = _learned.Get(modelId);
        if (learned is > 0)
        {
            return new ContextWindowInfo(learned, maxOutput, ContextWindowSource.Learned);
        }

        return new ContextWindowInfo(null, maxOutput, ContextWindowSource.Unknown);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ContextWindowResolverTests"`
Expected: PASS (6 tests).

Note: the `Unknown_WhenNoSourceHasAValue` test asserts `MaxOutputTokens` is null because that test's catalog stub supplies no output cap. The resolver deliberately returns the catalog output cap even in the `Unknown` case — the two values are independent.

- [ ] **Step 6: Register the resolver**

In `src/server/GuideAntsApi/Program.cs`, beside the Task 5 registration:

```csharp
builder.Services.AddScoped<IContextWindowResolver, ContextWindowResolver>();
```

Use `AddScoped` to match `IChatTargetResolver`'s lifetime. If `IChatTargetResolver` is registered as a singleton, use `AddSingleton` instead — a scoped dependency inside a singleton throws at startup.

- [ ] **Step 7: Run the full unit suite**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/server/GuideAntsApi/Services/Routing/IContextWindowResolver.cs \
        src/server/GuideAntsApi/Services/Routing/ContextWindowResolver.cs \
        src/server/GuideAntsApi/Program.cs \
        src/server/GuideAntsApi.Tests/Services/Routing/ContextWindowResolverTests.cs
git commit -m "Resolves model context window by source precedence"
```

---

## Task 7: Seed known cloud model values

**Files:**
- Modify: `src/client/src/pages/settings/data/knownCloudModels.json`

**Interfaces:**
- Consumes: nothing
- Produces: optional `contextWindowTokens` / `maxOutputTokens` keys on each seed entry

> **This task is research, not typing.** Only one value below is verified from an
> authoritative source bundled with this repo's tooling. Every other model's numbers must be
> looked up before they are written. **A model whose value cannot be confirmed gets no key at
> all** — it then renders as "unknown", which is correct. Guessing produces a meter that lies.

- [ ] **Step 1: Look up each model's context window and max output**

The file has 19 entries. Sources, in order of authority:

| Provider | Models | Source |
|---|---|---|
| Anthropic | `claude-opus-4-5`, `claude-sonnet-4-5`, `claude-haiku-4-5` | Models API (`max_input_tokens`, `max_tokens`) via `client.models.retrieve(id)`, or docs.claude.com model comparison table |
| OpenAI | `gpt-4.1*`, `gpt-4o*`, `gpt-5*`, `o3`, `o4-mini` | platform.openai.com model reference pages |
| Google | `gemini-2.5-pro`, `gemini-2.5-flash` | ai.google.dev model pages |
| OpenRouter | `minimax/minimax-m3` | `GET https://openrouter.ai/api/v1/models` → `context_length` (no auth needed) |

Verified value available now: **`claude-haiku-4-5` — 200,000 context / 64,000 max output.**

For the Anthropic models, `claude-opus-4-5` and `claude-sonnet-4-5` are not in the bundled cached table; retrieve them from the Models API or the published comparison table. Do not infer them from newer Opus/Sonnet versions — context windows changed between generations.

- [ ] **Step 2: Add the keys to entries you verified**

Each entry gains up to two optional keys. Example for the one verified model:

```json
  {
    "modelId": "claude-haiku-4-5",
    "displayName": "Claude Haiku 4.5",
    "providers": ["anthropic"],
    "description": "Fastest Claude model for low-latency tasks.",
    "parameterSurfaceSeed": "anthropic_standard",
    "reasoningEffortEnabled": false,
    "contextWindowTokens": 200000,
    "maxOutputTokens": 64000
  }
```

Keep every existing key exactly as it is — only add. Omit both keys entirely for any model you could not verify.

- [ ] **Step 3: Verify the file still parses and report coverage**

```bash
cd src/client && node -e "
const d=require('./src/pages/settings/data/knownCloudModels.json');
const withWin=d.filter(m=>m.contextWindowTokens);
console.log(\`\${withWin.length}/\${d.length} models have a context window\`);
d.filter(m=>!m.contextWindowTokens).forEach(m=>console.log('  unverified:', m.modelId));
"
```

Expected: valid JSON, and a printed list of any models left unverified. That list is the honest state of the seed — not a failure.

- [ ] **Step 4: Commit**

```bash
git add src/client/src/pages/settings/data/knownCloudModels.json
git commit -m "Seeds context window values for known cloud models"
```

---

## Task 8: Back-fill existing catalog rows

Model catalog rows are created entirely by admins through Settings — there is **no** server-side
model seeder to extend. So an existing install has rows with null windows, and Task 7's client
JSON only helps models added *after* this ships. Without a back-fill, the W8 meter reads
"unknown" forever on every existing deployment.

> **Known duplication.** This task puts the same ~19 numbers in a server-side table that Task 7
> put in the client seed JSON. The client file carries other fields the wizard needs
> (`parameterSurfaceSeed`, `reasoningEffortEnabled`, `providers`), so it cannot simply be
> deleted, and the server cannot read a client-project file at runtime in a container. Accept
> the duplication for W1; a follow-up should make one the generated artifact of the other. Keep
> the two files' values identical — a divergence means a model's seeded value differs from its
> back-filled value.

**Files:**
- Create: `src/server/GuideAntsApi/Services/Bootstrap/KnownModelContextWindows.cs`
- Create: `src/server/GuideAntsApi/Services/Bootstrap/ModelContextWindowBackfill.cs`
- Create: `src/server/GuideAntsApi.Tests/Services/Bootstrap/ModelContextWindowBackfillTests.cs`
- Modify: `src/server/GuideAntsApi/Program.cs`

**Interfaces:**
- Consumes: `Model.ContextWindowTokens` / `.MaxOutputTokens` (Task 1), the values verified in Task 7
- Produces: `KnownModelContextWindows.TryGet(string modelId, out int contextWindow, out int? maxOutput)`, `ModelContextWindowBackfill.RunAsync(ApplicationDbContext, CancellationToken)`

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Bootstrap/ModelContextWindowBackfillTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowBackfillTests"`
Expected: FAIL — `ModelContextWindowBackfill` does not exist.

- [ ] **Step 3: Create the value table**

Create `src/server/GuideAntsApi/Services/Bootstrap/KnownModelContextWindows.cs`. Populate it with
**exactly the models you verified in Task 7** — same values, same omissions:

```csharp
namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Context-window values for well-known hosted models, used to back-fill catalog rows that
/// predate the context-window columns. A model absent from this table keeps a null window and
/// renders as "unknown" — which is correct. Never add a value that has not been verified
/// against the provider's own documentation.
///
/// These values are duplicated in the client seed at
/// src/client/src/pages/settings/data/knownCloudModels.json. Keep the two identical.
/// </summary>
internal static class KnownModelContextWindows
{
    private static readonly Dictionary<string, (int ContextWindow, int? MaxOutput)> Values =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-haiku-4-5"] = (200_000, 64_000),
            // Add one entry per model verified in Task 7. Omit anything unverified.
        };

    internal static bool TryGet(string modelId, out int contextWindow, out int? maxOutput)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && Values.TryGetValue(modelId, out var found))
        {
            contextWindow = found.ContextWindow;
            maxOutput = found.MaxOutput;
            return true;
        }

        contextWindow = 0;
        maxOutput = null;
        return false;
    }
}
```

- [ ] **Step 4: Implement the back-fill**

Create `src/server/GuideAntsApi/Services/Bootstrap/ModelContextWindowBackfill.cs`:

```csharp
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Fills context-window values on catalog rows that have none, for models we know. Runs at
/// startup and is idempotent. Never overwrites a non-null value — an admin-entered number is
/// authoritative over this table.
/// </summary>
public static class ModelContextWindowBackfill
{
    public static async Task RunAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var candidates = await db.Models
            .Where(m => m.ContextWindowTokens == null)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var model in candidates)
        {
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
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowBackfillTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Call it at startup**

In `src/server/GuideAntsApi/Program.cs`, find where the app runs its startup database work after
migrations (search for `GuideAntsSystemSeeder` — the bootstrap seeders run together there) and add
the back-fill alongside it, inside the same scope and using that block's existing `ApplicationDbContext`
and cancellation token:

```csharp
await ModelContextWindowBackfill.RunAsync(db, cancellationToken);
```

It must run **after** migrations have applied, or the columns will not exist yet.

- [ ] **Step 7: Run the full unit suite**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/server/GuideAntsApi/Services/Bootstrap/KnownModelContextWindows.cs \
        src/server/GuideAntsApi/Services/Bootstrap/ModelContextWindowBackfill.cs \
        src/server/GuideAntsApi/Program.cs \
        src/server/GuideAntsApi.Tests/Services/Bootstrap/ModelContextWindowBackfillTests.cs
git commit -m "Back-fills context window values on existing catalog rows"
```

---

## Task 9: Settings UI editing

**Files:**
- Modify: `src/client/src/pages/settings/components/ModelsTab.tsx`
- Modify: `src/client/src/pages/settings/components/catalog/AddModelWizard.tsx`
- Modify: the client model type where `SettingsModelDto` is mirrored (find with the grep in Step 1)

**Interfaces:**
- Consumes: `SettingsModelDto.contextWindowTokens` / `.maxOutputTokens` (Task 3), seed keys (Task 7)
- Produces: editable numeric inputs persisting through the existing create/update calls

- [ ] **Step 1: Locate the client-side model type**

```bash
cd src/client && grep -rn "requestFieldsWhenToolsPresentJson" src --include=*.ts --include=*.tsx | grep -v test
```

The file defining that field alongside `modelId` / `displayName` is the mirror of `SettingsModelDto`. Add to it:

```typescript
  contextWindowTokens?: number | null;
  maxOutputTokens?: number | null;
```

Add the same two optional fields to the create and update request types in the same file.

- [ ] **Step 2: Write the failing test**

Add to `src/client/src/pages/settings/components/__tests__/ModelsTab.catalogEdit.test.tsx`, following the existing test file's setup helpers and imports:

This file mocks the API module at `vi.mock('../../../../services/api')` and asserts through
`vi.mocked(api.settings.updateModel)`. It has no shared render helper — each test calls
`render(<ModelsTab ... />)` inline with the `cloudModel` / `profile` fixtures defined at the top
of the file. Follow that existing shape; copy the full prop list from the neighbouring test
rather than inventing a helper:

```tsx
it('persists an edited context window', async () => {
  const user = userEvent.setup();
  render(/* same <ModelsTab .../> element and props as the test above this one */);

  await user.click(await screen.findByRole('button', { name: /edit/i }));

  const windowInput = await screen.findByLabelText(/context window/i);
  await user.clear(windowInput);
  await user.type(windowInput, '200000');

  await user.click(screen.getByRole('button', { name: /save/i }));

  expect(vi.mocked(api.settings.updateModel)).toHaveBeenCalledWith(
    expect.objectContaining({ contextWindowTokens: 200000 }),
  );
});

it('sends null when the context window field is cleared', async () => {
  const user = userEvent.setup();
  render(/* same <ModelsTab .../> element and props as the test above this one */);

  await user.click(await screen.findByRole('button', { name: /edit/i }));
  await user.clear(await screen.findByLabelText(/context window/i));
  await user.click(screen.getByRole('button', { name: /save/i }));

  expect(vi.mocked(api.settings.updateModel)).toHaveBeenCalledWith(
    expect.objectContaining({ contextWindowTokens: null }),
  );
});
```

Add `contextWindowTokens: 200000` to the `cloudModel: SettingsModelDto` fixture so the first
test has a value to clear, and import `userEvent` if the file does not already.

- [ ] **Step 3: Run test to verify it fails**

Run: `cd src/client && npx vitest run src/pages/settings/components/__tests__/ModelsTab.catalogEdit.test.tsx`
Expected: FAIL — no element labelled "context window".

- [ ] **Step 4: Add the inputs to `ModelsTab.tsx`**

In the model edit form, beside the existing `displayOrder` numeric input, add two fields following that input's existing markup and class conventions:

```tsx
<label className="block text-sm font-medium" htmlFor="contextWindowTokens">
  Context window (tokens)
</label>
<input
  id="contextWindowTokens"
  type="number"
  min={1}
  value={draft.contextWindowTokens ?? ''}
  placeholder="Unknown"
  onChange={(e) =>
    setDraft({
      ...draft,
      contextWindowTokens: e.target.value === '' ? null : Number(e.target.value),
    })
  }
/>
<p className="text-xs text-gray-500">
  Leave empty if unknown. An incorrect value makes the context meter misleading.
</p>
```

Repeat for `maxOutputTokens` with the label "Max output (tokens)". Match `draft` / `setDraft` to the existing state variable names in that component.

- [ ] **Step 5: Apply seeded values in `AddModelWizard.tsx`**

Where the wizard copies `parameterSurfaceSeed` and other fields off a selected `knownCloudModels` entry, also carry the two new keys:

```tsx
contextWindowTokens: seed.contextWindowTokens ?? null,
maxOutputTokens: seed.maxOutputTokens ?? null,
```

Add the same two numeric inputs from Step 4 to the wizard's model-details step, so a model absent from the seed file can still be given values at creation time.

- [ ] **Step 6: Run tests**

Run: `cd src/client && npx vitest run src/pages/settings/components/__tests__/ModelsTab.catalogEdit.test.tsx`
Expected: PASS.

Run: `cd src/client && npm run typecheck`
Expected: no errors from either tsconfig.

Run: `cd src/client && npx vitest run src/pages/settings`
Expected: PASS — `AddModelWizard.flow.test.tsx` and `AddModelWizard.providers.test.tsx` still pass.

- [ ] **Step 7: Commit**

```bash
git add src/client/src/pages/settings
git commit -m "Adds context window editing to model settings"
```

---

## Task 10: Fetch context window from the provider

Two of the four providers publish this data. Probing them removes those models from the manual
set entirely, covers models released after the seed file was written, and fixes the seed-decay
problem permanently for those providers. OpenAI and Google publish no such endpoint, so they
remain seed-or-manual.

The probe **returns** values for the admin to review and save. It never writes to the catalog
directly — same principle as the back-fill's null-only rule.

**Files:**
- Create: `src/server/GuideAntsApi/Services/Routing/IModelContextWindowProbe.cs`
- Create: `src/server/GuideAntsApi/Services/Routing/ModelContextWindowProbe.cs`
- Create: `src/server/GuideAntsApi.Tests/Services/Routing/ModelContextWindowProbeTests.cs`
- Modify: `src/server/GuideAntsApi/Endpoints/Settings/SettingsModelsEndpoints.cs`
- Modify: `src/server/GuideAntsApi/Program.cs`
- Modify: `src/client/src/pages/settings/components/ModelsTab.tsx`

**Interfaces:**
- Consumes: `IProviderConfigurationResolver.GetAnthropicConfig()` → `AnthropicConfig.ApiKey` / `.BaseUrl`; `IProviderConfigurationResolver.GetOpenRouterOptions()` → `OpenRouterOptions.BaseUrl`; `IHttpClientFactory`
- Produces: `IModelContextWindowProbe.ProbeAsync(string modelId, string provider, CancellationToken)` → `ContextWindowProbeResult(bool Supported, int? ContextWindowTokens, int? MaxOutputTokens, string? Message)`

- [ ] **Step 1: Write the failing test**

Create `src/server/GuideAntsApi.Tests/Services/Routing/ModelContextWindowProbeTests.cs`:

```csharp
using System.Net;
using System.Text;
using AntRunner.Chat.Anthropic;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Moq;
using Moq.Protected;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ModelContextWindowProbeTests
{
    private static IModelContextWindowProbe Build(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler.Object));

        var providers = new Mock<IProviderConfigurationResolver>();
        providers.Setup(p => p.GetAnthropicConfig())
            .Returns(new AnthropicConfig { ApiKey = "test-key", BaseUrl = "https://api.anthropic.com" });
        providers.Setup(p => p.GetOpenRouterOptions())
            .Returns(new OpenRouterOptions { BaseUrl = "https://openrouter.ai/api/v1" });

        return new ModelContextWindowProbe(factory.Object, providers.Object);
    }

    [TestMethod]
    public async Task Anthropic_ParsesMaxInputAndMaxTokens()
    {
        var probe = Build("""
            {"id":"claude-haiku-4-5","max_input_tokens":200000,"max_tokens":64000}
            """);

        var result = await probe.ProbeAsync("claude-haiku-4-5", "anthropic", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(200_000);
        result.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public async Task OpenRouter_ParsesContextLengthForMatchingId()
    {
        var probe = Build("""
            {"data":[
              {"id":"other/model","context_length":8192,
               "top_provider":{"max_completion_tokens":1024}},
              {"id":"minimax/minimax-m3","context_length":1000000,
               "top_provider":{"max_completion_tokens":128000}}
            ]}
            """);

        var result = await probe.ProbeAsync("minimax/minimax-m3", "openrouter-chat", CancellationToken.None);

        result.Supported.Should().BeTrue();
        result.ContextWindowTokens.Should().Be(1_000_000);
        result.MaxOutputTokens.Should().Be(128_000);
    }

    [TestMethod]
    public async Task OpenRouter_ReturnsNullValues_WhenModelIdNotListed()
    {
        var probe = Build("""{"data":[{"id":"other/model","context_length":8192}]}""");

        var result = await probe.ProbeAsync("missing/model", "openrouter-chat", CancellationToken.None);

        result.Supported.Should().BeTrue("the provider supports probing even when the model is absent");
        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task UnsupportedProvider_ReportsUnsupported()
    {
        var probe = Build("{}");

        var result = await probe.ProbeAsync("gpt-4.1", "openai-chat", CancellationToken.None);

        result.Supported.Should().BeFalse();
        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().Contain("does not publish");
    }

    [TestMethod]
    public async Task HttpFailure_ReturnsMessageRatherThanThrowing()
    {
        var probe = Build("""{"error":"nope"}""", HttpStatusCode.Unauthorized);

        var result = await probe.ProbeAsync("claude-haiku-4-5", "anthropic", CancellationToken.None);

        result.ContextWindowTokens.Should().BeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowProbeTests"`
Expected: FAIL — `IModelContextWindowProbe` does not exist.

If `Moq.Protected` is unavailable, it ships inside the `Moq` package already referenced by the
test project — add `using Moq.Protected;` only, no new PackageReference.

- [ ] **Step 3: Define the interface**

Create `src/server/GuideAntsApi/Services/Routing/IModelContextWindowProbe.cs`:

```csharp
namespace GuideAntsApi.Services.Routing;

/// <param name="Supported">False when the provider publishes no capability endpoint at all.</param>
/// <param name="Message">Human-readable explanation when values could not be determined.</param>
public sealed record ContextWindowProbeResult(
    bool Supported,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    string? Message);

/// <summary>
/// Asks a provider what a model's context window is, where the provider publishes that.
/// Returns values for an admin to review — never writes to the catalog itself.
/// </summary>
public interface IModelContextWindowProbe
{
    Task<ContextWindowProbeResult> ProbeAsync(
        string modelId,
        string provider,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the probe**

Create `src/server/GuideAntsApi/Services/Routing/ModelContextWindowProbe.cs`:

```csharp
using System.Text.Json;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

/// <inheritdoc />
public sealed class ModelContextWindowProbe : IModelContextWindowProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderConfigurationResolver _providers;

    public ModelContextWindowProbe(
        IHttpClientFactory httpClientFactory,
        IProviderConfigurationResolver providers)
    {
        _httpClientFactory = httpClientFactory;
        _providers = providers;
    }

    public async Task<ContextWindowProbeResult> ProbeAsync(
        string modelId,
        string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return provider?.ToLowerInvariant() switch
            {
                "anthropic" => await ProbeAnthropicAsync(modelId, cancellationToken),
                "openrouter-chat" => await ProbeOpenRouterAsync(modelId, cancellationToken),
                _ => new ContextWindowProbeResult(
                    false, null, null,
                    $"'{provider}' does not publish model context windows. Enter the value manually.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ContextWindowProbeResult(true, null, null, $"Probe failed: {ex.Message}");
        }
    }

    private async Task<ContextWindowProbeResult> ProbeAnthropicAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        var config = _providers.GetAnthropicConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new ContextWindowProbeResult(
                true, null, null, "No Anthropic API key is configured.");
        }

        var baseUrl = (config.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models/{modelId}");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ContextWindowProbeResult(
                true, null, null, $"Anthropic returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new ContextWindowProbeResult(
            true,
            ReadInt(root, "max_input_tokens"),
            ReadInt(root, "max_tokens"),
            null);
    }

    private async Task<ContextWindowProbeResult> ProbeOpenRouterAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        var baseUrl = (_providers.GetOpenRouterOptions().BaseUrl ?? "https://openrouter.ai/api/v1")
            .TrimEnd('/');

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"{baseUrl}/models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ContextWindowProbeResult(
                true, null, null, $"OpenRouter returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return new ContextWindowProbeResult(
                true, null, null, "OpenRouter returned an unexpected response shape.");
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int? maxOutput = null;
            if (entry.TryGetProperty("top_provider", out var top))
            {
                maxOutput = ReadInt(top, "max_completion_tokens");
            }

            return new ContextWindowProbeResult(
                true, ReadInt(entry, "context_length"), maxOutput, null);
        }

        return new ContextWindowProbeResult(
            true, null, null, $"'{modelId}' is not listed in the OpenRouter catalog.");
    }

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
        && parsed > 0
            ? parsed
            : null;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj --filter "FullyQualifiedName~ModelContextWindowProbeTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Register and expose the endpoint**

In `src/server/GuideAntsApi/Program.cs`, beside the other Task 5/6 registrations:

```csharp
builder.Services.AddScoped<IModelContextWindowProbe, ModelContextWindowProbe>();
```

In `src/server/GuideAntsApi/Endpoints/Settings/SettingsModelsEndpoints.cs`, add a route beside the
existing `MapPut("/models/{**modelId}")`. Use the `{**modelId}` catch-all — model ids contain
slashes (`minimax/minimax-m3`), which is why the existing routes use it:

```csharp
        group.MapPost("/models/{**modelId}:probe-context-window", async (
            string modelId,
            string provider,
            IModelContextWindowProbe probe,
            CancellationToken cancellationToken) =>
        {
            var result = await probe.ProbeAsync(modelId, provider, cancellationToken);
            return Results.Ok(result);
        });
```

Match the surrounding routes' authorization and naming conventions — copy whatever
`.RequireAuthorization(...)` or filter chain the neighbouring model routes carry.

- [ ] **Step 7: Add the client action**

In `src/client/src/pages/settings/components/ModelsTab.tsx`, beside the two numeric inputs added
in Task 9, add a button that calls the endpoint and fills the form without saving:

```tsx
<button
  type="button"
  disabled={probing}
  onClick={async () => {
    setProbing(true);
    try {
      const result = await api.settings.probeModelContextWindow(draft.modelId, draft.provider);
      if (result.contextWindowTokens) {
        setDraft((d) => ({
          ...d,
          contextWindowTokens: result.contextWindowTokens,
          maxOutputTokens: result.maxOutputTokens ?? d.maxOutputTokens,
        }));
      }
      setProbeMessage(result.message ?? null);
    } finally {
      setProbing(false);
    }
  }}
>
  Fetch from provider
</button>
{probeMessage && <p className="text-xs text-amber-600">{probeMessage}</p>}
```

Add the matching `probeModelContextWindow` function to the settings API client alongside
`updateModel`, and declare `probing` / `probeMessage` with `useState` in the component. Hide or
disable the button for providers the probe does not support (`openai-chat`,
`openai-responses`, `azure-openai-*`, `google-gemini-chat`, `hf-inference-chat`, `llama-cpp`).

- [ ] **Step 8: Test the client action**

Add to `ModelsTab.catalogEdit.test.tsx`, following the file's existing mock style — extend the
`vi.mock('../../../../services/api')` factory with `probeModelContextWindow: vi.fn()`:

```tsx
it('fills the context window from a provider probe without saving', async () => {
  const user = userEvent.setup();
  vi.mocked(api.settings.probeModelContextWindow).mockResolvedValue({
    supported: true,
    contextWindowTokens: 200000,
    maxOutputTokens: 64000,
    message: null,
  });

  render(/* same <ModelsTab .../> element and props as the test above this one */);

  await user.click(await screen.findByRole('button', { name: /edit/i }));
  await user.click(screen.getByRole('button', { name: /fetch from provider/i }));

  expect(await screen.findByLabelText(/context window/i)).toHaveValue(200000);
  expect(vi.mocked(api.settings.updateModel)).not.toHaveBeenCalled();
});
```

Run: `cd src/client && npx vitest run src/pages/settings/components/__tests__/ModelsTab.catalogEdit.test.tsx`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/server/GuideAntsApi/Services/Routing/IModelContextWindowProbe.cs \
        src/server/GuideAntsApi/Services/Routing/ModelContextWindowProbe.cs \
        src/server/GuideAntsApi/Endpoints/Settings/SettingsModelsEndpoints.cs \
        src/server/GuideAntsApi/Program.cs \
        src/server/GuideAntsApi.Tests/Services/Routing/ModelContextWindowProbeTests.cs \
        src/client/src/pages/settings
git commit -m "Fetches model context window from providers that publish it"
```

---

## Task 11: Full verification

- [ ] **Step 1: Run the server unit suite**

Run: `dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj`
Expected: PASS, no skips beyond pre-existing ones.

- [ ] **Step 2: Run the client suite with coverage**

Run: `cd src/client && npm run test:coverage`
Expected: PASS, ≥85% lines.

- [ ] **Step 3: Apply the migration against a real database**

Start the DB: `docker compose -f docker/docker-compose.mssql.yml up -d`

Run from `src/server`:

```bash
dotnet ef database update \
  --project GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj \
  --startup-project GuideAntsApi/GuideAntsApi.csproj
```

Expected: applies cleanly. EF InMemory does not exercise real DDL, so this is the only check that the migration is valid against SQL Server.

- [ ] **Step 4: Confirm no behavior changed**

Start the API (`dotnet run --project GuideAntsApi`) and run one chat turn in a notebook. Confirm it behaves exactly as before — W1 adds metadata only and must not alter request shaping. Confirm Settings → Models shows the new fields and that saving a value round-trips after a page reload.

- [ ] **Step 5: Commit any fixes and push**

```bash
git push origin feature/compaction
```

---

## Out of scope for W1

Deliberately excluded — these belong to later workstreams:

- Calling `ILearnedContextWindowCache.Record` from the overflow path. The cache is built and registered here but nothing writes to it yet; wiring it to `ChatContextOverflowException` happens in W6 alongside the overflow-handling rewrite.
- Token estimation and the `lastRoundPromptTokens` fix (W2).
- The context meter UI itself (W8) — W1 only makes the number available.
- Any compaction behavior whatsoever.
