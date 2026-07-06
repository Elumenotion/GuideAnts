namespace GuideAntsApi.Services.HuggingFace;

/// <summary>
/// Single source of truth for the Hugging Face token used by every HF
/// download path in the app (llama quants + mmproj, Stable Diffusion bundles,
/// ASR, TTS, embeddings catalog downloads).
///
/// <para>
/// The value lives in the <c>HuggingFace</c> application-settings section as
/// the single secret property <c>Token</c> (see
/// <see cref="Settings.SettingsSectionRegistry"/>). The DB-backed setting is
/// authoritative.
/// </para>
///
/// <para>
/// The resolver is intentionally trivial — one DB-backed read — because the
/// rule is: one token, one place, no per-request overrides, no per-service
/// duplicates. Anything that downloads from Hugging Face on behalf of the app
/// MUST route through this resolver (directly, or via a server endpoint that
/// injects the resolved value before forwarding to a downstream service).
/// </para>
/// </summary>
public interface IHuggingFaceTokenResolver
{
    /// <summary>
    /// Returns the configured token, trimmed, or <c>null</c> when no token is
    /// configured. Callers MUST surface a clear, actionable error when they
    /// actually need a token and this returns <c>null</c> — never silently
    /// retry against a different source.
    /// </summary>
    string? Resolve();
}

public sealed class HuggingFaceTokenResolver : IHuggingFaceTokenResolver
{
    private readonly IConfiguration _configuration;

    public HuggingFaceTokenResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? Resolve()
    {
        var raw = _configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim();
    }
}
