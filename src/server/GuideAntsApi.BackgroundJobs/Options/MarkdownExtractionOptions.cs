namespace GuideAntsApi.BackgroundJobs.Options;

public sealed class MarkdownExtractionOptions
{
    public const string SectionName = "MarkdownExtraction";
    public bool Enabled { get; set; } = true;
    public int ProcessingIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 5;
    public int MaxFileSizeMB { get; set; } = 500;
    public string[] SupportedExtensions { get; set; } = Array.Empty<string>();
}



