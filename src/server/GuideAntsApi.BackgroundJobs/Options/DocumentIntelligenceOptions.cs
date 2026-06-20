namespace GuideAntsApi.BackgroundJobs.Options;

public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public int TimeoutSeconds { get; set; } = 300;
    public int MaxConcurrentConversions { get; set; } = 1;
    public int AsyncStatusPollIntervalMs { get; set; } = 2000;
    public bool? DoclingDoOcr { get; set; }
    public bool? DoclingForceOcr { get; set; }
    public string? DoclingOcrPreset { get; set; }
    public string? DoclingOcrLang { get; set; }
    public string? DoclingPdfBackend { get; set; }
    public string? DoclingTableMode { get; set; }
    public bool? DoclingTableCellMatching { get; set; }
    public string? DoclingImageExportMode { get; set; }
    public string? DoclingPipeline { get; set; }
    public bool? DoclingDoCodeEnrichment { get; set; }
    public bool? DoclingDoFormulaEnrichment { get; set; }
    public bool? DoclingDoPictureClassification { get; set; }
    public bool? DoclingDoPictureDescription { get; set; }
    public string? DoclingPictureDescriptionPreset { get; set; }
    public string? DoclingApiKey { get; set; }
}
