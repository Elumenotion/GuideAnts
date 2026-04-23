namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// Enumeration representing different types of authentication methods.
    /// The enum is serialized as a JSON string.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AuthType
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        none,
        service_http,
        service_query,
        oauth,
        azure_oauth
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }

    /// <summary>
    /// Represents domain-specific authorization configurations.
    /// </summary>
    public class DomainAuth
    {
        /// <summary>
        /// Gets or sets the dictionary mapping host names to their corresponding authorization configurations.
        /// </summary>
        [JsonPropertyName("hosts")]
        public Dictionary<string, ActionAuthConfig> HostAuthorizationConfigurations { get; set; } = new();
    }

    /// <summary>
    /// Represents the authorization configuration for a specific action.
    /// This record holds various settings for different authentication types.
    /// </summary>
    public record ActionAuthConfig
    {
        /// <summary>
        /// Gets or sets the type of authentication.
        /// </summary>
        [JsonPropertyName("auth_type")]
        public AuthType AuthType { get; set; }

        /// <summary>
        /// Gets or sets the header key name for the HTTP request.
        /// Ignored when null during JSON serialization.
        /// </summary>
        [JsonPropertyName("header_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HeaderKey { get; set; }

        /// <summary>
        /// Gets or sets the environment variable name for the header value.
        /// Ignored when null during JSON serialization.
        /// </summary>
        [JsonPropertyName("header_value_env_var")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HeaderValueEnvironmentVariable { get; set; }

        /// <summary>
        /// Gets or sets the literal value for the header. Used for owner-scoped secrets.
        /// Ignored when null during JSON serialization.
        /// </summary>
        [JsonPropertyName("header_value_literal")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HeaderValueLiteral { get; set; }

        /// <summary>
        /// Gets or sets the OAuth client ID.
        /// Ignored when null during JSON serialization.
        /// </summary>
        [JsonPropertyName("oauth_clientId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OAuthClientId { get; set; }

        /// <summary>
        /// Gets or sets the environment variable name for the OAuth client secret.
        /// Ignored when null during JSON serialization.
        /// </summary>
        [JsonPropertyName("oauth_secret_env_var")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OAuthClientSecretEnvironmentVariable { get; set; }
    }
}