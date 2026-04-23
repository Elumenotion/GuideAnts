using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsJsonTests
{
    [TestMethod]
    public void EncryptSecrets_AndDecryptSecrets_RoundTripsWithEncV2()
    {
        var definition = CreateSectionDefinition();
        var options = CreateSecretsOptions();
        var payload = new JsonObject
        {
            ["ApiKey"] = "top-secret"
        };

        var encrypted = ApplicationSettingsJson.EncryptSecrets(definition, payload, options);
        var cipher = encrypted["ApiKey"]!.GetValue<string>();

        cipher.Should().StartWith("encv2::tests::");
        cipher.Should().NotBe("top-secret");

        var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, encrypted, options);
        decrypted["ApiKey"]!.GetValue<string>().Should().Be("top-secret");
    }

    [TestMethod]
    public void DecryptSecrets_UsesLegacyFallback_ForEncPrefix()
    {
        var definition = CreateSectionDefinition();
        var options = CreateSecretsOptions();

        var contentRoot = Path.Combine(Path.GetTempPath(), "settings-json-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var protector = ApplicationSettingsJson.CreateLegacyProtector(contentRoot);
            var payload = new JsonObject
            {
                ["ApiKey"] = $"enc::{protector.Protect("legacy-secret")}"
            };

            var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, payload, options, protector);
            decrypted["ApiKey"]!.GetValue<string>().Should().Be("legacy-secret");
        }
        finally
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Delete(contentRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EncryptSecrets_DoesNotReEncrypt_ExistingEncV2Value()
    {
        var definition = CreateSectionDefinition();
        var options = CreateSecretsOptions();

        var encryptedOnce = ApplicationSettingsJson.EncryptSecrets(
            definition,
            new JsonObject { ["ApiKey"] = "secret" },
            options);

        var encryptedTwice = ApplicationSettingsJson.EncryptSecrets(definition, encryptedOnce, options);

        encryptedTwice["ApiKey"]!.GetValue<string>()
            .Should()
            .Be(encryptedOnce["ApiKey"]!.GetValue<string>());
    }

    [TestMethod]
    public void ValidateSettingsSecrets_RejectsMissingActiveKeyId()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = string.Empty,
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);
        errors.Should().Contain(error => error.Contains("ActiveKeyId", StringComparison.Ordinal));
    }

    private static SettingsSectionDefinition CreateSectionDefinition()
    {
        return new SettingsSectionDefinition
        {
            SectionName = "UnitTest",
            DisplayName = "Unit Test",
            DisplayOrder = 1,
            Properties =
            [
                new SettingsPropertyDefinition(
                    Name: "ApiKey",
                    CanonicalKey: "UnitTest:ApiKey",
                    IsSecret: true)
            ]
        };
    }

    private static SettingsSecretsOptions CreateSecretsOptions()
    {
        return new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        };
    }
}
