using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class LocalModelPersistencePhase1BTests
{
    private static readonly byte[] DefaultRowVersion = [1, 0, 0, 0, 0, 0, 0, 0];

    [TestMethod]
    public async Task LocalModelInstallation_OneToOneWithModel_PersistsRoundTrip()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = "qwen-local",
            ManagementMode = "curated",
            CatalogId = "qwen3.6-35b-a3b-mtp",
            CatalogVersion = "2026-07-10",
            Repository = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            RequestedRevision = "main",
            ResolvedRevision = "8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a",
            QuantId = "q6_k_xl",
            QuantLabel = "Q6_K_XL",
            RouterModelId = "Qwen3.6-35B-A3B-MTP-GGUF",
            TargetDirectory = "Qwen3.6-35B-A3B-MTP-GGUF",
            ModelArtifactsJson = """[{"repositoryPath":"a.gguf","installedRelativePath":"a/a.gguf","byteSize":1}]""",
            ProjectorArtifactsJson = "[]",
            RouterPresetSnapshotJson = """{"ctx-size":"131072"}""",
            RowVersion = DefaultRowVersion
        });
        await db.SaveChangesAsync();

        var loaded = await db.LocalModelInstallations
            .AsNoTracking()
            .SingleAsync(x => x.ModelId == "qwen-local");

        loaded.ManagementMode.Should().Be("curated");
        loaded.CatalogId.Should().Be("qwen3.6-35b-a3b-mtp");
        loaded.RouterPresetSnapshotJson.Should().Contain("ctx-size");
    }

    [TestMethod]
    public async Task LocalModelOperation_ImmutableInput_PersistsStatusAndCorrelation()
    {
        await using var db = CreateDbContext();
        var operationId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationId = operationId,
            OperationKind = "curatedInstall",
            ModelId = "qwen-local",
            RouterModelId = "Qwen3.6-35B-A3B-MTP-GGUF",
            ImmutableInputJson = """{"definitionId":"qwen3.6-35b-a3b-mtp"}""",
            Status = "downloading",
            CurrentStep = "downloadModelFile",
            CompletedSideEffectsJson = "{}",
            RowVersion = DefaultRowVersion
        });
        await db.SaveChangesAsync();

        var loaded = await db.LocalModelOperations.AsNoTracking().SingleAsync(x => x.OperationId == operationId);
        loaded.Status.Should().Be("downloading");
        loaded.ImmutableInputJson.Should().Contain("definitionId");
    }

    [TestMethod]
    public async Task ModelDelete_CascadesInstallation_NotOperations()
    {
        await using var db = CreateDbContext();
        db.Models.Add(new Model
        {
            ModelId = "delete-me",
            DisplayName = "Delete Me",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow
        });
        db.LocalModelInstallations.Add(new LocalModelInstallation
        {
            ModelId = "delete-me",
            ManagementMode = "operatorManaged",
            ModelArtifactsJson = "[]",
            ProjectorArtifactsJson = "[]",
            RouterPresetSnapshotJson = "{}",
            RowVersion = DefaultRowVersion
        });
        db.LocalModelOperations.Add(new LocalModelOperation
        {
            OperationKind = "repair",
            ModelId = "delete-me",
            ImmutableInputJson = "{}",
            Status = "completed",
            CompletedSideEffectsJson = "{}",
            RowVersion = DefaultRowVersion
        });
        await db.SaveChangesAsync();

        var model = await db.Models.SingleAsync(m => m.ModelId == "delete-me");
        db.Models.Remove(model);
        await db.SaveChangesAsync();

        (await db.LocalModelInstallations.CountAsync()).Should().Be(0);
        (await db.LocalModelOperations.CountAsync()).Should().Be(1);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"phase1b-persistence-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}
