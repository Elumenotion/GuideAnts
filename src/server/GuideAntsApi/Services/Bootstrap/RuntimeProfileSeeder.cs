using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class RuntimeProfileSeeder : IRuntimeProfileSeeder
{
    private const string ProfilesFolderRelativePath = "Resources/bootstrap/runtime-profiles";

    private readonly IWebHostEnvironment _environment;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<RuntimeProfileSeeder> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RuntimeProfileSeeder(
        IWebHostEnvironment environment,
        ApplicationDbContext dbContext,
        ILogger<RuntimeProfileSeeder> logger)
    {
        _environment = environment;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var profilesFolder = Path.Combine(_environment.ContentRootPath, ProfilesFolderRelativePath);
        if (!Directory.Exists(profilesFolder))
        {
            _logger.LogInformation(
                "Bootstrap runtime profiles folder not found at {Path}. Skipping runtime profile seed.",
                profilesFolder);
            return;
        }

        var seedFiles = Directory.EnumerateFiles(profilesFolder, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var seedFile in seedFiles)
        {
            var fileName = Path.GetFileName(seedFile);
            try
            {
                await SeedProfileFromFileAsync(seedFile, fileName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to seed runtime profile from {FileName}.", fileName);
                throw;
            }
        }
    }

    private async Task SeedProfileFromFileAsync(string filePath, string fileName, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("profileId", out var profileIdElement))
        {
            throw new InvalidOperationException(
                $"Bootstrap runtime profile seed '{fileName}' is missing 'profileId'.");
        }

        var profileId = profileIdElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException(
                $"Bootstrap runtime profile seed '{fileName}' has a blank 'profileId'.");
        }

        var exists = await _dbContext.RuntimeProfiles
            .AnyAsync(p => p.ProfileId == profileId, cancellationToken);
        if (exists)
        {
            _logger.LogInformation(
                "Runtime profile '{ProfileId}' already exists. Skipping seed {FileName}.",
                profileId, fileName);
            return;
        }

        var entity = new RuntimeProfile
        {
            ProfileId = profileId,
            DisplayName = GetRequiredString(root, "displayName", fileName),
            Description = GetOptionalString(root, "description"),
            CombineSystemAndDeveloperMessages = root.TryGetProperty("combineSystemAndDeveloperMessages", out var csadm) && csadm.GetBoolean(),
            ThoughtBlockPattern = GetOptionalString(root, "thoughtBlockPattern"),
            SamplingParametersJson = GetNestedObjectAsString(root, "samplingParametersJson", fileName),
            ThinkingControlJson = GetNestedObjectAsString(root, "thinkingControlJson", fileName),
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        };

        _dbContext.RuntimeProfiles.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded bootstrap runtime profile '{ProfileId}' from {FileName}.",
            profileId, fileName);
    }

    private static string GetRequiredString(JsonElement root, string propertyName, string fileName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidOperationException(
                $"Bootstrap runtime profile seed '{fileName}' is missing '{propertyName}'.");
        }

        var value = element.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Bootstrap runtime profile seed '{fileName}' has a blank '{propertyName}'.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) ? element.GetString() : null;
    }

    /// <summary>
    /// The seed files store samplingParametersJson and thinkingControlJson as nested JSON objects
    /// for readability. The DB columns store them as serialized JSON strings, so re-serialize here.
    /// </summary>
    private static string GetNestedObjectAsString(JsonElement root, string propertyName, string fileName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidOperationException(
                $"Bootstrap runtime profile seed '{fileName}' is missing '{propertyName}'.");
        }

        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "{}";

        if (element.ValueKind == JsonValueKind.Object)
            return element.GetRawText();

        throw new InvalidOperationException(
            $"Bootstrap runtime profile seed '{fileName}': '{propertyName}' must be a JSON object or string.");
    }
}
