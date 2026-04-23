using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;

namespace GuideAntsApi.DataModel.Models
{
    public class ContentFile
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        public string Path { get; set; } = string.Empty;

        [Required]
        public string RelativePath { get; set; } = string.Empty;

        [Required]
        public long FileSize { get; set; }

        [Required]
        public string ContentType { get; set; } = string.Empty;


        [Required]
        [StringLength(255)]
        public string DocumentId { get; private set; } = string.Empty;

        [Required]
        public Guid ProjectId { get; set; }
        public Project Project { get; set; } = null!;

        public Guid? FolderId { get; set; }
        public ProjectFolder? Folder { get; set; }

        public int LatestVersion { get; set; } = 1;

        public bool IsSnapshot { get; set; } = false;



        public ICollection<ContentFileVersion> Versions { get; set; } = [];

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public void GenerateDocumentId()
        {
            // Use stable identifiers so project/notebook slug/path renames do not invalidate embeddings.
            using var sha256 = SHA256.Create();
            var normalizedRelativePath = (RelativePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            var pathBytes = Encoding.UTF8.GetBytes($"{ProjectId}:{normalizedRelativePath}");
            var hashBytes = sha256.ComputeHash(pathBytes);
            
            DocumentId = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        public void OnSaveOrUpdate()
        {
            GenerateDocumentId();
        }

        /// <summary>
        /// Updates the relative path based on the folder and filename
        /// </summary>
        public void UpdateRelativePath()
        {
            if (Folder != null)
            {
                RelativePath = string.IsNullOrEmpty(Folder.RelativePath) 
                    ? FileName 
                    : $"{Folder.RelativePath}/{FileName}";
            }
            else
            {
                RelativePath = FileName;
            }
        }

        /// <summary>
        /// Gets the full physical path for this file including the project directory
        /// </summary>
        public string GetPhysicalPath(string basePath)
        {
            return System.IO.Path.Combine(basePath, ProjectId.ToString(), RelativePath);
        }

        /// <summary>
        /// Gets the directory path for this file (without filename)
        /// </summary>
        public string GetDirectoryPath()
        {
            var dirPath = System.IO.Path.GetDirectoryName(RelativePath);
            return string.IsNullOrEmpty(dirPath) ? "" : dirPath.Replace('\\', '/');
        }
    }
} 
