using AntRunner.Chat.Abstractions;

using FluentAssertions;

using GuideAntsApi.BackgroundJobs;

using GuideAntsApi.Services.ConversationLockGate;

using GuideAntsApi.Services.Routing;

using Moq;



namespace GuideAntsApi.Tests.BackgroundJobs;



[TestClass]

public sealed class ConversationLockGateEligibilityTests

{

    [TestMethod]

    public async Task BothUseLocalAiAsync_ReturnsTrue_WhenChatAndEmbeddingsAreLocal()

    {

        var chatResolver = new Mock<IChatModelResolver>();

        chatResolver

            .Setup(r => r.Resolve(null))

            .Returns(new ResolvedChatModel(

                "local-chat",

                ChatModelReferenceKind.DefaultedTo,

                new ResolvedExecutionPolicy(

                    "local-chat",

                    ConversationLockGateEligibility.LocalChatProvider,

                    ParameterAuthority.AssistantDefinition,

                    new Dictionary<string, System.Text.Json.JsonElement>())));



        var modeResolver = new Mock<IServiceModeResolver>();

        modeResolver

            .Setup(r => r.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()))

            .ReturnsAsync(new ServiceMode(

                "local",

                ConversationLockGateEligibility.LocalEmbeddingsProviderSection,

                null,

                null,

                Enabled: true,

                IsDefault: true));



        var eligibility = new ConversationLockGateEligibility(chatResolver.Object, modeResolver.Object);



        (await eligibility.BothUseLocalAiAsync()).Should().BeTrue();

    }



    [TestMethod]

    public async Task BothUseLocalAiAsync_ReturnsFalse_WhenChatIsCloud()

    {

        var chatResolver = new Mock<IChatModelResolver>();

        chatResolver

            .Setup(r => r.Resolve(null))

            .Returns(new ResolvedChatModel(

                "cloud-chat",

                ChatModelReferenceKind.DefaultedTo,

                new ResolvedExecutionPolicy(

                    "cloud-chat",

                    "openrouter-chat",

                    ParameterAuthority.AssistantDefinition,

                    new Dictionary<string, System.Text.Json.JsonElement>())));



        var modeResolver = new Mock<IServiceModeResolver>();

        modeResolver

            .Setup(r => r.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()))

            .ReturnsAsync(new ServiceMode(

                "local",

                ConversationLockGateEligibility.LocalEmbeddingsProviderSection,

                null,

                null,

                Enabled: true,

                IsDefault: true));



        var eligibility = new ConversationLockGateEligibility(chatResolver.Object, modeResolver.Object);



        (await eligibility.BothUseLocalAiAsync()).Should().BeFalse();

    }



    [TestMethod]

    public async Task BothUseLocalAiAsync_ReturnsFalse_WhenEmbeddingsAreCloud()

    {

        var chatResolver = new Mock<IChatModelResolver>();

        chatResolver

            .Setup(r => r.Resolve(null))

            .Returns(new ResolvedChatModel(

                "local-chat",

                ChatModelReferenceKind.DefaultedTo,

                new ResolvedExecutionPolicy(

                    "local-chat",

                    ConversationLockGateEligibility.LocalChatProvider,

                    ParameterAuthority.AssistantDefinition,

                    new Dictionary<string, System.Text.Json.JsonElement>())));



        var modeResolver = new Mock<IServiceModeResolver>();

        modeResolver

            .Setup(r => r.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()))

            .ReturnsAsync(new ServiceMode(

                "azure",

                "AzureOpenAiEmbedding",

                null,

                null,

                Enabled: true,

                IsDefault: true));



        var eligibility = new ConversationLockGateEligibility(chatResolver.Object, modeResolver.Object);



        (await eligibility.BothUseLocalAiAsync()).Should().BeFalse();

    }

}

