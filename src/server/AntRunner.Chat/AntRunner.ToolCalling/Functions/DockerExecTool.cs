using System.IO;
using System.Text;
using System.Threading;
using CliWrap;

namespace AntRunner.ToolCalling.Functions
{
    public enum ScriptType
    {
        Bash,
        PowerShell,
        Python
    }

    public class ScriptExecutionResult
    {
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        
        /// <summary>
        /// CWD-relative paths of files created by this tool execution.
        /// Used by ThreadRun to inject a system message so the assistant is aware of new files.
        /// </summary>
        public List<string>? NewFiles { get; set; }
        
        /// <summary>
        /// CWD-relative paths of files modified by this tool execution.
        /// Used by ThreadRun to inject a system message so the assistant is aware of modified files.
        /// </summary>
        public List<string>? ModifiedFiles { get; set; }
    }

    public class DockerScriptService
    {
        public static async Task<ScriptExecutionResult> ExecuteDockerScript(string script, string containerName, ScriptType scriptType)
        {
            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();
            Exception? executionException = null;
           

            if (string.IsNullOrWhiteSpace(script))
            {
                stdErrBuffer.AppendLine("Script cannot be null or empty.");
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            script = script.Replace("\r\n", "\n").Replace("\r", "\n");

            var guid = Guid.NewGuid().ToString();
            var workingDirectory = $"/app/shared/{guid}";
            var scriptFilename = $"{guid}_{scriptType switch
            {
                ScriptType.Bash => "script.sh",
                ScriptType.PowerShell => "script.ps1",
                ScriptType.Python => "script.py",
                _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
            }}";
            var hostScriptFilePath = Path.Combine(Path.GetTempPath(), scriptFilename);
            var containerScriptFilePath = $"{workingDirectory}/{scriptFilename}";

            try
            {
                // Create working directory
                await CreateWorkingDirectory(containerName, workingDirectory, stdOutBuffer, stdErrBuffer);

                // Create script file on host
                await CreateScriptFileOnHost(hostScriptFilePath, script);

                // Copy script file to container
                await CopyScriptFileToContainer(containerName, hostScriptFilePath, containerScriptFilePath, stdOutBuffer, stdErrBuffer);

                // Make script file executable if it's a Bash script
                if (scriptType == ScriptType.Bash)
                {
                    await MakeScriptFileExecutable(containerName, containerScriptFilePath, workingDirectory, stdOutBuffer, stdErrBuffer);
                }

                // Execute the script
                await ExecuteScript(containerName, containerScriptFilePath, scriptType, workingDirectory, stdOutBuffer, stdErrBuffer);

                var filesList = await GetNewFileUrls(containerName, workingDirectory, guid, scriptFilename);
                if (!string.IsNullOrWhiteSpace(filesList))
                {
                    stdOutBuffer.Append("\nNew Files\n\n---\n");
                    stdOutBuffer.Append(filesList);
                }
            }
            catch (Exception ex)
            {
                stdErrBuffer.AppendLine($"Error running script in Docker container {containerName}:");
                stdErrBuffer.AppendLine(ex.ToString());
                executionException = ex;
            }
            finally
            {
                // Clean up temporary script file on host
                if (File.Exists(hostScriptFilePath))
                {
                    File.Delete(hostScriptFilePath);
                }
            }

            if (stdOutBuffer.Length == 0 && stdErrBuffer.Length == 0)
            {
                stdOutBuffer.AppendLine("The operation completed successfully");
            }

            var scriptExecutionResult = new ScriptExecutionResult
            {
                StandardOutput = stdOutBuffer.ToString(),
                StandardError = stdErrBuffer.ToString()
            };

            var basePath = Environment.GetEnvironmentVariable("DOCKER_EXEC_LOG_PATH") ?? "/tmp/scripts";
            var scriptFolder = Path.Combine(basePath, scriptType.ToString(), executionException == null ? "success" : "failure");
            Directory.CreateDirectory(scriptFolder);
            var resultFilename = $"{Guid.NewGuid()}.json";
            var resultPath = Path.Combine(scriptFolder, resultFilename);

            var logObject = new
            {
                Script = script,
                ContainerName = containerName,
                ScriptType = scriptType,
                Result = scriptExecutionResult
            };

            var jsonResult = JsonSerializer.Serialize(logObject, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resultPath, jsonResult);

            return scriptExecutionResult;
        }

        private static async Task ExecuteDockerCommand(string containerName, string command, string workingDirectory, StringBuilder stdOutBuffer, StringBuilder stdErrBuffer)
        {
            try
            {
                var result = await Cli.Wrap("docker")
                    .WithArguments($"exec -i -w {workingDirectory} {containerName} {command}")
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(line => stdOutBuffer.AppendLine(line)))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(line => stdErrBuffer.AppendLine(line)))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync();
            }
            catch (Exception ex)
            {
                stdErrBuffer.AppendLine($"Error executing Docker command '{command}' in container '{containerName}':");
                stdErrBuffer.AppendLine(ex.ToString());
                throw;
            }
        }

        private static async Task CreateWorkingDirectory(string containerName, string workingDirectory, StringBuilder stdOutBuffer, StringBuilder stdErrBuffer)
        {
            await ExecuteDockerCommand(containerName, $"mkdir -p {workingDirectory}", "/", stdOutBuffer, stdErrBuffer);
        }

        private static async Task CreateScriptFileOnHost(string scriptFilePath, string scriptContent)
        {
            await File.WriteAllTextAsync(scriptFilePath, scriptContent);
        }

        private static async Task CopyScriptFileToContainer(string containerName, string hostScriptFilePath, string containerScriptFilePath, StringBuilder stdOutBuffer, StringBuilder stdErrBuffer)
        {
            try
            {
                var result = await Cli.Wrap("docker")
                    .WithArguments($"cp {hostScriptFilePath} {containerName}:{containerScriptFilePath}")
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(line => stdOutBuffer.AppendLine(line)))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(line => stdErrBuffer.AppendLine(line)))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync();
            }
            catch (Exception ex)
            {
                stdErrBuffer.AppendLine($"Error copying script file to Docker container '{containerName}':");
                stdErrBuffer.AppendLine(ex.ToString());
                throw;
            }
        }

        private static async Task MakeScriptFileExecutable(string containerName, string scriptFilePath, string workingDirectory, StringBuilder stdOutBuffer, StringBuilder stdErrBuffer)
        {
            await ExecuteDockerCommand(containerName, $"chmod +x {scriptFilePath}", workingDirectory, stdOutBuffer, stdErrBuffer);
        }

        private static async Task ExecuteScript(string containerName, string scriptFilePath, ScriptType scriptType, string workingDirectory, StringBuilder stdOutBuffer, StringBuilder stdErrBuffer)
        {
            string command = scriptType switch
            {
                ScriptType.Bash => $"bash -c \"{scriptFilePath}\"",
                ScriptType.PowerShell => $"pwsh -c \"{scriptFilePath}\"",
                ScriptType.Python => $"python \"{scriptFilePath}\"",
                _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
            };

            await ExecuteDockerCommand(containerName, command, workingDirectory, stdOutBuffer, stdErrBuffer);
        }

        private static async Task<string> GetNewFileUrls(string containerName, string workingDirectory, string sessionFolder, string scriptFilename)
        {
            if (string.IsNullOrWhiteSpace(containerName))
            {
                throw new ArgumentException("Container name must not be null or empty", nameof(containerName));
            }
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentException("Working directory must not be null or empty", nameof(workingDirectory));
            }
            if (string.IsNullOrWhiteSpace(sessionFolder))
            {
                throw new ArgumentException("Session folder must not be null or empty", nameof(sessionFolder));
            }

            var stdOutBuffer = new StringBuilder();
            var formattedFilesList = new StringBuilder();
            var hostUrl = Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL") ?? "https://localhost";
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                var result = await Cli.Wrap("docker")
                    .WithArguments($"exec -i {containerName} ls -1 {workingDirectory}")
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(cancellationTokenSource.Token).ConfigureAwait(false);

                var files = stdOutBuffer.ToString().Trim().Split('\n');
                foreach (var file in files)
                {
                    if (!string.IsNullOrWhiteSpace(file) && !file.Equals(scriptFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        var fileUri = new Uri(new Uri(hostUrl), $"{sessionFolder}/{file}");
                        formattedFilesList.AppendLine(fileUri.ToString());
                    }
                }

                return formattedFilesList.ToString().Trim();
            }
            catch (OperationCanceledException)
            {
                stdOutBuffer.Clear();
                stdOutBuffer.AppendLine($"Operation timed out while listing files in Docker container {containerName}:");
                stdOutBuffer.AppendLine($"Working Directory: {workingDirectory}");
                stdOutBuffer.AppendLine($"Session Folder: {sessionFolder}");
                stdOutBuffer.AppendLine($"Host URL: {hostUrl}");
            }
            catch (Exception ex)
            {
                stdOutBuffer.Clear();
                stdOutBuffer.AppendLine($"Error listing files in Docker container {containerName}:");
                stdOutBuffer.AppendLine($"Working Directory: {workingDirectory}");
                stdOutBuffer.AppendLine($"Session Folder: {sessionFolder}");
                stdOutBuffer.AppendLine($"Host URL: {hostUrl}");
                stdOutBuffer.AppendLine($"Exception: {ex.Message}");
                stdOutBuffer.AppendLine(ex.ToString());
            }

            return stdOutBuffer.ToString().Trim();
        }
    }
}