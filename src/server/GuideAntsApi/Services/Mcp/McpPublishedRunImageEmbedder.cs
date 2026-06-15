using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Builds MCP <see cref="ImageContentBlock"/>s from the output files a published-guide run actually
/// produced. The authoritative source is <c>ConversationTurn.FilesCreated</c>/<c>FilesModified</c>
/// (CWD-relative paths tracked during the run) rather than URLs parsed from assistant markdown.
/// </summary>
public sealed class McpPublishedRunImageEmbedder
{
    private static readonly HashSet<string> ImageMimeWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ApplicationDbContext _db;
    private readonly INotebookFileService _fileService;
    private readonly McpImageEmbeddingOptions _options;
    private readonly ILogger<McpPublishedRunImageEmbedder> _logger;

    public McpPublishedRunImageEmbedder(
        ApplicationDbContext db,
        INotebookFileService fileService,
        IOptions<McpImageEmbeddingOptions> options,
        ILogger<McpPublishedRunImageEmbedder> logger)
    {
        _db = db;
        _fileService = fileService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the image files created (and optionally modified) during the given turn and returns
    /// them as <see cref="ImageContentBlock"/>s, honoring the configured count and size caps.
    /// </summary>
    public async Task<IReadOnlyList<ImageContentBlock>> EmbedTurnImagesAsync(
        Guid notebookId,
        Guid conversationId,
        int turnIndex,
        CancellationToken cancellationToken)
    {
        if (!_options.EmbedImages || _options.MaxImagesPerResponse <= 0)
        {
            return [];
        }

        var turn = await _db.ConversationTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.NotebookConversationId == conversationId && t.TurnIndex == turnIndex,
                cancellationToken);
        if (turn == null)
        {
            return [];
        }

        var cwdPaths = ParsePaths(turn.FilesCreated);
        if (_options.IncludeModifiedFiles)
        {
            cwdPaths = cwdPaths.Concat(ParsePaths(turn.FilesModified));
        }

        var blocks = new List<ImageContentBlock>();
        var seenFileIds = new HashSet<Guid>();

        foreach (var cwdPath in cwdPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (blocks.Count >= _options.MaxImagesPerResponse)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(cwdPath))
            {
                continue;
            }

            // Match CWD-relative path to a notebook file by suffix (same approach as TurnManager).
            var notebookFile = await _db.NotebookFiles
                .AsNoTracking()
                .Where(f => f.NotebookId == notebookId && f.RelativePath.EndsWith(cwdPath))
                .OrderByDescending(f => f.LastModifiedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (notebookFile == null || !seenFileIds.Add(notebookFile.Id))
            {
                continue;
            }

            var loaded = await _fileService.GetFileContentStreamAsync(notebookFile.Id, cancellationToken);
            if (loaded == null)
            {
                continue;
            }

            var (stream, contentType, _) = loaded.Value;
            await using (stream)
            {
                if (!ImageMimeWhitelist.Contains(contentType))
                {
                    continue;
                }

                var bytes = await ReadCappedAsync(stream, _options.MaxImageBytes, cancellationToken);
                if (bytes == null)
                {
                    _logger.LogInformation(
                        "Skipping oversized MCP image embed for notebook file {NotebookFileId} (limit {MaxImageBytes} bytes)",
                        notebookFile.Id,
                        _options.MaxImageBytes);
                    continue;
                }

                blocks.Add(ImageContentBlock.FromBytes(bytes, contentType));
            }
        }

        return blocks;
    }

    private static IEnumerable<string> ParsePaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the stream fully unless it would exceed <paramref name="maxBytes"/>, in which case null
    /// is returned without buffering the whole payload.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
