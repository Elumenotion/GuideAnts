using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Services.HuggingFace;

/// <summary>
/// Outcome of browsing an HF repository's file tree. All fields are safe to
/// echo to the browser; the HF token is NEVER included.
/// </summary>
public sealed record HuggingFaceRepositoryListing(
    string Repository,
    bool Gated,
    bool TokenUsed,
    string? ModelCardUrl,
    IReadOnlyList<HuggingFaceRepositoryFile> Files);

/// <summary>
/// One file in an HF repository, categorized for the Add Model wizard.
/// <para><c>Category</c> is one of: <c>gguf</c> (downloadable llama-cpp quant),
/// <c>mmproj</c> (vision projector for multimodal llama-cpp models),
/// <c>other</c> (everything else: tokenizer configs, README, etc.).</para>
/// </summary>
public sealed record HuggingFaceRepositoryFile(
    string Path,
    long? Size,
    string Category,
    string? QuantLabel,
    bool Sharded,
    int? ShardIndex,
    int? ShardTotal);

public sealed class HuggingFaceBrowseException : Exception
{
    public HuggingFaceBrowseException(string code, string message, HttpStatusCode statusCode, string? detail = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Detail = detail;
    }

    public string Code { get; }
    public HttpStatusCode StatusCode { get; }
    public string? Detail { get; }
}

public interface IHuggingFaceRepositoryBrowser
{
    /// <summary>
    /// Lists files in the given <c>owner/repo</c> on Hugging Face and returns
    /// them classified into model / mmproj / other so the UI can render file
    /// pickers. The configured HF token is injected if present; the browser
    /// never sees it.
    /// </summary>
    Task<HuggingFaceRepositoryListing> ListFilesAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default);
}

public sealed class HuggingFaceRepositoryBrowser : IHuggingFaceRepositoryBrowser
{
    private static readonly Regex ShardRegex = new(
        @"-(\d{5})-of-(\d{5})\.gguf$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuantRegex = new(
        @"(?<label>IQ\d+_[A-Z0-9_]+|Q\d+(?:_K(?:_[A-Z])?|_\d+)?|F16|F32|BF16|UD-[A-Z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IHuggingFaceTokenResolver _tokenResolver;
    private readonly ILogger<HuggingFaceRepositoryBrowser> _logger;

    public HuggingFaceRepositoryBrowser(
        HttpClient httpClient,
        IHuggingFaceTokenResolver tokenResolver,
        ILogger<HuggingFaceRepositoryBrowser> logger)
    {
        _httpClient = httpClient;
        _tokenResolver = tokenResolver;
        _logger = logger;
    }

    public async Task<HuggingFaceRepositoryListing> ListFilesAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new HuggingFaceBrowseException(
                "REPO_INVALID",
                "Repository owner is required.",
                HttpStatusCode.BadRequest);
        }
        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new HuggingFaceBrowseException(
                "REPO_INVALID",
                "Repository name is required.",
                HttpStatusCode.BadRequest);
        }

        var ownerClean = owner.Trim().Trim('/');
        var repoClean = repo.Trim().Trim('/');
        var repoFull = $"{ownerClean}/{repoClean}";

        // HF paginates when recursive=true via Link header. Follow all pages.
        var url = $"https://huggingface.co/api/models/{Uri.EscapeDataString(ownerClean)}/{Uri.EscapeDataString(repoClean)}/tree/main?recursive=true";

        var token = _tokenResolver.Resolve();
        var tokenUsed = false;
        var files = new List<HuggingFaceRepositoryFile>();

        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GuideAnts", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                tokenUsed = true;
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var hasToken = !string.IsNullOrEmpty(token);
                throw new HuggingFaceBrowseException(
                    hasToken ? "REPO_TOKEN_INSUFFICIENT" : "REPO_TOKEN_MISSING",
                    hasToken
                        ? $"The configured Hugging Face token does not grant access to '{repoFull}'."
                        : $"Repository '{repoFull}' is gated or private; configure a Hugging Face token under Settings \u2192 Hugging Face.",
                    response.StatusCode,
                    Truncate(body, 512));
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HuggingFaceBrowseException(
                    "REPO_NOT_FOUND",
                    $"Repository '{repoFull}' was not found on Hugging Face. Check the owner/name spelling.",
                    HttpStatusCode.NotFound,
                    Truncate(body, 512));
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HF tree listing failed for {Repo}. status={Status} body={Body}",
                    repoFull, (int)response.StatusCode, Truncate(body, 512));
                throw new HuggingFaceBrowseException(
                    "HF_UPSTREAM",
                    $"Hugging Face returned {(int)response.StatusCode} while listing '{repoFull}'.",
                    HttpStatusCode.BadGateway,
                    Truncate(body, 512));
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new HuggingFaceBrowseException(
                        "HF_UPSTREAM",
                        "Hugging Face returned an unexpected response shape (expected a JSON array).",
                        HttpStatusCode.BadGateway);
                }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "file")
                    {
                        continue;
                    }
                    if (!item.TryGetProperty("path", out var pathEl))
                    {
                        continue;
                    }
                    var path = pathEl.GetString();
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    long? size = null;
                    if (item.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out var sizeVal))
                    {
                        size = sizeVal;
                    }

                    files.Add(BuildFile(path, size));
                }
            }
            catch (JsonException ex)
            {
                throw new HuggingFaceBrowseException(
                    "HF_UPSTREAM",
                    "Hugging Face returned a response that was not valid JSON.",
                    HttpStatusCode.BadGateway,
                    ex.Message);
            }

            // RFC5988 Link header may contain rel="next" for long trees.
            url = response.Headers.TryGetValues("Link", out var linkValues)
                ? ParseNextLink(linkValues)
                : null;
        }

        return new HuggingFaceRepositoryListing(
            Repository: repoFull,
            Gated: false,
            TokenUsed: tokenUsed,
            ModelCardUrl: $"https://huggingface.co/{ownerClean}/{repoClean}",
            Files: files);
    }

    private static HuggingFaceRepositoryFile BuildFile(string path, long? size)
    {
        var leaf = System.IO.Path.GetFileName(path);
        var lowerLeaf = leaf.ToLowerInvariant();
        var isGguf = lowerLeaf.EndsWith(".gguf", StringComparison.Ordinal);
        var isMmproj = lowerLeaf.Contains("mmproj", StringComparison.Ordinal);

        string category;
        if (isMmproj && isGguf)
        {
            category = "mmproj";
        }
        else if (isGguf)
        {
            category = "gguf";
        }
        else if (isMmproj)
        {
            category = "mmproj";
        }
        else
        {
            category = "other";
        }

        var sharded = false;
        int? shardIndex = null;
        int? shardTotal = null;
        var shardMatch = ShardRegex.Match(leaf);
        if (shardMatch.Success)
        {
            sharded = true;
            if (int.TryParse(shardMatch.Groups[1].Value, out var idx))
            {
                shardIndex = idx;
            }
            if (int.TryParse(shardMatch.Groups[2].Value, out var total))
            {
                shardTotal = total;
            }
        }

        string? quantLabel = null;
        if (isGguf && !isMmproj)
        {
            var quantMatch = QuantRegex.Match(leaf);
            if (quantMatch.Success)
            {
                quantLabel = quantMatch.Groups["label"].Value.ToUpperInvariant();
            }
        }

        return new HuggingFaceRepositoryFile(
            Path: path,
            Size: size,
            Category: category,
            QuantLabel: quantLabel,
            Sharded: sharded,
            ShardIndex: shardIndex,
            ShardTotal: shardTotal);
    }

    private static string? ParseNextLink(IEnumerable<string> linkValues)
    {
        foreach (var header in linkValues)
        {
            foreach (var part in header.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
                {
                    var lt = trimmed.IndexOf('<');
                    var gt = trimmed.IndexOf('>');
                    if (lt >= 0 && gt > lt)
                    {
                        return trimmed.Substring(lt + 1, gt - lt - 1);
                    }
                }
            }
        }
        return null;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, max) + "\u2026";
    }
}
