namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class CuratedInstallErrorCodes
{
    public const string CuratedForbiddenField = "CURATED_FORBIDDEN_FIELD";
    public const string CatalogVersionUnavailable = "CATALOG_VERSION_UNAVAILABLE";
    public const string CatalogDefinitionNotFound = "CATALOG_DEFINITION_NOT_FOUND";
    public const string CommitUnavailable = "COMMIT_UNAVAILABLE";
    public const string CommitChanged = "COMMIT_CHANGED";
    public const string QuantMissing = "QUANT_MISSING";
    public const string QuantIncomplete = "QUANT_INCOMPLETE";
    public const string ProjectorMissing = "PROJECTOR_MISSING";
    public const string PresetInvalid = "PRESET_INVALID";
    public const string CatalogFinalization = "CATALOG_FINALIZATION";
    public const string HuggingFaceTokenMissing = "HUGGINGFACE_TOKEN_MISSING";
    public const string RepoTokenInsufficient = "REPO_TOKEN_INSUFFICIENT";
}
