using System.IO;
using System.Linq;
using System.Reflection;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    public class FileStorage
    {
        private static string GetBasePath()
        {
            var basePath = Environment.GetEnvironmentVariable("ASSISTANTS_BASE_FOLDER_PATH");
            return Path.GetDirectoryName(basePath ?? Assembly.GetExecutingAssembly().Location)!;
        }

        private static string GetFolderPath(string assistantName, string folderName)
        {
            return Path.Combine(GetBasePath(), "AssistantDefinitions", $"{assistantName}/{folderName}");
        }

        private static string GetFilePath(string assistantName, string fileName)
        {
            return Path.Combine(GetBasePath(), "AssistantDefinitions", $"{assistantName}/{fileName}");
        }

        private static async Task<string?> ReadFileAsync(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    using var streamReader = new StreamReader(filePath);
                    return await streamReader.ReadToEndAsync();
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"An error occurred while accessing the file at {filePath}.", ex);
            }
        }

        private static List<string>? EnumerateFilesInFolder(string folderPath)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    return Directory.EnumerateFiles(folderPath).ToList();
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"An error occurred while accessing the folder at {folderPath}.", ex);
            }
        }

        public static Task<string?> GetManifest(string assistantName)
        {
            var filePath = GetFilePath(assistantName, "manifest.json");
            return ReadFileAsync(filePath);
        }

        public static Task<string?> GetInstructions(string assistantName)
        {
            var filePath = GetFilePath(assistantName, "instructions.md");
            return ReadFileAsync(filePath);
        }

        public static Task<string?> GetActionAuth(string assistantName)
        {
            var filePath = GetFilePath(assistantName, "OpenAPI/auth.json");
            return ReadFileAsync(filePath);
        }

        public static async Task<string?> GetContextOptions(string assistantName)
        {
            // Look for any contextOptions.json under HostExtensions subfolders
            var hostExtensionsRoot = GetFolderPath(assistantName, "HostExtensions");
            if (!Directory.Exists(hostExtensionsRoot)) return null;

            var filePath = Directory.EnumerateFiles(hostExtensionsRoot, "contextOptions.json", SearchOption.AllDirectories)
                                      .FirstOrDefault();
            return filePath == null ? null : await ReadFileAsync(filePath);
        }

        public static async Task<byte[]?> GetFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                return await File.ReadAllBytesAsync(filePath);
            }
            return null;
        }

        public static List<string>? GetFilesInOpenApiFolder(string assistantName)
        {
            var folderPath = GetFolderPath(assistantName, "OpenAPI");
            return EnumerateFilesInFolder(folderPath)?.Where(o => !o.Contains("auth.json", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static List<string>? GetFilesInCodeInterpreterFolder(string assistantName)
        {
            var folderPath = GetFolderPath(assistantName, "CodeInterpreter");
            return EnumerateFilesInFolder(folderPath);
        }

        public static List<string>? GetFilesInVectorStoreFolder(string assistantName, string vectorStoreName)
        {
            var folderPath = GetFolderPath(assistantName, $"VectorStores/{vectorStoreName}");
            return EnumerateFilesInFolder(folderPath);
        }
    }
}