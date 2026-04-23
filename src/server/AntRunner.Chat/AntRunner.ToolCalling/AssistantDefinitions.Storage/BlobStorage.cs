using Azure.Storage.Blobs;
using System.IO;
using System.Linq;

namespace AntRunner.ToolCalling.AssistantDefinitions.Storage
{
    public class BlobStorage
    {
        private static readonly BlobContainerClient? BlobContainerClient;

        static BlobStorage()
        {
            var connectionString = Environment.GetEnvironmentVariable("ASSISTANTS_STORAGE_CONNECTION");
            var containerName = Environment.GetEnvironmentVariable("ASSISTANTS_STORAGE_CONTAINER");

            if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(containerName))
            {
                return;
            }

            var blobServiceClient = new BlobServiceClient(connectionString);
            BlobContainerClient =  blobServiceClient.GetBlobContainerClient(containerName);
        }

        private static async Task<string?> DownloadBlobAsStringAsync(BlobClient blobClient)
        {
            try
            {
                if (await blobClient.ExistsAsync())
                {
                    var response = await blobClient.DownloadAsync();
                    using var streamReader = new StreamReader(response.Value.Content);
                    return await streamReader.ReadToEndAsync();
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new ApplicationException("An error occurred while accessing the blob storage.", ex);
            }
        }

        private static async Task<byte[]?> DownloadBlobAsByteArrayAsync(BlobClient blobClient)
        {
            try
            {
                if (await blobClient.ExistsAsync())
                {
                    var response = await blobClient.DownloadAsync();
                    using var memoryStream = new MemoryStream();
                    await response.Value.Content.CopyToAsync(memoryStream);
                    return memoryStream.ToArray();
                }
                return null;
            }
            catch (Exception ex)
            {
                throw new ApplicationException("An error occurred while accessing the blob storage.", ex);
            }
        }

        private static async Task<List<string>?> ListBlobsAsync(string prefix)
        {
            try
            {
                if (BlobContainerClient == null) return null;

                var blobs = BlobContainerClient.GetBlobsAsync(prefix: prefix);
                var fileList = new List<string>();

                await foreach (var blobItem in blobs)
                {
                    fileList.Add(blobItem.Name);
                }

                return fileList.Count > 0 ? fileList : null;
            }
            catch (Exception ex)
            {
                throw new ApplicationException("An error occurred while accessing the blob storage.", ex);
            }
        }

        public static async Task<string?> GetManifest(string assistantName)
        {
            var blobClient = BlobContainerClient?.GetBlobClient($"{assistantName}/manifest.json");
            return blobClient != null ? await DownloadBlobAsStringAsync(blobClient) : null;
        }

        public static async Task<string?> GetInstructions(string assistantName)
        {
            var blobClient = BlobContainerClient?.GetBlobClient($"{assistantName}/instructions.md");
            return blobClient != null ? await DownloadBlobAsStringAsync(blobClient) : null;
        }

        public static async Task<byte[]?> GetFile(string filePath)
        {
            var blobClient = BlobContainerClient?.GetBlobClient(filePath);
            return blobClient != null ? await DownloadBlobAsByteArrayAsync(blobClient) : null;
        }

        public static Task<List<string>?> GetFilesInVectorStoreFolder(string assistantName, string vectorStoreName)
        {
            return ListBlobsAsync($"{assistantName}/VectorStores/{vectorStoreName}/");
        }

        public static Task<List<string>?> GetFilesInCodeInterpreterFolder(string assistantName)
        {
            return ListBlobsAsync($"{assistantName}/CodeInterpreter");
        }

        public static async Task<List<string>?> GetFilesInOpenApiFolder(string assistantName)
        {
            return (await ListBlobsAsync($"{assistantName}/OpenAPI"))?.Where(o => !o.Contains("auth.json", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static async Task<string?> GetActionAuth(string assistantName)
        {
            var blobClient = BlobContainerClient?.GetBlobClient($"{assistantName}/OpenAPI/auth.json");
            return blobClient != null ? await DownloadBlobAsStringAsync(blobClient) : null;
        }
    }
}