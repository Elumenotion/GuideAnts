namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Explicit authentication mode for a published guide. Exactly one mode applies
    /// per published guide (mutually exclusive). Replaces the previous implicit
    /// inference from the nullable <see cref="PublishedGuide.ApiKeyHash"/> /
    /// <see cref="PublishedGuide.AuthValidationWebhookUrl"/> columns.
    /// </summary>
    public enum PublishedGuideAuthMode
    {
        /// <summary>No authentication required on published endpoints.</summary>
        Anonymous = 0,

        /// <summary>X-Published-Auth token POSTed to the configured webhook URL.</summary>
        Webhook = 1,

        /// <summary>x-guideants-apikey header required, validated against the stored hash.</summary>
        ApiKey = 2,

        /// <summary>
        /// X-Published-Auth / session cookie must be a valid GuideAnts app JWT; the
        /// caller is resolved to a real user. Settable only via internal/server paths.
        /// </summary>
        AppIdentity = 3,
    }
}
