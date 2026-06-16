namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Options controlling how run output images are embedded into MCP tool responses.
/// Bound from the "Mcp" configuration section.
/// </summary>
public sealed class McpImageEmbeddingOptions
{
    public const string SectionName = "Mcp";

    /// <summary>Whether to embed run output images as <c>ImageContentBlock</c>s.</summary>
    public bool EmbedImages { get; set; } = true;

    /// <summary>Maximum number of images embedded in a single tool response.</summary>
    public int MaxImagesPerResponse { get; set; } = 5;

    /// <summary>Maximum size (bytes) of an individual image to embed; larger files are skipped.</summary>
    public long MaxImageBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Whether files modified during the turn (in addition to created) are eligible for embedding.</summary>
    public bool IncludeModifiedFiles { get; set; } = true;
}
