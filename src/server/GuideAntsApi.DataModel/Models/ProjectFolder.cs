using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class ProjectFolder
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [StringLength(255)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string RelativePath { get; set; } = string.Empty;

        [Required]
        public Guid ProjectId { get; set; }
        public Project Project { get; set; } = null!;

        public Guid? ParentFolderId { get; set; }
        public ProjectFolder? ParentFolder { get; set; }

        public ICollection<ProjectFolder> SubFolders { get; set; } = [];
        public ICollection<ContentFile> ContentFiles { get; set; } = [];

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Modified { get; set; }

        /// <summary>
        /// Validates that the folder name contains only valid characters
        /// </summary>
        public bool IsValidName()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return false;

            var invalidChars = Path.GetInvalidFileNameChars();
            return !Name.Any(c => invalidChars.Contains(c));
        }

        /// <summary>
        /// Updates the relative path based on parent folder
        /// </summary>
        public void UpdateRelativePath(ProjectFolder? parentFolder = null)
        {
            if (parentFolder != null)
            {
                RelativePath = string.IsNullOrEmpty(parentFolder.RelativePath) 
                    ? Name 
                    : $"{parentFolder.RelativePath}/{Name}";
            }
            else
            {
                RelativePath = Name;
            }
            
            Modified = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets the full physical path for this folder
        /// </summary>
        public string GetPhysicalPath(string basePath)
        {
            return Path.Combine(basePath, ProjectId.ToString(), RelativePath);
        }

        /// <summary>
        /// Checks if this folder is empty (no files or subfolders)
        /// </summary>
        public bool IsEmpty()
        {
            return !ContentFiles.Any() && !SubFolders.Any();
        }

        /// <summary>
        /// Gets all descendant folders recursively
        /// </summary>
        public IEnumerable<ProjectFolder> GetAllDescendants()
        {
            var descendants = new List<ProjectFolder>();
            
            foreach (var subFolder in SubFolders)
            {
                descendants.Add(subFolder);
                descendants.AddRange(subFolder.GetAllDescendants());
            }
            
            return descendants;
        }

        /// <summary>
        /// Validates that this folder can be moved to the specified parent
        /// (prevents circular references)
        /// </summary>
        public bool CanMoveTo(ProjectFolder? newParent)
        {
            if (newParent == null) return true;
            if (newParent.Id == Id) return false;
            
            // Check if newParent is a descendant of this folder
            return !GetAllDescendants().Any(d => d.Id == newParent.Id);
        }
    }
} 