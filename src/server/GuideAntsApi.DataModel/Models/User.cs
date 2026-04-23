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
    }
}