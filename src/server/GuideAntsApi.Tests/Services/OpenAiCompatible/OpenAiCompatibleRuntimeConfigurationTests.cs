using FluentAssertions;
using GuideAntsApi.Services.OpenAiCompatible;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Services.OpenAiCompatible;

[TestClass]
public sealed class OpenAiCompatibleRuntimeConfigurationTests
{
    private static SettingsSecretsOptions Keyring() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };

    // Base64url ciphertexts never contain quotes/backslashes, so plain concatenation is safe.
    private static string JsonWithBaseUrlAndCiphertext(string ciphertext) =>
        "{\"baseUrl\":\"http://localhost:8000/v1\",\"apiKey\":\"" + ciphertext + "\"}";

    [TestMethod]
    public void Parse_ReturnsConfig_WhenValidJson()
    {
        var config = OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"baseUrl":"http://localhost:8000/v1"}""");

        config.Should().NotBeNull();
        config!.BaseUrl.Should().Be("http://localhost:8000/v1");
        config.ApiKey.Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_ReturnsNull_WhenNoConfig()
    {
        OpenAiCompatibleRuntimeConfigurationParser.Parse("m", null).Should().BeNull();
        OpenAiCompatibleRuntimeConfigurationParser.Parse("m", "").Should().BeNull();
        OpenAiCompatibleRuntimeConfigurationParser.Parse("m", "  ").Should().BeNull();
    }

    [TestMethod]
    public void Parse_Throws_WhenBaseUrlMissing()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"apiKey":"x"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*baseUrl*");
    }

    [TestMethod]
    public void Parse_Throws_WhenBaseUrlHasBadScheme()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"baseUrl":"ftp://localhost:8000/v1"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*absolute http(s)*");
    }

    [TestMethod]
    public void Parse_Throws_WhenBaseUrlHasTrailingSlash()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"baseUrl":"http://localhost:8000/v1/"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*trailing*");
    }

    [TestMethod]
    public void Parse_Throws_WhenBaseUrlIsRelative()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"baseUrl":"localhost:8000/v1"}""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*absolute http(s)*");
    }

    [TestMethod]
    public void Parse_Throws_WhenJsonIsNotAnObject()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", "[1,2]");
        act.Should().Throw<InvalidOperationException>().WithMessage("*JSON object*");
    }

    [TestMethod]
    public void Parse_Throws_WhenJsonIsInvalid()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", "{ not json ::");
        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid JSON*");
    }

    [TestMethod]
    public void Parse_KeepsPlaintextApiKey_WhenNotEncrypted()
    {
        var config = OpenAiCompatibleRuntimeConfigurationParser.Parse("m", """{"baseUrl":"http://localhost:8000/v1","apiKey":"plain-key"}""");

        config!.ApiKey.Should().Be("plain-key");
    }

    [TestMethod]
    public void Parse_DecryptsApiKey_WhenEncV2Wrapped()
    {
        var keyring = Keyring();
        var ciphertext = ApplicationSettingsJson.EncryptSecretValue("secret-row-key", keyring)!;
        var json = JsonWithBaseUrlAndCiphertext(ciphertext);

        var config = OpenAiCompatibleRuntimeConfigurationParser.Parse("m", json, keyring);

        config!.ApiKey.Should().Be("secret-row-key");
    }

    [TestMethod]
    public void Parse_Throws_WhenApiKeyEncryptedButNoKeyring()
    {
        var keyring = Keyring();
        var ciphertext = ApplicationSettingsJson.EncryptSecretValue("secret-row-key", keyring)!;
        var json = JsonWithBaseUrlAndCiphertext(ciphertext);

        var act = () => OpenAiCompatibleRuntimeConfigurationParser.Parse("m", json, secretsOptions: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*keyring*");
    }

    [TestMethod]
    public void ParseRequired_Throws_WhenNoConfig()
    {
        var act = () => OpenAiCompatibleRuntimeConfigurationParser.ParseRequired("m", null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*missing RuntimeConfigJson*");
    }

    [TestMethod]
    public void TryParseBaseUrl_ReturnsTrue_WhenValid()
    {
        OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl("""{"baseUrl":"https://api.example.com/v1"}""", out var baseUrl).Should().BeTrue();
        baseUrl.Should().Be("https://api.example.com/v1");
    }

    [TestMethod]
    public void TryParseBaseUrl_ReturnsFalse_WhenMissingOrInvalid()
    {
        OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl(null, out _).Should().BeFalse();
        OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl("{}", out _).Should().BeFalse();
        OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl("""{"baseUrl":"http://x/v1/"}""", out _).Should().BeFalse();
        OpenAiCompatibleRuntimeConfigurationParser.TryParseBaseUrl("not json", out _).Should().BeFalse();
    }
}
