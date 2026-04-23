using System.IO;

namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// Static class containing drive-related utility functions.
    /// </summary>
    public static class DriveTools
    {
        /// <summary>
        /// Lists all available drives and their details.
        /// </summary>
        /// <returns>A list of DriveDetails objects representing the drives.</returns>
        public static List<DriveDetails> ListDrives()
        {
            // Get all the drives on the system
            var drives = DriveInfo.GetDrives();
            // Initialize a list to hold drive details
            var driveDetailsList = new List<DriveDetails>();

            // Iterate through each drive
            foreach (var drive in drives)
            {
                // Check if the drive is ready for use
                if (drive.IsReady)
                {
                    // Create a DriveDetails object and populate its properties
                    var driveDetails = new DriveDetails
                    {
                        Name = drive.Name ?? string.Empty,
                        DriveType = drive.DriveType.ToString(),
                        TotalSize = drive.TotalSize,
                        AvailableFreeSpace = drive.AvailableFreeSpace,
                        IsReady = drive.IsReady
                    };

                    // Add the drive details to the list
                    driveDetailsList.Add(driveDetails);
                }
            }

            // Return the list of drive details
            return driveDetailsList;
        }

        /// <summary>
        /// Lists all items (files and directories) in a specified path.
        /// </summary>
        /// <param name="path">The path to search for items.</param>
        /// <param name="recurse">Whether to search recursively through subdirectories.</param>
        /// <param name="searchPattern">The search pattern to match against the names of files and directories. Default is "*".</param>
        /// <returns>A list of ItemDetails objects representing the items.</returns>
        public static List<ItemDetails> ListItems(string path, bool recurse, string searchPattern = "*")
        {
            // Initialize a list to hold item details
            var items = new List<ItemDetails>();
            // Determine the search option based on recursion flag
            var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // Enumerate all directories in the specified path
            foreach (var dir in Directory.EnumerateDirectories(path, searchPattern, searchOption))
            {
                try
                {
                    // Get directory information
                    var dirInfo = new DirectoryInfo(dir);
                    // Create an ItemDetails object for the directory
                    var item = new ItemDetails
                    {
                        Name = dirInfo.Name,
                        Path = dirInfo.FullName,
                        IsDirectory = true,
                        Size = null // Directories do not have a size
                    };
                    // Add the directory details to the list
                    items.Add(item);
                }
                // Handle specific exceptions and continue with the next directory
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (PathTooLongException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
            }

            // Enumerate all files in the specified path
            foreach (var file in Directory.EnumerateFiles(path, searchPattern, searchOption))
            {
                try
                {
                    // Get file information
                    var fileInfo = new FileInfo(file);
                    // Create an ItemDetails object for the file
                    var item = new ItemDetails
                    {
                        Name = fileInfo.Name,
                        Path = fileInfo.FullName,
                        IsDirectory = false,
                        Size = fileInfo.Length
                    };
                    // Add the file details to the list
                    items.Add(item);
                }
                // Handle specific exceptions and continue with the next file
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (PathTooLongException)
                {
                    continue;
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
            }

            // Return the list of item details
            return items;
        }
    }

    /// <summary>
    /// Class to hold details about a drive.
    /// </summary>
    public class DriveDetails
    {
        /// <summary>
        /// Gets or sets the name of the drive.
        /// </summary>
        public string Name { get; set; } = string.Empty; // Drive name

        /// <summary>
        /// Gets or sets the type of drive (e.g., Fixed, Removable).
        /// </summary>
        public string DriveType { get; set; } = string.Empty; // Type of drive

        /// <summary>
        /// Gets or sets the total size of the drive.
        /// </summary>
        public long TotalSize { get; set; } // Total size of the drive

        /// <summary>
        /// Gets or sets the available free space on the drive.
        /// </summary>
        public long AvailableFreeSpace { get; set; } // Available free space on the drive

        /// <summary>
        /// Gets or sets a value indicating whether the drive is ready for use.
        /// </summary>
        public bool IsReady { get; set; } // Whether the drive is ready for use
    }

    /// <summary>
    /// Class to hold details about an item (file or directory).
    /// </summary>
    public class ItemDetails
    {
        /// <summary>
        /// Gets or sets the name of the item.
        /// </summary>
        public string Name { get; set; } = string.Empty; // Item name

        /// <summary>
        /// Gets or sets the full path of the item.
        /// </summary>
        public string Path { get; set; } = string.Empty; // Full path of the item

        /// <summary>
        /// Gets or sets a value indicating whether the item is a directory.
        /// </summary>
        public bool IsDirectory { get; set; } // Whether the item is a directory

        /// <summary>
        /// Gets or sets the size of the item (nullable for directories).
        /// </summary>
        public long? Size { get; set; } // Size of the item (nullable for directories)
    }
}