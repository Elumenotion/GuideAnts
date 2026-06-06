using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Per-user OAuth token grant for external tool providers.
    /// Uniqueness is enforced per (UserId, ProviderId).
    /// </summary>
    public class ExternalOAuthToken
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        public User User { get; set; } = null!;

        /// <summary>
        /// Optional project reference for auditing where the grant was established.
        /// Not part of uniqueness or lookup.
        /// </summary>
        public Guid? ProjectId { get; set; }

        public Project? Project { get; set; }

        [Required]
        [StringLength(255)]
        public string ProviderId { get; set; } = string.Empty;

        [Required]
        public string AccessTokenEncrypted { get; set; } = string.Empty;

        public string? RefreshTokenEncrypted { get; set; }

        [Required]
        public DateTime ExpiresAt { get; set; }

        [StringLength(4000)]
        public string? Scope { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }
}
