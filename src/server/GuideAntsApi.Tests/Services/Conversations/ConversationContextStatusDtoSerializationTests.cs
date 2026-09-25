using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Conversations;

/// <summary>
/// The server writes these enums as strings (global JsonStringEnumConverter, Program.cs). Any consumer
/// reading the conversation API with default web options -- HttpContent.ReadFromJsonAsync, the integration
/// suite, an external client -- must be able to read them back. W2 broke that; see the W9 plan.
/// </summary>
[TestClass]
public sealed class ConversationContextStatusDtoSerializationTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Deserialize_StringEnumsWithDefaultWebOptions_ReadsBothEnums()
    {
        const string json =
            """{"contextWindowTokens":8192,"estimatedPromptTokens":1200,"boundaryTurnIndex":3,"estimateSource":"Characters","modelDeploymentId":"m","contextWindowSource":"LiveRuntime"}""";

        var dto = JsonSerializer.Deserialize<ConversationContextStatusDto>(json, WebDefaults);

        dto.Should().NotBeNull();
        dto!.EstimateSource.Should().Be(ContextEstimateSource.Characters);
        dto.ContextWindowSource.Should().Be(ContextWindowSource.LiveRuntime);
    }

    [TestMethod]
    public void Serialize_WithDefaultWebOptions_WritesEnumsAsTheSameStringsTheServerSends()
    {
        var dto = new ConversationContextStatusDto(
            8192, 1200, 3, ContextEstimateSource.ProviderUsage, "m", ContextWindowSource.Catalog);

        var json = JsonSerializer.Serialize(dto, WebDefaults);

        json.Should().Contain("\"estimateSource\":\"ProviderUsage\"");
        json.Should().Contain("\"contextWindowSource\":\"Catalog\"");
    }
}
