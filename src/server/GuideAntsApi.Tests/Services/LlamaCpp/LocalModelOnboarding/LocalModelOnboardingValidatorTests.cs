using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp.LocalModelOnboarding;

[TestClass]
public class LocalModelOnboardingValidatorTests
{
    [TestMethod]
    public async Task ValidateAsync_HuggingFaceTextOnly_Passes()
    {
        var validator = CreateValidator(
            models: Array.Empty<SettingsModelDto>(),
            inventory: [
                new LlamaRuntimeInventoryItemDto("qwen-local", "unloaded", "/models/qwen.gguf", null, true, false, [], 0)
            ],
            huggingFaceToken: "hf_token");

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace, mmprojPattern: string.Empty);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_HuggingFaceWithMmproj_Passes()
    {
        var validator = CreateValidator(
            models: Array.Empty<SettingsModelDto>(),
            inventory: [],
            huggingFaceToken: "hf_token");

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace, mmprojPattern: "*mmproj*");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_ExistingAliasAttach_Passes()
    {
        var validator = CreateValidator(
            models: Array.Empty<SettingsModelDto>(),
            inventory: [
                new LlamaRuntimeInventoryItemDto("alias-a", "unloaded", "/models/a.gguf", null, true, false, [], 0)
            ],
            huggingFaceToken: null);

        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias, routerModelId: "alias-a");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_DuplicateModelId_ThrowsModelIdTaken()
    {
        var validator = CreateValidator(
            models: [
                new SettingsModelDto("model-a", "Model A", "llama-cpp", null, null, null, true, null, DateTime.UtcNow, null)
            ],
            inventory: [],
            huggingFaceToken: "hf_token");

        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace, catalogModelId: "model-a");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("MODEL_ID_TAKEN");
    }

    [TestMethod]
    public async Task ValidateAsync_AdoptedAlias_ThrowsRouterAliasTaken()
    {
        var validator = CreateValidator(
            models: Array.Empty<SettingsModelDto>(),
            inventory: [
                new LlamaRuntimeInventoryItemDto("alias-a", "loaded", "/models/a.gguf", null, true, false, ["existing-model"], 0)
            ],
            huggingFaceToken: null);

        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias, routerModelId: "alias-a");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await validator.ValidateAsync(request, command, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("ROUTER_ALIAS_TAKEN");
    }

    private static LocalModelOnboardingValidator CreateValidator(
        IReadOnlyList<SettingsModelDto> models,
        IReadOnlyList<LlamaRuntimeInventoryItemDto> inventory,
        string? huggingFaceToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var settingsService = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settingsService
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(models);

        var chatTargetValidator = new Mock<IChatTargetValidator>(MockBehavior.Strict);
        chatTargetValidator
            .Setup(x => x.Validate(It.IsAny<ChatTarget>()));

        var inventoryService = new Mock<ILlamaRuntimeInventoryService>(MockBehavior.Strict);
        inventoryService
            .Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(inventory);

        var tokenResolver = new Mock<IHuggingFaceTokenResolver>(MockBehavior.Strict);
        tokenResolver
            .Setup(x => x.Resolve())
            .Returns(huggingFaceToken);

        return new LocalModelOnboardingValidator(
            configuration,
            settingsService.Object,
            chatTargetValidator.Object,
            inventoryService.Object,
            tokenResolver.Object);
    }

    private static AddModelRequest CreateLocalRequest(
        string source,
        string? catalogModelId = null,
        string? routerModelId = null,
        string? mmprojPattern = null)
    {
        var alias = routerModelId ?? "qwen-local";
        return new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto(
                ModelId: catalogModelId ?? "qwen-local",
                DisplayName: "Qwen Local",
                Description: "",
                DisplayOrder: null,
                IsActive: true),
            ProviderConfig: null,
            Install: new AddModelInstallDto(
                Source: source,
                RouterModelId: alias,
                RuntimeProfileId: "qwen3_6",
                HuggingFace: string.Equals(source, LocalModelInstallSources.HuggingFace, StringComparison.OrdinalIgnoreCase)
                    ? new AddModelInstallHuggingFaceDto(
                        Repository: "unsloth/Qwen3.6-9B-GGUF",
                        QuantIncludePattern: "*Q5_K_M*",
                        MmprojIncludePattern: mmprojPattern ?? string.Empty,
                        TargetDirectory: alias)
                    : null,
                ExistingAlias: string.Equals(source, LocalModelInstallSources.ExistingAlias, StringComparison.OrdinalIgnoreCase)
                    ? new AddModelInstallExistingAliasDto(alias)
                    : null));
    }
}
