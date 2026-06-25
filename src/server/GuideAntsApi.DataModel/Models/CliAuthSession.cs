using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Server-side persisted state for the CLI device-code authorization flow.
    /// The CLI polls with a hashed device secret; the browser approves with the session ID.
    /// </summary>
    public class CliAuthSession
    {
        [Key]
        [StringLength(128)]
        public string SessionId { get; set; } = string.Empty;

        [Required]
        [StringLength(128)]
        public string DeviceSecretHash { get; set; } = string.Empty;

        public CliAuthSessionStatus Status { get; set; } = CliAuthSessionStatus.Pending;

        public Guid? UserId { get; set; }

        public User? User { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime ExpiresAt { get; set; }
    }
}
