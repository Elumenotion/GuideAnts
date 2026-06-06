using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Server-side persisted PKCE state for OAuth authorization-code exchanges.
    /// Bound to the authenticated user and provider.
    /// </summary>
    public class OAuthAuthorizationState
    {
        [Key]
        [StringLength(128)]
        public string State { get; set; } = string.Empty;

        [Required]
        public Guid UserId { get; set; }

        public User User { get; set; } = null!;

        [Required]
        [StringLength(255)]
        public string ProviderId { get; set; } = string.Empty;

        [Required]
        [StringLength(512)]
        public string CodeVerifier { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string ClientId { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Tenant { get; set; } = "organizations";

        [StringLength(4000)]
        public string? Scopes { get; set; }

        [Required]
        [StringLength(2048)]
        public string RedirectUri { get; set; } = string.Empty;

        [StringLength(2048)]
        public string? ReturnUrl { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime ExpiresAt { get; set; }
    }
}
