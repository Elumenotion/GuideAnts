using FluentAssertions;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.IntegrationTests.Settings;

/// <summary>
/// Phase G.1: asserts the RFC 7807 problem+json body emitted by
/// <see cref="RoutingProblemDetailsFactory.Create"/> satisfies R-2.2 (code +
/// action plus context) and R-2.4 (status mapping: 400 for caller input, 409
/// for state, 503 for unavailability — never 500).
/// <para>
/// These run against the factory directly rather than the HTTP surface so a
/// wiring regression cannot silently mask a contract violation. The HTTP
/// paths are covered by <see cref="RoutingEndpointsTests"/>.
/// </para>
/// </summary>
[TestClass]
public sealed class RoutingProblemDetailsShapeTests
{
    [TestMethod]
    public void ModeNotFound_Maps_To_400_WithStableShape()
    {
        var ex = RoutingException.ModeNotFound("Embeddings", "missing");
        var problem = RoutingProblemDetailsFactory.Create(ex);

        problem.Status.Should().Be(StatusCodes.Status400BadRequest, "R-2.4: ModeNotFound is caller input");
        problem.Type.Should().StartWith(RoutingProblemDetailsFactory.ProblemTypeBase);
        problem.Type.Should().Be($"{RoutingProblemDetailsFactory.ProblemTypeBase}routing-mode-not-found");

        problem.Extensions.Should().ContainKey("code");
        problem.Extensions["code"].Should().Be(RoutingErrorCodes.ModeNotFound);
        problem.Extensions.Should().ContainKey("action");
        ((string)problem.Extensions["action"]!).Should().NotBeNullOrWhiteSpace("R-2.2: action is required");
        problem.Extensions["service"].Should().Be("Embeddings");
        problem.Extensions["modeId"].Should().Be("missing");
    }

    [TestMethod]
    public void ProviderNotReady_Maps_To_409_WithBlockersExtension()
    {
        var ex = RoutingException.ProviderNotReady(
            providerSection: "AzureOpenAI",
            blockers: new[] { "AzureOpenAI:ApiKey is not configured." },
            serviceId: "Chat",
            modeId: null);

        var problem = RoutingProblemDetailsFactory.Create(ex);

        problem.Status.Should().Be(StatusCodes.Status409Conflict, "R-2.4: ProviderNotReady is a state conflict");
        problem.Type.Should().Be($"{RoutingProblemDetailsFactory.ProblemTypeBase}routing-provider-not-ready");

        problem.Extensions["code"].Should().Be(RoutingErrorCodes.ProviderNotReady);
        ((string)problem.Extensions["action"]!).Should().NotBeNullOrWhiteSpace();
        problem.Extensions["provider"].Should().Be("AzureOpenAI");
        problem.Extensions["service"].Should().Be("Chat");
        problem.Extensions.Should().ContainKey("blockers");
        ((IEnumerable<string>)problem.Extensions["blockers"]!).Should().HaveCount(1);
    }

    [TestMethod]
    public void ModelNotReady_Maps_To_409_WithModelIdExtension()
    {
        var ex = RoutingException.ModelNotReady(
            modelId: "qwen3.6-27b",
            reason: "Model not found in the catalog.",
            serviceId: "Chat");

        var problem = RoutingProblemDetailsFactory.Create(ex);

        problem.Status.Should().Be(StatusCodes.Status409Conflict, "R-2.4: ModelNotReady is a state conflict");
        problem.Type.Should().Be($"{RoutingProblemDetailsFactory.ProblemTypeBase}routing-model-not-ready");

        problem.Extensions["code"].Should().Be(RoutingErrorCodes.ModelNotReady);
        ((string)problem.Extensions["action"]!).Should().NotBeNullOrWhiteSpace();
        problem.Extensions["modelId"].Should().Be("qwen3.6-27b");
        problem.Extensions["service"].Should().Be("Chat");
    }

    [TestMethod]
    public void RuntimeNotReady_Maps_To_503_WithModelIdExtension()
    {
        var ex = RoutingException.RuntimeNotReady(
            detail: "Router alias 'qwen-env' is not loaded.",
            modelId: "qwen3.6-27b");

        var problem = RoutingProblemDetailsFactory.Create(ex);

        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable, "R-2.4: RuntimeNotReady is unavailable");
        problem.Type.Should().Be($"{RoutingProblemDetailsFactory.ProblemTypeBase}routing-runtime-not-ready");

        problem.Extensions["code"].Should().Be(RoutingErrorCodes.RuntimeNotReady);
        ((string)problem.Extensions["action"]!).Should().NotBeNullOrWhiteSpace();
        problem.Extensions["modelId"].Should().Be("qwen3.6-27b");
        problem.Extensions.Should().ContainKey("blockers");
    }

    [TestMethod]
    public void NoCode_Is_Never_500()
    {
        // Defence in depth: even an unrecognized code must never map to 500.
        var ex = new RoutingException(
            code: "ROUTING_BESPOKE",
            message: "custom",
            action: "remediate",
            serviceId: "Chat");

        var problem = RoutingProblemDetailsFactory.Create(ex);

        problem.Status.Should().NotBe(StatusCodes.Status500InternalServerError, "R-2.4: 500 is explicitly forbidden");
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable,
            "unknown codes fall through to 503 so routing failures are surfaced as availability issues, not server errors");
    }

    [TestMethod]
    public void AllFactoryExceptions_CarryNonEmptyAction()
    {
        // R-2.2 guardrail: every RoutingException factory must populate the
        // Action field. If anybody adds a new factory without an action, this
        // test fails loudly.
        var cases = new[]
        {
            RoutingException.ModeNotFound("Embeddings", "missing"),
            RoutingException.ProviderNotReady("AzureOpenAI", new[] { "AzureOpenAI:ApiKey missing" }),
            RoutingException.ModelNotReady("m", "not found"),
            RoutingException.RuntimeNotReady("not loaded")
        };

        foreach (var ex in cases)
        {
            ex.Action.Should().NotBeNullOrWhiteSpace($"{ex.Code} factory must populate Action per R-2.2");
        }
    }
}
