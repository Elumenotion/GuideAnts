using GuideAntsApi.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Configuration;

/// <summary>
/// Phase H (R-4.4) scope narrowed this validator to cross-cutting concerns
/// only: encrypted secrets, UI root path, and gateway base-URL prefixes.
/// Per-service <c>{Service}:ActiveProviderId</c> validation was retired and
/// moved to <c>IRoutingReadinessService</c> + <c>IChatTargetValidator</c>,
/// which surface structured <c>ROUTING_*</c> errors at dispatch time. Any
/// test attempting to revive <c>ActiveProviderId</c> reads here is a
/// regression.
/// </summary>
[TestClass]
public sealed class ServiceRoutingStartupValidatorTests
{
    [TestMethod]
    public void Evaluate_ReturnsNoErrors_ForValidConfiguration()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var errors = ServiceRoutingStartupValidator.Evaluate(configuration);

        errors.Should().BeEmpty();
    }

    [TestMethod]
    public void Evaluate_IgnoresActiveProviderIdKeys_PerR4p4()
    {
        // Configuration with an intentionally garbage ActiveProviderId must NOT
        // surface an error here. ActiveProviderId has been retired from
        // startup validation; any readiness concern now flows through the
        // routing readiness service at runtime.
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["SpeechTranscription:ActiveProviderId"] = "this-value-is-legacy-noise",
            ["Embeddings:ActiveProviderId"] = "also-legacy",
            ["ImageGeneration:ActiveProviderId"] = "nope",
            ["SpeechSynthesis:ActiveProviderId"] = "still-nope",
            ["DocumentIntelligence:ActiveProviderId"] = "none-of-this-matters-now"
        });

        var errors = ServiceRoutingStartupValidator.Evaluate(configuration);
        errors.Should().NotContain(e => e.Contains("ActiveProviderId", StringComparison.OrdinalIgnoreCase),
            "R-4.4: ActiveProviderId reads are removed from the startup validator.");
    }

    [TestMethod]
    public void Validate_AllowsPrefixedGatewayConfiguration()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110/sandbox",
            ["ServiceRouting:Containers:plantuml:BaseUrl"] = "http://localhost:8111"
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_RejectsLegacyLlamaBaseUrl()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LlamaCpp:BaseUrl"] = "http://localhost:8080"
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*LlamaCpp:BaseUrl must include the '/llama-cpp' prefix*");
    }

    [TestMethod]
    public void Validate_RejectsLegacyGuideantsScriptBaseUrl()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110"
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ServiceRouting:Containers:guideants-ai:BaseUrl must include the '/sandbox' prefix*");
    }

    [TestMethod]
    public void Validate_RejectsMissingLlamaBaseUrl()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LlamaCpp:BaseUrl"] = null
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*LlamaCpp:BaseUrl is required*");
    }

    [TestMethod]
    public void Validate_RejectsMissingSettingsSecretsConfig()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = null
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*SettingsSecrets:ActiveKeyId is required*");
    }

    [TestMethod]
    public void Validate_RejectsEncryptedSecretCiphertext()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AzureOpenAI:ApiKey"] = "enc::not-decrypted"
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);
        act
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*AzureOpenAI:ApiKey is still encrypted*");
    }

    [TestMethod]
    public void Validate_IgnoresRemovedSearchSecret()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Search:ApiKey"] = "enc::not-decrypted"
        });

        Action act = () => ServiceRoutingStartupValidator.Validate(configuration);

        act.Should().NotThrow();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["SettingsSecrets:ActiveKeyId"] = "tests",
            ["SettingsSecrets:Keys:tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=",
            ["Ui:RootPath"] = "./ui",
            ["LlamaCpp:BaseUrl"] = "http://localhost:8110/llama-cpp",
            ["ServiceRouting:Containers:guideants-ai:BaseUrl"] = "http://localhost:8110/sandbox"
        };

        foreach (var pair in values)
        {
            defaults[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .Build();
    }
}
