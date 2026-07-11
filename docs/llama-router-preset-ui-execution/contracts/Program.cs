using System.Text.Json;

var contractsDir = AppContext.BaseDirectory;
while (!string.IsNullOrEmpty(contractsDir))
{
    var candidate = Path.Combine(contractsDir, "docs", "llama-router-preset-ui-execution", "contracts");
    if (Directory.Exists(candidate))
    {
        contractsDir = candidate;
        break;
    }

    var parent = Directory.GetParent(contractsDir);
    contractsDir = parent?.FullName ?? string.Empty;
}

if (!Directory.Exists(contractsDir))
{
    contractsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory()));
}

var fixturePaths = Directory.GetFiles(contractsDir, "*.fixture.json", SearchOption.TopDirectoryOnly);
var schemaPaths = Directory.GetFiles(contractsDir, "schema.*.json", SearchOption.TopDirectoryOnly);
var failures = new List<string>();

foreach (var path in schemaPaths.Concat(fixturePaths).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var stream = File.OpenRead(path);
        JsonDocument.Parse(stream);
    }
    catch (JsonException ex)
    {
        failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
    }
}

var d12Path = Path.Combine(contractsDir, "immutable-operation-input.fixture.json");
if (File.Exists(d12Path))
{
    using var stream = File.OpenRead(d12Path);
    using var doc = JsonDocument.Parse(stream);
    var root = doc.RootElement;
    if (root.TryGetProperty("mmprojFiles", out var mmprojFiles) && mmprojFiles.GetArrayLength() > 0)
    {
        failures.Add("immutable-operation-input.fixture.json: D12 requires empty mmprojFiles for MTP");
    }

    if (root.TryGetProperty("routerPreset", out var preset) && preset.ValueKind == JsonValueKind.Object)
    {
        foreach (var forbidden in new[] { "image-min-tokens", "mmproj", "projector" })
        {
            if (preset.TryGetProperty(forbidden, out _))
            {
                failures.Add($"immutable-operation-input.fixture.json: D12 forbids routerPreset key '{forbidden}'");
            }
        }
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL {failure}");
    }

    Environment.ExitCode = 1;
    return;
}

Console.WriteLine(
    $"PASS parsed {fixturePaths.Length} fixtures and {schemaPaths.Length} schema files under contracts/");
Environment.ExitCode = 0;
