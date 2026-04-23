using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace GuideAntsApi.Settings;

public static class ApplicationSettingsJson
{
    public const string SecretMask = "********";
    public const string SecretCipherPrefix = "encv2::";
    public const string LegacySecretCipherPrefix = "enc::";

    private const string EnvelopeSeparator = "::";
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    public static IDataProtector CreateLegacyProtector(string contentRootPath)
    {
        var keysDirectory = Path.Combine(contentRootPath, ".data-protection", "settings-keys");
        Directory.CreateDirectory(keysDirectory);
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keysDirectory),
            options => options.SetApplicationName("GuideAntsApi.Settings"));
        return provider.CreateProtector("ApplicationSettings.SecretProtector.v1");
    }

    public static IReadOnlyList<string> ValidateSettingsSecrets(SettingsSecretsOptions? options)
    {
        var errors = new List<string>();
        var activeKeyId = options?.ActiveKeyId?.Trim();
        if (string.IsNullOrWhiteSpace(activeKeyId))
        {
            errors.Add("SettingsSecrets:ActiveKeyId is required.");
        }

        var keyEntries = options?.Keys;
        if (keyEntries == null || keyEntries.Count == 0)
        {
            errors.Add("SettingsSecrets:Keys must include at least one base64-encoded AES key.");
            return errors;
        }

        foreach (var (keyId, encodedKey) in keyEntries)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                errors.Add("SettingsSecrets:Keys contains a blank key ID.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(encodedKey))
            {
                errors.Add($"SettingsSecrets:Keys:{keyId} is empty.");
                continue;
            }

            if (!TryDecodeBase64Key(encodedKey, out var keyBytes))
            {
                errors.Add($"SettingsSecrets:Keys:{keyId} must be valid base64.");
                continue;
            }

            if (!IsValidAesKeyLength(keyBytes.Length))
            {
                errors.Add($"SettingsSecrets:Keys:{keyId} must decode to 16, 24, or 32 bytes.");
            }
        }

        if (!string.IsNullOrWhiteSpace(activeKeyId) && !keyEntries.ContainsKey(activeKeyId))
        {
            errors.Add($"SettingsSecrets:Keys:{activeKeyId} is required because it is the active key.");
        }

        return errors;
    }

    public static bool IsEncryptedSecretValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.StartsWith(SecretCipherPrefix, StringComparison.Ordinal)
            || value.StartsWith(LegacySecretCipherPrefix, StringComparison.Ordinal);
    }

    public static JsonObject EncryptSecrets(SettingsSectionDefinition definition, JsonObject payload, SettingsSecretsOptions options)
    {
        var (activeKeyId, activeKeyBytes) = GetActiveEncryptionKeyOrThrow(options);

        var copy = CloneObject(payload);
        foreach (var secret in definition.Properties.Where(p => p.IsSecret))
        {
            if (!copy.TryGetPropertyValue(secret.Name, out var node) || node is null)
            {
                continue;
            }

            var raw = NodeToString(node);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (raw.StartsWith(SecretCipherPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var encrypted = EncryptEncV2(raw, activeKeyId, activeKeyBytes);
            copy[secret.Name] = encrypted;
        }

        return copy;
    }

    public static JsonObject DecryptSecrets(
        SettingsSectionDefinition definition,
        JsonObject payload,
        SettingsSecretsOptions options,
        IDataProtector? legacyProtector = null)
    {
        var copy = CloneObject(payload);
        var keyRing = BuildReadKeyRing(options);

        foreach (var secret in definition.Properties.Where(p => p.IsSecret))
        {
            if (!copy.TryGetPropertyValue(secret.Name, out var node) || node is null)
            {
                continue;
            }

            var raw = NodeToString(node);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (raw.StartsWith(SecretCipherPrefix, StringComparison.Ordinal))
            {
                if (TryDecryptEncV2(raw, keyRing, out var plaintext))
                {
                    copy[secret.Name] = plaintext;
                }

                continue;
            }

            if (!raw.StartsWith(LegacySecretCipherPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (legacyProtector == null)
            {
                continue;
            }

            var cipher = raw[LegacySecretCipherPrefix.Length..];
            try
            {
                copy[secret.Name] = legacyProtector.Unprotect(cipher);
            }
            catch
            {
                // Preserve original value when decryption fails so callers can still inspect row integrity.
            }
        }

        return copy;
    }

    public static (JsonObject MaskedPayload, Dictionary<string, bool> SecretHasValues) MaskSecrets(
        SettingsSectionDefinition definition,
        JsonObject decryptedPayload)
    {
        var masked = CloneObject(decryptedPayload);
        var metadata = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var secret in definition.Properties.Where(p => p.IsSecret))
        {
            var hasValue = masked.TryGetPropertyValue(secret.Name, out var node)
                           && !string.IsNullOrWhiteSpace(NodeToString(node));

            metadata[secret.Name] = hasValue;
            masked[secret.Name] = hasValue ? SecretMask : string.Empty;
        }

        return (masked, metadata);
    }

    public static JsonObject MergeForUpdate(
        SettingsSectionDefinition definition,
        JsonObject? existingDecryptedPayload,
        JsonObject incomingPayload)
    {
        var merged = existingDecryptedPayload != null
            ? CloneObject(existingDecryptedPayload)
            : new JsonObject();

        foreach (var property in definition.Properties)
        {
            if (!incomingPayload.TryGetPropertyValue(property.Name, out var incomingNode))
            {
                continue;
            }

            if (property.IsSecret)
            {
                var incomingSecret = NodeToString(incomingNode);
                if (incomingSecret == SecretMask)
                {
                    continue;
                }
            }

            merged[property.Name] = incomingNode?.DeepClone();
        }

        return merged;
    }

    public static JsonObject MergeMissingProperties(JsonObject existingPayload, JsonObject bootstrapPayload)
    {
        var merged = CloneObject(existingPayload);
        foreach (var kvp in bootstrapPayload)
        {
            if (!merged.ContainsKey(kvp.Key))
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        return merged;
    }

    public static string Serialize(JsonObject payload)
    {
        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public static JsonObject DeserializeObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return node ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static string NodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            var kind = value.GetValueKind();
            return kind switch
            {
                JsonValueKind.String => value.GetValue<string?>() ?? string.Empty,
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => value.ToJsonString()
            };
        }

        return node.ToJsonString();
    }

    public static JsonObject CloneObject(JsonObject source)
    {
        return DeserializeObject(source.ToJsonString());
    }

    private static (string ActiveKeyId, byte[] ActiveKeyBytes) GetActiveEncryptionKeyOrThrow(SettingsSecretsOptions options)
    {
        var errors = ValidateSettingsSecrets(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SettingsSecrets configuration:\n - " + string.Join("\n - ", errors));
        }

        var activeKeyId = options.ActiveKeyId.Trim();
        var encoded = options.Keys[activeKeyId];
        if (!TryDecodeBase64Key(encoded, out var keyBytes) || !IsValidAesKeyLength(keyBytes.Length))
        {
            throw new InvalidOperationException(
                $"SettingsSecrets:Keys:{activeKeyId} must decode to a valid AES key (16, 24, or 32 bytes).");
        }

        return (activeKeyId, keyBytes);
    }

    private static Dictionary<string, byte[]> BuildReadKeyRing(SettingsSecretsOptions options)
    {
        var ring = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (keyId, encodedKey) in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(encodedKey))
            {
                continue;
            }

            if (!TryDecodeBase64Key(encodedKey, out var keyBytes))
            {
                continue;
            }

            if (!IsValidAesKeyLength(keyBytes.Length))
            {
                continue;
            }

            ring[keyId] = keyBytes;
        }

        return ring;
    }

    private static string EncryptEncV2(string plaintext, string keyId, byte[] keyBytes)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(keyBytes, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintextBytes, cipherBytes, tag);
        }

        var packed = new byte[NonceSizeBytes + cipherBytes.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, packed, NonceSizeBytes, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSizeBytes + cipherBytes.Length, TagSizeBytes);

        var encoded = ToBase64Url(packed);
        return $"{SecretCipherPrefix}{keyId}{EnvelopeSeparator}{encoded}";
    }

    private static bool TryDecryptEncV2(string raw, IReadOnlyDictionary<string, byte[]> keyRing, out string plaintext)
    {
        plaintext = string.Empty;

        var envelope = raw[SecretCipherPrefix.Length..];
        var delimiterIndex = envelope.IndexOf(EnvelopeSeparator, StringComparison.Ordinal);
        if (delimiterIndex <= 0 || delimiterIndex == envelope.Length - EnvelopeSeparator.Length)
        {
            return false;
        }

        var keyId = envelope[..delimiterIndex];
        var encoded = envelope[(delimiterIndex + EnvelopeSeparator.Length)..];
        if (!keyRing.TryGetValue(keyId, out var keyBytes))
        {
            return false;
        }

        byte[] packed;
        try
        {
            packed = FromBase64Url(encoded);
        }
        catch
        {
            return false;
        }

        if (packed.Length <= NonceSizeBytes + TagSizeBytes)
        {
            return false;
        }

        var nonce = packed.AsSpan(0, NonceSizeBytes).ToArray();
        var cipherLength = packed.Length - NonceSizeBytes - TagSizeBytes;
        var cipher = packed.AsSpan(NonceSizeBytes, cipherLength).ToArray();
        var tag = packed.AsSpan(NonceSizeBytes + cipherLength, TagSizeBytes).ToArray();
        var plaintextBytes = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(keyBytes, TagSizeBytes);
            aes.Decrypt(nonce, cipher, tag, plaintextBytes);
            plaintext = System.Text.Encoding.UTF8.GetString(plaintextBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeBase64Key(string raw, out byte[] keyBytes)
    {
        keyBytes = Array.Empty<byte>();

        try
        {
            keyBytes = Convert.FromBase64String(raw.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidAesKeyLength(int keyLength)
    {
        return keyLength is 16 or 24 or 32;
    }

    private static string ToBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("Invalid base64url length.")
        };

        return Convert.FromBase64String(padded);
    }
}
