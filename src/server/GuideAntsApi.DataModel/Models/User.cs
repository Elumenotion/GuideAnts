using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class User
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [StringLength(255)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        // Identity provider information for multi-provider support
        [StringLength(255)]
        public string? IdentityIssuer { get; set; }

        [StringLength(255)]
        public string? IdentitySubject { get; set; }

        public string? PasswordHash { get; set; }

        [Required]
        public Guid SecurityStamp { get; set; } = Guid.NewGuid();

        public DateTime? LastLoginAt { get; set; }

        public Guid? ApprovedByUserId { get; set; }

        public User? ApprovedByUser { get; set; }

        public DateTime? ApprovedAt { get; set; }

        [Required]
        public bool MustChangePassword { get; set; }

        /// <summary>
        /// OSS-lite user preferences payload.
        /// Intended to evolve without frequent schema changes.
        /// </summary>
        public string? PreferencesJson { get; set; }

        [StringLength(100)]
        public string? TimeZone { get; set; }

        [StringLength(20)]
        public string? Locale { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
        public ICollection<ExternalOAuthToken> ExternalOAuthTokens { get; set; } = new List<ExternalOAuthToken>();
        public ICollection<OAuthAuthorizationState> OAuthAuthorizationStates { get; set; } = new List<OAuthAuthorizationState>();
        public ICollection<CliAuthSession> CliAuthSessions { get; set; } = new List<CliAuthSession>();
    }
}