using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Services.OpenAiCompatible;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Services.OpenAiCompatible;

[TestClass]
public sealed class OpenAiCompatibleRuntimeConfigSecretsTests
{
    private static SettingsSecretsOptions Keyring() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
        }
    };

    [TestMethod]
    public void NormalizeAndEncrypt_EncryptsApiKeyOnSave()
    {
        var keyring = Keyring();
        var stored = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m",
            """{"baseUrl":"http://localhost:8000/v1","apiKey":"my-key"}""",
            existingJson: null,
            keyring);

        var root = JsonNode.Parse(stored)!.AsObject();
        root["baseUrl"]!.GetValue<string>().Should().Be("http://localhost:8000/v1");
        var apiKey = root["apiKey"]!.GetValue<string>();
        apiKey.Should().StartWith(ApplicationSettingsJson.SecretCipherPrefix);
        apiKey.Should().NotContain("my-key");
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey(stored).Should().BeTrue();
    }

    [TestMethod]
    public void NormalizeAndEncrypt_PreservesExistingCiphertext_WhenIncomingKeyEmpty()
    {
        var keyring = Keyring();
        var first = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"http://localhost:8000/v1","apiKey":"my-key"}""", null, keyring);

        var second = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"http://localhost:8000/v1"}""", first, keyring);

        var firstKey = JsonNode.Parse(first)!.AsObject()!["apiKey"]!.GetValue<string>();
        var secondKey = JsonNode.Parse(second)!.AsObject()!["apiKey"]!.GetValue<string>();
        secondKey.Should().Be(firstKey, "byte-stable save when the key is unchanged");
    }

    [TestMethod]
    public void NormalizeAndEncrypt_RemovesField_WhenRemoveSentinel()
    {
        var keyring = Keyring();
        var first = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"http://localhost:8000/v1","apiKey":"my-key"}""", null, keyring);

        var removed = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"http://localhost:8000/v1","apiKey":"__REMOVE__"}""", first, keyring);

        JsonNode.Parse(removed)!.AsObject().ContainsKey("apiKey").Should().BeFalse();
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey(removed).Should().BeFalse();
    }

    [TestMethod]
    public void NormalizeAndEncrypt_RoundTripsThroughParser()
    {
        var keyring = Keyring();
        var stored = OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"http://localhost:8000/v1","apiKey":"roundtrip-key"}""", null, keyring);

        var config = OpenAiCompatibleRuntimeConfigurationParser.ParseRequired("m", stored, keyring);
        config.ApiKey.Should().Be("roundtrip-key");
        config.BaseUrl.Should().Be("http://localhost:8000/v1");
    }

    [TestMethod]
    public void NormalizeAndEncrypt_Throws_WhenBaseUrlInvalid()
    {
        var keyring = Keyring();
        var act = () => OpenAiCompatibleRuntimeConfigSecrets.NormalizeAndEncrypt(
            "m", """{"baseUrl":"not-a-url"}""", null, keyring);
        act.Should().Throw<InvalidOperationException>().WithMessage("*baseUrl*");
    }

    [TestMethod]
    public void HasEncryptedKey_ReturnsFalse_WhenKeyAbsentOrPlaintext()
    {
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey(null).Should().BeFalse();
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey("{}").Should().BeFalse();
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey("""{"baseUrl":"http://x/v1"}""").Should().BeFalse();
        OpenAiCompatibleRuntimeConfigSecrets.HasEncryptedKey("""{"baseUrl":"http://x/v1","apiKey":"plain"}""").Should().BeFalse();
    }
}
