using FluentAssertions;
using System.Text.Json.Nodes;
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
public class LocalModelOnboardingValidatorDeep2Tests
{
    [TestMethod]
    public async Task ValidateAsync_NonLlamaCppProvider_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace) with { Provider = "openai-chat" };
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("INSTALL_STEP_FAILED");
    }

    [TestMethod]
    public async Task ValidateAsync_MissingModelId_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { CatalogModelId = "  " };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("INSTALL_STEP_FAILED");
        ex.Which.Message.Should().Contain("Model ID");
    }

    [TestMethod]
    public async Task ValidateAsync_MissingDisplayName_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { CatalogDisplayName = "" };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Message.Should().Contain("Display name");
    }

    [TestMethod]
    public async Task ValidateAsync_MissingRuntimeProfile_ThrowsRuntimeProfileNotFound()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RuntimeProfileId = "" };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("RUNTIME_PROFILE_NOT_FOUND");
    }

    [TestMethod]
    public async Task ValidateAsync_MissingRouterAlias_ThrowsRouterAliasTaken()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RouterModelId = "" };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("ROUTER_ALIAS_TAKEN");
        ex.Which.Message.Should().Contain("Router alias is required");
    }

    [TestMethod]
    public async Task ValidateAsync_HuggingFaceMissingRepository_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { Repository = "" };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("INSTALL_STEP_FAILED");
        ex.Which.Message.Should().Contain("Repository");
    }

    [TestMethod]
    public async Task ValidateAsync_ContextSizeTooSmall_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RouterContextSize = 512 };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Message.Should().Contain("Context size");
    }

    [TestMethod]
    public async Task ValidateAsync_ContextSizeTooLarge_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RouterContextSize = 2_000_000 };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Message.Should().Contain("Context size");
    }

    [TestMethod]
    public async Task ValidateAsync_CacheRamNegative_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RouterCacheRamMib = -1 };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Message.Should().Contain("cache RAM");
    }

    [TestMethod]
    public async Task ValidateAsync_CacheRamTooLarge_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator();
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with { RouterCacheRamMib = 999_999 };

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Message.Should().Contain("cache RAM");
    }

    [TestMethod]
    public async Task ValidateAsync_ValidRouterKnobs_PassThrough()
    {
        var validator = CreateValidator(
            inventory: [new LlamaRuntimeInventoryItemDto("qwen-local", "unloaded", "/models/qwen.gguf", null, true, false, [], 0)],
            huggingFaceToken: "hf_token");
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request) with
        {
            RouterContextSize = 4096,
            RouterCacheRamMib = 0
        };

        await Invoking(validator, request, command).Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_LlamaBaseUrlMissing_ThrowsProviderCredentialsMissing()
    {
        var validator = CreateValidator(llamaBaseUrl: null, huggingFaceToken: "hf_token");
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("PROVIDER_CREDENTIALS_MISSING");
    }

    [TestMethod]
    public async Task ValidateAsync_RoutingProviderNotReady_MapsToProviderCredentialsMissing()
    {
        var validator = CreateValidator(
            huggingFaceToken: "hf_token",
            chatTargetException: RoutingException.ProviderNotReady("LlamaCpp", ["missing base url"]));
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("PROVIDER_CREDENTIALS_MISSING");
        ex.Which.InnerException.Should().BeOfType<RoutingException>();
    }

    [TestMethod]
    public async Task ValidateAsync_RoutingRuntimeNotReady_MapsToRuntimeProfileNotFound()
    {
        var validator = CreateValidator(
            huggingFaceToken: "hf_token",
            chatTargetException: RoutingException.RuntimeNotReady("alias not loaded"));
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("RUNTIME_PROFILE_NOT_FOUND");
    }

    [TestMethod]
    public async Task ValidateAsync_RoutingModelNotReady_MapsToInstallStepFailed()
    {
        var validator = CreateValidator(
            huggingFaceToken: "hf_token",
            chatTargetException: RoutingException.ModelNotReady("qwen-local", "not active"));
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("INSTALL_STEP_FAILED");
    }

    [TestMethod]
    public async Task ValidateAsync_HuggingFaceTokenMissing_ThrowsHuggingFaceTokenMissing()
    {
        var validator = CreateValidator(huggingFaceToken: null);
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("HUGGINGFACE_TOKEN_MISSING");
    }

    [TestMethod]
    public async Task ValidateAsync_HuggingFaceAliasAlreadyReferenced_ThrowsRouterAliasTaken()
    {
        var validator = CreateValidator(
            inventory: [new LlamaRuntimeInventoryItemDto("qwen-local", "loaded", "/models/qwen.gguf", null, true, false, ["other-model"], 0)],
            huggingFaceToken: "hf_token");
        var request = CreateLocalRequest(LocalModelInstallSources.HuggingFace, routerModelId: "qwen-local");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("ROUTER_ALIAS_TAKEN");
    }

    [TestMethod]
    public async Task ValidateAsync_ExistingAliasNotFound_ThrowsRouterAliasTaken()
    {
        var validator = CreateValidator(inventory: [], huggingFaceToken: null);
        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias, routerModelId: "missing-alias");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("ROUTER_ALIAS_TAKEN");
        ex.Which.Message.Should().Contain("does not exist");
    }

    [TestMethod]
    public async Task ValidateAsync_ExistingAliasMissingModelFile_ThrowsInstallStepFailed()
    {
        var validator = CreateValidator(
            inventory: [new LlamaRuntimeInventoryItemDto("alias-a", "unloaded", null, null, false, false, [], 0)],
            huggingFaceToken: null);
        var request = CreateLocalRequest(LocalModelInstallSources.ExistingAlias, routerModelId: "alias-a");
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var ex = await Invoking(validator, request, command).Should().ThrowAsync<AddModelException>();
        ex.Which.Code.Should().Be("INSTALL_STEP_FAILED");
        ex.Which.Message.Should().Contain("missing a model artifact");
    }

    private static Func<Task> Invoking(
        LocalModelOnboardingValidator validator,
        AddModelRequest request,
        LocalModelOnboardingCommand command) =>
        async () => await validator.ValidateAsync(request, command, CancellationToken.None);

    private static LocalModelOnboardingValidator CreateValidator(
        IReadOnlyList<SettingsModelDto>? models = null,
        IReadOnlyList<LlamaRuntimeInventoryItemDto>? inventory = null,
        string? huggingFaceToken = "hf_token",
        string? llamaBaseUrl = "http://localhost:8080/llama-cpp",
        RoutingException? chatTargetException = null)
    {
        var configValues = new Dictionary<string, string?>();
        if (llamaBaseUrl is not null)
        {
            configValues["LlamaCpp:BaseUrl"] = llamaBaseUrl;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService
            .Setup(x => x.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(models ?? Array.Empty<SettingsModelDto>());

        var chatTargetValidator = new Mock<IChatTargetValidator>();
        if (chatTargetException is not null)
        {
            chatTargetValidator
                .Setup(x => x.Validate(It.IsAny<ChatTarget>()))
                .Throws(chatTargetException);
        }
        else
        {
            chatTargetValidator.Setup(x => x.Validate(It.IsAny<ChatTarget>()));
        }

        var inventoryService = new Mock<ILlamaRuntimeInventoryService>();
        inventoryService
            .Setup(x => x.GetInventoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(inventory ?? Array.Empty<LlamaRuntimeInventoryItemDto>());

        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns(huggingFaceToken);

        var curatedResolver = new Mock<ICuratedInstallResolver>();

        return new LocalModelOnboardingValidator(
            configuration,
            settingsService.Object,
            chatTargetValidator.Object,
            new Mock<IRuntimeProfileResolver>(MockBehavior.Loose).Object,
            inventoryService.Object,
            tokenResolver.Object,
            curatedResolver.Object,
            new Mock<ICustomInstallResolver>(MockBehavior.Strict).Object);
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
            ProviderConfig: CreateRowOwnedChatBehavior(),
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

    private static JsonObject CreateRowOwnedChatBehavior() =>
        JsonNode.Parse("""{"samplingParametersJson":"{}","thinkingControlJson":"{}","requestFieldsWhenToolsPresentJson":"{}","combineSystemAndDeveloperMessages":true}""")!.AsObject();
}
