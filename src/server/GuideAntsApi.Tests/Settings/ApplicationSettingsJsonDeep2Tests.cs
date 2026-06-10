using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Tests.Settings;

[TestClass]
public sealed class ApplicationSettingsJsonDeep2Tests
{
    private const string ValidKey = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY=";

    private static SettingsSectionDefinition SecretDefinition() => new()
    {
        SectionName = "UnitTest",
        Properties =
        [
            new SettingsPropertyDefinition(
                Name: "ApiKey",
                CanonicalKey: "UnitTest:ApiKey",
                IsSecret: true)
        ]
    };

    private static SettingsSecretsOptions ValidOptions() => new()
    {
        ActiveKeyId = "tests",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests"] = ValidKey
        }
    };

    [TestMethod]
    public void ValidateSettingsSecrets_NullOptions_ReportsActiveKeyAndMissingKeys()
    {
        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(null);

        errors.Should().Contain(e => e.Contains("ActiveKeyId", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("must include at least one", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_EmptyKeys_ReportsMissingKeys()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().ContainSingle()
            .Which.Should().Contain("must include at least one");
    }

    [TestMethod]
    public void ValidateSettingsSecrets_BlankKeyId_IsReported()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey,
                [" "] = ValidKey
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().Contain(e => e.Contains("blank key ID", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_EmptyEncodedKey_IsReported()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey,
                ["blank"] = "  "
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().Contain(e => e.Contains("blank") && e.Contains("is empty", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_InvalidBase64_IsReported()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey,
                ["bad"] = "not!!base64"
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().Contain(e => e.Contains("must be valid base64", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_WrongKeyLength_IsReported()
    {
        var shortKey = Convert.ToBase64String(new byte[10]);
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey,
                ["short"] = shortKey
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().Contain(e => e.Contains("16, 24, or 32 bytes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_ActiveKeyNotInRing_IsReported()
    {
        var options = new SettingsSecretsOptions
        {
            ActiveKeyId = "missing",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey
            }
        };

        var errors = ApplicationSettingsJson.ValidateSettingsSecrets(options);

        errors.Should().Contain(e => e.Contains("is the active key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ValidateSettingsSecrets_ValidOptions_ReturnsNoErrors()
    {
        ApplicationSettingsJson.ValidateSettingsSecrets(ValidOptions()).Should().BeEmpty();
    }

    [TestMethod]
    public void IsEncryptedSecretValue_DistinguishesPrefixes()
    {
        ApplicationSettingsJson.IsEncryptedSecretValue("encv2::tests::abc").Should().BeTrue();
        ApplicationSettingsJson.IsEncryptedSecretValue("enc::legacy").Should().BeTrue();
        ApplicationSettingsJson.IsEncryptedSecretValue("plaintext").Should().BeFalse();
        ApplicationSettingsJson.IsEncryptedSecretValue(null).Should().BeFalse();
        ApplicationSettingsJson.IsEncryptedSecretValue("   ").Should().BeFalse();
    }

    [TestMethod]
    public void EncryptSecrets_ThrowsForInvalidOptions()
    {
        var definition = SecretDefinition();
        var invalid = new SettingsSecretsOptions
        {
            ActiveKeyId = string.Empty,
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        var act = () => ApplicationSettingsJson.EncryptSecrets(definition, new JsonObject { ["ApiKey"] = "x" }, invalid);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid SettingsSecrets configuration*");
    }

    [TestMethod]
    public void EncryptSecrets_SkipsMissingAndEmptySecretValues()
    {
        var definition = SecretDefinition();
        var options = ValidOptions();

        var missing = ApplicationSettingsJson.EncryptSecrets(definition, new JsonObject(), options);
        missing.ContainsKey("ApiKey").Should().BeFalse();

        var empty = ApplicationSettingsJson.EncryptSecrets(definition, new JsonObject { ["ApiKey"] = "   " }, options);
        empty["ApiKey"]!.GetValue<string>().Should().Be("   ");
    }

    [TestMethod]
    public void DecryptSecrets_LeavesValue_WhenEncV2EnvelopeMalformed()
    {
        var definition = SecretDefinition();
        var options = ValidOptions();

        var payload = new JsonObject { ["ApiKey"] = "encv2::no-delimiter-here" };

        var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, payload, options);
        decrypted["ApiKey"]!.GetValue<string>().Should().Be("encv2::no-delimiter-here");
    }

    [TestMethod]
    public void DecryptSecrets_LeavesValue_WhenKeyIdNotInRing()
    {
        var definition = SecretDefinition();
        var options = ValidOptions();

        var payload = new JsonObject { ["ApiKey"] = "encv2::unknown-key::ZHVtbXk" };

        var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, payload, options);
        decrypted["ApiKey"]!.GetValue<string>().Should().Be("encv2::unknown-key::ZHVtbXk");
    }

    [TestMethod]
    public void DecryptSecrets_LeavesLegacyValue_WhenNoProtector()
    {
        var definition = SecretDefinition();
        var options = ValidOptions();

        var payload = new JsonObject { ["ApiKey"] = "enc::legacy-cipher" };

        var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, payload, options);
        decrypted["ApiKey"]!.GetValue<string>().Should().Be("enc::legacy-cipher");
    }

    [TestMethod]
    public void DecryptSecrets_SkipsPlainAndEmptyValues()
    {
        var definition = SecretDefinition();
        var options = ValidOptions();

        var plain = ApplicationSettingsJson.DecryptSecrets(definition, new JsonObject { ["ApiKey"] = "plain" }, options);
        plain["ApiKey"]!.GetValue<string>().Should().Be("plain");

        var empty = ApplicationSettingsJson.DecryptSecrets(definition, new JsonObject { ["ApiKey"] = "" }, options);
        empty["ApiKey"]!.GetValue<string>().Should().Be("");
    }

    [TestMethod]
    public void MaskSecrets_MasksPresentValues_AndReportsMetadata()
    {
        var definition = SecretDefinition();

        var (maskedWithValue, metaWithValue) = ApplicationSettingsJson.MaskSecrets(
            definition,
            new JsonObject { ["ApiKey"] = "secret" });
        maskedWithValue["ApiKey"]!.GetValue<string>().Should().Be(ApplicationSettingsJson.SecretMask);
        metaWithValue["ApiKey"].Should().BeTrue();

        var (maskedNoValue, metaNoValue) = ApplicationSettingsJson.MaskSecrets(
            definition,
            new JsonObject { ["ApiKey"] = "" });
        maskedNoValue["ApiKey"]!.GetValue<string>().Should().BeEmpty();
        metaNoValue["ApiKey"].Should().BeFalse();
    }

    [TestMethod]
    public void MergeForUpdate_KeepsExistingSecret_WhenIncomingIsMask()
    {
        var definition = SecretDefinition();

        var merged = ApplicationSettingsJson.MergeForUpdate(
            definition,
            new JsonObject { ["ApiKey"] = "existing-secret" },
            new JsonObject { ["ApiKey"] = ApplicationSettingsJson.SecretMask });

        merged["ApiKey"]!.GetValue<string>().Should().Be("existing-secret");
    }

    [TestMethod]
    public void MergeForUpdate_WithNullExisting_StartsFromEmpty()
    {
        var definition = SecretDefinition();

        var merged = ApplicationSettingsJson.MergeForUpdate(
            definition,
            existingDecryptedPayload: null,
            new JsonObject { ["ApiKey"] = "new-secret" });

        merged["ApiKey"]!.GetValue<string>().Should().Be("new-secret");
    }

    [TestMethod]
    public void MergeForUpdate_IgnoresPropertiesMissingFromIncoming()
    {
        var definition = SecretDefinition();

        var merged = ApplicationSettingsJson.MergeForUpdate(
            definition,
            new JsonObject { ["ApiKey"] = "existing-secret" },
            new JsonObject());

        merged["ApiKey"]!.GetValue<string>().Should().Be("existing-secret");
    }

    [TestMethod]
    public void CanonicalizeToDefinition_NoProperties_ClonesPayload()
    {
        var definition = new SettingsSectionDefinition
        {
            SectionName = "Empty",
            Properties = []
        };

        var payload = new JsonObject { ["Anything"] = "value" };
        var canonical = ApplicationSettingsJson.CanonicalizeToDefinition(definition, payload);

        canonical["Anything"]!.GetValue<string>().Should().Be("value");
    }

    [TestMethod]
    public void CanonicalizeToDefinition_DropsPropertiesNotInPayload()
    {
        var definition = SecretDefinition();
        var payload = new JsonObject { ["Unrelated"] = "x" };

        var canonical = ApplicationSettingsJson.CanonicalizeToDefinition(definition, payload);

        canonical.ContainsKey("ApiKey").Should().BeFalse();
        canonical.ContainsKey("Unrelated").Should().BeFalse();
    }

    [TestMethod]
    public void MergeMissingProperties_AddsOnlyAbsentKeys()
    {
        var merged = ApplicationSettingsJson.MergeMissingProperties(
            new JsonObject { ["A"] = "existing" },
            new JsonObject { ["A"] = "bootstrap", ["B"] = "added" });

        merged["A"]!.GetValue<string>().Should().Be("existing");
        merged["B"]!.GetValue<string>().Should().Be("added");
    }

    [TestMethod]
    public void MergeMissingProperties_WithDefinition_CanonicalizesExistingFirst()
    {
        var definition = SecretDefinition();

        var merged = ApplicationSettingsJson.MergeMissingProperties(
            definition,
            new JsonObject { ["ApiKey"] = "kept", ["Retired"] = "dropped" },
            new JsonObject { ["ApiKey"] = "ignored", ["Extra"] = "added" });

        merged["ApiKey"]!.GetValue<string>().Should().Be("kept");
        merged.ContainsKey("Retired").Should().BeFalse();
        merged["Extra"]!.GetValue<string>().Should().Be("added");
    }

    [TestMethod]
    public void Serialize_ProducesCompactJson()
    {
        var json = ApplicationSettingsJson.Serialize(new JsonObject { ["A"] = 1 });
        json.Should().Be("{\"A\":1}");
    }

    [TestMethod]
    public void DeserializeObject_HandlesNullEmptyAndInvalidJson()
    {
        ApplicationSettingsJson.DeserializeObject(null).Count.Should().Be(0);
        ApplicationSettingsJson.DeserializeObject("   ").Count.Should().Be(0);
        ApplicationSettingsJson.DeserializeObject("{not json").Count.Should().Be(0);
        ApplicationSettingsJson.DeserializeObject("[1,2,3]").Count.Should().Be(0);

        var parsed = ApplicationSettingsJson.DeserializeObject("{\"A\":1}");
        parsed["A"]!.GetValue<int>().Should().Be(1);
    }

    [TestMethod]
    public void NodeToString_HandlesAllValueKinds()
    {
        ApplicationSettingsJson.NodeToString(null).Should().BeEmpty();
        ApplicationSettingsJson.NodeToString(JsonValue.Create("text")).Should().Be("text");
        ApplicationSettingsJson.NodeToString(JsonValue.Create(42)).Should().Be("42");
        ApplicationSettingsJson.NodeToString(JsonValue.Create(true)).Should().Be("true");
        ApplicationSettingsJson.NodeToString(JsonValue.Create(false)).Should().Be("false");
        ApplicationSettingsJson.NodeToString(new JsonObject { ["k"] = "v" }).Should().Contain("\"k\"");
        ApplicationSettingsJson.NodeToString(new JsonArray(1, 2)).Should().Contain("1");
    }

    [TestMethod]
    public void CloneObject_ProducesIndependentCopy()
    {
        var original = new JsonObject { ["A"] = "value" };
        var clone = ApplicationSettingsJson.CloneObject(original);

        clone["A"] = "changed";
        original["A"]!.GetValue<string>().Should().Be("value");
    }

    [TestMethod]
    public void DecryptSecrets_RoundTripsThroughMultiKeyRing()
    {
        var definition = SecretDefinition();

        // Encrypt with a clean key ring, then decrypt with a ring that also
        // contains blank/invalid entries to exercise BuildReadKeyRing's skip
        // branches without tripping the encrypt-side validation.
        var encrypted = ApplicationSettingsJson.EncryptSecrets(
            definition,
            new JsonObject { ["ApiKey"] = "ring-secret" },
            ValidOptions());

        var dirtyReadOptions = new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = ValidKey,
                [" "] = ValidKey,
                ["bad"] = "not!!base64",
                ["short"] = Convert.ToBase64String(new byte[10])
            }
        };

        var decrypted = ApplicationSettingsJson.DecryptSecrets(definition, encrypted, dirtyReadOptions);
        decrypted["ApiKey"]!.GetValue<string>().Should().Be("ring-secret");
    }
}
