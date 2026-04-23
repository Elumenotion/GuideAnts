using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(Key), IsUnique = true)]
public class UsageReportCategory
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty; // stable key like "chat", "search"

    [Required]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Description { get; set; }

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public ICollection<UsageReportCategoryOperation> Operations { get; set; } = new List<UsageReportCategoryOperation>();
}

[Index(nameof(Operation))]
public class UsageReportCategoryOperation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string Operation { get; set; } = string.Empty; // joins to UsageEvent.Operation

    [Required]
    public Guid UsageReportCategoryId { get; set; }
    public UsageReportCategory UsageReportCategory { get; set; } = null!;
}





