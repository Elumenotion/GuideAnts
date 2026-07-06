using System.Text.Json;

namespace GuideAntsApi.Endpoints.Settings;

internal static class ServiceLocalModelDownloadValidator
{
    public static IResult? ValidateDownloadPayload(string serviceId, JsonElement payload)
    {
        if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
        {
            return ValidateImageGenerationBundle(payload);
        }

        if (!LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out _))
        {
            return Results.BadRequest(new { error = "model_id is required." });
        }

        return null;
    }

    public static IResult? ValidateCatalogMembership(string modelId, IReadOnlySet<string> catalogIds)
    {
        if (!catalogIds.Contains(modelId))
        {
            return Results.BadRequest(new
            {
                error = $"model_id '{modelId}' is not in the curated model catalog.",
            });
        }

        return null;
    }

    private static IResult? ValidateImageGenerationBundle(JsonElement payload)
    {
        // The bundle contract is strict on purpose: each role is a
        // (repo, single filename) pair, all required. There are no
        // defaults, no optional fallbacks, and no globs — the caller
        // must name exactly one file per role so a bundle download
        // cannot accidentally pull a whole multi-quantization repo.
        var requiredRepoFields = new (string Name, string Description)[]
        {
            ("bundle_id", "a local id for the bundle on disk"),
            ("diffusion_repo", "Hugging Face repo id for the diffusion weights"),
            ("diffusion_file", "single filename of the diffusion file inside the diffusion repo"),
            ("vae_repo", "Hugging Face repo id for the VAE"),
            ("vae_file", "single filename of the VAE file inside the VAE repo"),
            ("text_encoder_repo", "Hugging Face repo id for the text encoder / LLM"),
            ("text_encoder_file", "single filename of the text encoder file inside the text encoder repo"),
        };
        foreach (var (name, description) in requiredRepoFields)
        {
            if (!LocalServiceAdminRouting.TryGetNonEmptyString(payload, name, out _))
            {
                return Results.BadRequest(new
                {
                    error = $"{name} is required ({description}).",
                });
            }
        }

        // Reject glob metacharacters in the filename fields. The
        // upstream SD service uses huggingface_hub allow_patterns
        // literally; a stray '*' would silently widen the download
        // to every matching file in the repo.
        var filenameFields = new[] { "diffusion_file", "vae_file", "text_encoder_file" };
        foreach (var field in filenameFields)
        {
            if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, field, out var filename)
                && (filename.Contains('*') || filename.Contains('?')))
            {
                return Results.BadRequest(new
                {
                    error = $"{field} must be a single filename (no '*' or '?' glob metacharacters).",
                });
            }
        }

        return null;
    }
}
