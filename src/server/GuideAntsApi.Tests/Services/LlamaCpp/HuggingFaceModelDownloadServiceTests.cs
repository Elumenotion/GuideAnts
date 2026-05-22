using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public class HuggingFaceModelDownloadServiceTests
{
    [TestMethod]
    public async Task StartDownloadAsync_DelegatesToAdminClient()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>(MockBehavior.Strict);
        var options = new Mock<IOptionsMonitor<LlamaModelManagementOptions>>(MockBehavior.Strict);
        var request = CreateRequest();

        tokenResolver.Setup(x => x.Resolve()).Returns("hf_token");
        options.SetupGet(x => x.CurrentValue).Returns(new LlamaModelManagementOptions { AllowOverwrite = true });
        adminClient
            .Setup(x => x.StartDownloadAsync(
                request,
                "hf_token",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-123",
                Status: "queued",
                RouterModelId: request.RouterModelId,
                Progress: 0,
                ErrorMessage: null,
                LogLine: "queued"));

        var service = new HuggingFaceModelDownloadService(
            adminClient.Object,
            tokenResolver.Object,
            options.Object,
            NullLogger<HuggingFaceModelDownloadService>.Instance);

        var op = await service.StartDownloadAsync(request, CancellationToken.None);

        op.OperationId.Should().Be("op-123");
        op.RouterModelId.Should().Be(request.RouterModelId);
        adminClient.VerifyAll();
    }

    [TestMethod]
    public async Task GetOperationStatusAsync_DelegatesToAdminClient()
    {
        var adminClient = new Mock<ILlamaRuntimeAdminClient>(MockBehavior.Strict);
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>(MockBehavior.Strict);
        var options = new Mock<IOptionsMonitor<LlamaModelManagementOptions>>(MockBehavior.Strict);

        tokenResolver.Setup(x => x.Resolve()).Returns((string?)null);
        options.SetupGet(x => x.CurrentValue).Returns(new LlamaModelManagementOptions { AllowOverwrite = false });
        adminClient
            .Setup(x => x.GetDownloadStatusAsync("op-xyz", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelDownloadOperationDto(
                OperationId: "op-xyz",
                Status: "downloading",
                RouterModelId: "router-a",
                Progress: 0.42,
                ErrorMessage: null,
                LogLine: "downloading"));

        var service = new HuggingFaceModelDownloadService(
            adminClient.Object,
            tokenResolver.Object,
            options.Object,
            NullLogger<HuggingFaceModelDownloadService>.Instance);

        var op = await service.GetOperationStatusAsync("op-xyz", CancellationToken.None);

        op.Should().NotBeNull();
        op!.Status.Should().Be("downloading");
        op.Progress.Should().Be(0.42);
        adminClient.VerifyAll();
    }

    private static StartModelDownloadRequest CreateRequest()
    {
        return new StartModelDownloadRequest(
            Repository: "repo/model",
            QuantIncludePattern: "*.gguf",
            MmprojIncludePattern: string.Empty,
            RouterModelId: "router-a",
            TargetDirectory: "/models/router-a",
            CatalogModelId: "model-a",
            CatalogDisplayName: "Model A",
            CatalogRuntimeProfileId: "profile-a",
            CatalogDescription: "description",
            CatalogIsActive: true,
            CatalogDisplayOrder: 1,
            CatalogLoadParamsJson: "{\"model\":\"router-a\"}",
            CatalogParallelToolCalls: false);
    }
}
