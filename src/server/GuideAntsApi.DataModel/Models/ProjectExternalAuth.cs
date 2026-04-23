using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Per-project external auth configuration overrides, keyed by provider id (host).
/// Stores user-provided overrides to manifest defaults for OAuth (clientId/tenant) and service_http (headerName/headerValue).
/// </summary>
public class ProjectExternalAuth
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>
    /// Provider id, typically the host (e.g., graph.microsoft.com or api.search.brave.com).
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Discriminator so we can validate fields at write time.
    /// Values: "oauth" or "service_http".
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string AuthType { get; set; } = string.Empty;

    // --- OAuth overrides (same names as manifest) ---
    [MaxLength(200)] public string? ClientId { get; set; }
    [MaxLength(100)] public string? Tenant { get; set; }

    // --- Service HTTP overrides ---
    [MaxLength(255)] public string? HeaderName { get; set; }
    [MaxLength(4096)] public string? HeaderValue { get; set; }

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;
}