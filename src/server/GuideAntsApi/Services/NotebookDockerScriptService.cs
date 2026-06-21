using AntRunner.ToolCalling.Functions;
using AntRunner.ToolCalling;
using System.Text;
using System.Text.Json;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services
{
    public interface INotebookDockerScriptService
    {
        Task<ScriptExecutionResult> ExecuteDockerScriptAsync(
            string script,
            string containerName,
            ScriptType scriptType,
            InvocationContext? context = null);
    }

    public class NotebookDockerScriptService : INotebookDockerScriptService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NotebookDockerScriptService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly IOptionsMonitor<SettingsSecretsOptions> _settingsSecretsOptions;

        public NotebookDockerScriptService(
            IHttpClientFactory httpClientFactory,
            ILogger<NotebookDockerScriptService> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            IOptionsMonitor<SettingsSecretsOptions> settingsSecretsOptions)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _settingsSecretsOptions = settingsSecretsOptions;
        }

        /// <summary>
        /// Executes a script in the notebook's directory using Docker.
        /// This method is called by the tool calling system with injected notebook context.
        /// </summary>
        /// <param name="script">The script content to execute</param>
        /// <param name="containerName">The target Docker container (matches OpenAPI)</param>
        /// <param name="scriptType">The type of script (Python, Bash, etc.)</param>
        /// <param name="context">The invocation context containing project, notebook, user, and conversation IDs</param>
        /// <returns>Script execution result</returns>
        public async Task<ScriptExecutionResult> ExecuteDockerScriptAsync(string script, string containerName, ScriptType scriptType, InvocationContext? context = null)
        {
            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();
            Exception? executionException = null;

            _logger.LogInformation("ExecuteDockerScript invoked. ScriptType={ScriptType}, Project={ProjectId}, Notebook={NotebookId}, Container={Container}, Conversation={ConversationId}", scriptType, context?.ProjectId, context?.NotebookId, LogValueSanitizer.Sanitize(containerName), context?.ConversationId);

            if (string.IsNullOrWhiteSpace(script))
            {
                var errorMsg = "Script cannot be null or empty.";
                _logger.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            // Validate project and notebook IDs
            if (context is null || context.ProjectId == Guid.Empty || context.NotebookId == Guid.Empty)
            {
                var errorMsg = "Project ID and Notebook ID are required.";
                _logger.LogError(errorMsg + " ProjectId='{ProjectId}', NotebookId='{NotebookId}'", context?.ProjectId, context?.NotebookId);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            script = script.Replace("\r\n", "\n").Replace("\r", "\n");

            var notebookDirectory = NotebookPathHelper.GetWorkingDirectory(context!);
            Directory.CreateDirectory(notebookDirectory);

            // containerName is required; throw if missing.
            if (string.IsNullOrWhiteSpace(containerName))
            {
                var msg = "Container name is required and cannot be null or whitespace.";
                _logger.LogError(msg);
                throw new ArgumentException(msg, nameof(containerName));
            }
            var scriptExecutionBaseUrl = ResolveScriptExecutionBaseUrl(containerName);

            _logger.LogInformation("Will execute script via agent. Container={Container}, BaseUrl={BaseUrl}, WorkingDir={Dir}", LogValueSanitizer.Sanitize(containerName), LogValueSanitizer.Sanitize(scriptExecutionBaseUrl), LogValueSanitizer.Sanitize(notebookDirectory));

            // Track detected file changes for ScriptExecutionResult
            List<string>? detectedNewFiles = null;
            List<string>? detectedModifiedFiles = null;

            try
            {
                // Execute script via HTTP
                var guideScopeId = await ResolveGuideScopeIdAsync(context);
                var executionEnvironment = await ResolveExecutionEnvironmentAsync(context, guideScopeId);
                var result = await ExecuteScriptViaHttp(
                    script,
                    scriptType,
                    notebookDirectory,
                    scriptExecutionBaseUrl,
                    context.ProjectId.ToString(),
                    context.NotebookId.ToString(),
                    guideScopeId.ToString("D"),
                    executionEnvironment);

                _logger.LogInformation("Script execution via agent returned {HasResult}. StdOutLength={OutLen}, StdErrLength={ErrLen}", result != null, result?.StandardOutput?.Length ?? 0, result?.StandardError?.Length ?? 0);

                if (result != null)
                {
                    _logger.LogDebug("Agent STDOUT: {StdOut}", LogValueSanitizer.Sanitize(result.StandardOutput));
                    _logger.LogDebug("Agent STDERR: {StdErr}", LogValueSanitizer.Sanitize(result.StandardError));
                    stdOutBuffer.Append(result.StandardOutput);
                    stdErrBuffer.Append(result.StandardError);

                    // For scripts, detect new files BEFORE syncing DB
                    // because scripts can create multiple files in various locations
                    if (_serviceProvider != null)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                        var storagePath = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");

                        // Detect new/modified files (returns CWD-relative paths)
                        (detectedNewFiles, detectedModifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(
                            _serviceProvider,
                            storagePath,
                            context!,
                            _logger);
                    }

                    // Reconcile DB with disk before returning so new files are queryable. Extraction/indexing enqueued inside sync.
                    if (_serviceProvider != null)
                    {
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var syncService = scope.ServiceProvider.GetRequiredService<INotebookFileSyncService>();
                            await syncService.SyncNotebookAsync(context.NotebookId);
                            _logger.LogInformation("Database synchronized for notebook {NotebookId} after script execution", context.NotebookId);
                        }
                        catch (Exception syncEx)
                        {
                            _logger.LogError(syncEx, "Failed to sync database after script execution");
                            stdErrBuffer.AppendLine($"Warning: Script executed but failed to sync database: {syncEx.Message}");
                        }
                    }
                }
                else
                {
                    stdErrBuffer.AppendLine($"Error: Script execution agent failed to respond or returned an error.");
                    _logger.LogWarning("Script execution via HTTP failed - no result returned from {ScriptExecutionBaseUrl}", LogValueSanitizer.Sanitize(scriptExecutionBaseUrl));
                    _logger.LogWarning("StdErr so far: {Err}", LogValueSanitizer.Sanitize(stdErrBuffer.ToString()));
                }
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error running script via HTTP: {ex.Message}";
                _logger.LogError(ex, "Script execution failed. Container={Container}, BaseUrl={ScriptExecutionBaseUrl}, WorkingDir={NotebookDirectory}", LogValueSanitizer.Sanitize(containerName), LogValueSanitizer.Sanitize(scriptExecutionBaseUrl), LogValueSanitizer.Sanitize(notebookDirectory));
                stdErrBuffer.AppendLine(errorMsg);
                executionException = ex;
            }
            finally
            {
                // No host script file to clean up in HTTP mode
            }

            if (stdOutBuffer.Length == 0 && stdErrBuffer.Length == 0)
            {
                stdOutBuffer.AppendLine("The operation completed successfully");
            }

            // Clean up output formatting - normalize line endings and remove excessive whitespace
            var cleanedOutput = stdOutBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var cleanedError = stdErrBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var scriptExecutionResult = new ScriptExecutionResult
            {
                StandardOutput = cleanedOutput,
                StandardError = cleanedError,
                NewFiles = detectedNewFiles,
                ModifiedFiles = detectedModifiedFiles
            };

            // Emit final buffers to logs (truncated to 4k chars)
            if (!string.IsNullOrWhiteSpace(cleanedOutput))
            {
                _logger.LogInformation("Final StdOut (truncated): {Out}", LogValueSanitizer.Sanitize(cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput));
            }
            if (!string.IsNullOrWhiteSpace(cleanedError))
            {
                _logger.LogWarning("Final StdErr (truncated): {Err}", LogValueSanitizer.Sanitize(cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError));
            }

            // Log the execution result
            var basePath = Environment.GetEnvironmentVariable("DOCKER_EXEC_LOG_PATH") ?? "/tmp/scripts";
            var scriptFolder = Path.Combine(basePath, scriptType.ToString(), executionException == null ? "success" : "failure");
            Directory.CreateDirectory(scriptFolder);
            var resultFilename = $"{Guid.NewGuid()}.json";
            var resultPath = Path.Combine(scriptFolder, resultFilename);

            var logObject = new
            {
                Script = script,
                ProjectId = context?.ProjectId.ToString(),
                NotebookId = context?.NotebookId.ToString(),
                ContainerName = containerName,
                ScriptType = scriptType,
                NotebookDirectory = notebookDirectory,
                Result = scriptExecutionResult
            };

            var jsonResult = JsonSerializer.Serialize(logObject, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resultPath, jsonResult);

            return scriptExecutionResult;
        }


        /// <summary>
        /// Executes a script via HTTP communication with the script execution agent
        /// </summary>
        private async Task<ScriptExecutionResult?> ExecuteScriptViaHttp(
            string script,
            ScriptType scriptType,
            string workingDirectory,
            string scriptExecutionBaseUrl,
            string projectId,
            string? notebookId = null,
            string? guideId = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId) || !Guid.TryParse(projectId, out var parsedProjectId) || parsedProjectId == Guid.Empty)
                {
                    throw new InvalidOperationException("ProjectId must be a non-empty GUID for script execution.");
                }

                if (string.IsNullOrWhiteSpace(notebookId) || !Guid.TryParse(notebookId, out var parsedNotebookId) || parsedNotebookId == Guid.Empty)
                {
                    throw new InvalidOperationException("NotebookId must be a non-empty GUID for script execution.");
                }

                if (string.IsNullOrWhiteSpace(guideId) || !Guid.TryParse(guideId, out var parsedGuideId) || parsedGuideId == Guid.Empty)
                {
                    throw new InvalidOperationException("GuideId must be a non-empty GUID for script execution.");
                }

                var request = new
                {
                    Script = script,
                    ScriptType = scriptType,
                    WorkingDirectory = workingDirectory,
                    ProjectId = projectId,
                    NotebookId = notebookId,
                    GuideId = guideId,
                    Environment = environment
                };

                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                AttachAgentAuthHeader(httpClient);

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation("Executing script via HTTP at base URL {ScriptExecutionBaseUrl}", LogValueSanitizer.Sanitize(scriptExecutionBaseUrl));

                var executeUri = BuildEndpointUri(scriptExecutionBaseUrl, "execute");

                _logger.LogInformation("Sending HTTP POST to {ExecuteUrl} with script length {ScriptLength} and type {ScriptType}", LogValueSanitizer.Sanitize(executeUri.ToString()), script.Length, scriptType);

                var response = await httpClient.PostAsync(executeUri, content);

                _logger.LogInformation("Agent response status {StatusCode} from {ExecuteUrl}", response.StatusCode, LogValueSanitizer.Sanitize(executeUri.ToString()));

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<ScriptExecutionResult>(responseContent, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (result != null)
                    {
                        _logger.LogInformation("HTTP script execution successful");
                        return result;
                    }
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("HTTP script execution failed with status {StatusCode}: {Error}", response.StatusCode, LogValueSanitizer.Sanitize(errorContent));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP script execution failed");
                return new ScriptExecutionResult
                {
                    StandardError = BuildScriptAgentTransportFailureMessage(scriptExecutionBaseUrl, ex)
                };
            }
        }

        private async Task<Guid> ResolveGuideScopeIdAsync(InvocationContext context)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
                if (db is not null)
                {
                    var guideId = await db.Notebooks
                        .AsNoTracking()
                        .Where(n => n.Id == context.NotebookId && n.ProjectId == context.ProjectId)
                        .Select(n => n.GuideId ?? n.NotebookTemplateId)
                        .FirstOrDefaultAsync();

                    if (guideId.HasValue && guideId.Value != Guid.Empty)
                    {
                        return guideId.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to resolve notebook guide for script execution scope. ProjectId={ProjectId}, NotebookId={NotebookId}",
                    context.ProjectId,
                    context.NotebookId);
            }

            if (context.AssistantId.HasValue && context.AssistantId.Value != Guid.Empty)
            {
                _logger.LogWarning(
                    "Guide scope fallback: using InvocationContext.AssistantId because notebook guide is unavailable. ProjectId={ProjectId}, NotebookId={NotebookId}, AssistantId={AssistantId}",
                    context.ProjectId,
                    context.NotebookId,
                    context.AssistantId.Value);
                return context.AssistantId.Value;
            }

            throw new InvalidOperationException(
                $"Unable to resolve GuideId scope for ProjectId={context.ProjectId} NotebookId={context.NotebookId}.");
        }

        private async Task<IReadOnlyDictionary<string, string>?> ResolveExecutionEnvironmentAsync(
            InvocationContext context,
            Guid guideScopeId)
        {
            // Credential persistence is intentionally owned by the API tier. The script
            // agent receives only per-run environment values and never reads a credential store.
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
            if (db is null)
            {
                return null;
            }

            var guideAndCrewIds = await db.GuideMembers
                .AsNoTracking()
                .Where(member => member.GuideId == guideScopeId)
                .OrderBy(member => member.DisplayOrder ?? int.MaxValue)
                .ThenBy(member => member.Assistant.Name)
                .Select(member => member.AssistantId)
                .ToListAsync();

            guideAndCrewIds.Insert(0, guideScopeId);

            var environmentManifests = await db.ProjectAssistantEnvironments
                .AsNoTracking()
                .Where(environment => environment.ProjectId == context.ProjectId
                    && guideAndCrewIds.Contains(environment.AssistantId))
                .Select(environment => new
                {
                    environment.AssistantId,
                    environment.EnvironmentConfigJson
                })
                .ToListAsync();

            var manifestByAssistantId = environmentManifests
                .ToDictionary(environment => environment.AssistantId, environment => environment.EnvironmentConfigJson);
            var orderedManifests = guideAndCrewIds
                .Select(assistantId => manifestByAssistantId.TryGetValue(assistantId, out var manifest) ? manifest : null)
                .Where(manifest => !string.IsNullOrWhiteSpace(manifest))
                .ToArray();

            var environment = EnvironmentVariableConfigSerializer.DeserializeForExecution(
                _settingsSecretsOptions.CurrentValue,
                orderedManifests);

            return environment.Count == 0 ? null : environment;
        }

        internal static string BuildScriptAgentTransportFailureMessage(string scriptExecutionBaseUrl, Exception ex)
        {
            var builder = new StringBuilder()
                .AppendLine($"Script execution agent request failed: {ex.Message}")
                .AppendLine()
                .AppendLine($"Agent URL: {scriptExecutionBaseUrl}")
                .AppendLine()
                .AppendLine("If this is the local Docker compose runtime, the guideants-ai container may have failed before its application logs were available.")
                .AppendLine("Run these host-side checks to see Docker's stored startup error:")
                .AppendLine("  docker ps -a --filter name=guideants-ai")
                .AppendLine("  docker inspect guideants-ai --format '{{.State.Status}} exit={{.State.ExitCode}} error={{.State.Error}}'");

            return builder.ToString().TrimEnd();
        }

        private void AttachAgentAuthHeader(HttpClient client)
        {
            var token = _configuration["ScriptExecution:AgentToken"];
            ApplyAgentAuthHeader(client, token);
        }

        internal static void ApplyAgentAuthHeader(HttpClient client, string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("ScriptExecution:AgentToken is required for API -> script-agent authentication.");
            }

            client.DefaultRequestHeaders.Remove("X-Script-Agent-Token");
            client.DefaultRequestHeaders.Add("X-Script-Agent-Token", token.Trim());
        }

        internal static Uri BuildEndpointUri(string baseUrl, string relativePath)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
            {
                throw new InvalidOperationException($"Script execution base URL '{baseUrl}' is not a valid absolute URI.");
            }

            if (!string.Equals(parsedBaseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsedBaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Script execution base URL '{baseUrl}' must use http or https.");
            }

            var normalizedBaseUrl = parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/";
            var normalizedRelativePath = relativePath.TrimStart('/');
            return new Uri(new Uri(normalizedBaseUrl), normalizedRelativePath);
        }

        private string ResolveScriptExecutionBaseUrl(string containerName)
        {
            var configKey = ServiceRoutingContracts.ContainerBaseUrlKey(containerName);
            var configuredContainerUrl = _configuration[configKey];
            var envSuffix = Environment.GetEnvironmentVariable("CONTAINER_APP_ENV_DNS_SUFFIX");
            return ResolveScriptExecutionBaseUrl(containerName, configuredContainerUrl, envSuffix, configKey);
        }

        internal static string ResolveScriptExecutionBaseUrl(
            string containerName,
            string? configuredContainerUrl,
            string? envSuffix,
            string? configKey = null)
        {
            if (!string.IsNullOrWhiteSpace(envSuffix))
            {
                if (string.Equals(containerName, ServiceRoutingContracts.GuideantsAiContainerName, StringComparison.OrdinalIgnoreCase))
                {
                    return $"http://{containerName}.internal.{envSuffix}{ServiceRoutingContracts.SandboxPath}";
                }

                return $"http://{containerName}.internal.{envSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(configuredContainerUrl))
            {
                return configuredContainerUrl.Trim();
            }

            var resolvedConfigKey = string.IsNullOrWhiteSpace(configKey)
                ? ServiceRoutingContracts.ContainerBaseUrlKey(containerName)
                : configKey;
            throw new InvalidOperationException(
                $"{resolvedConfigKey} is required for script execution routing.");
        }

        // Static service provider for tool calling system compatibility
        private static IServiceProvider? _staticServiceProvider;
        
        /// <summary>
        /// Initializes the static service provider for tool calling system compatibility
        /// </summary>
        public static void InitializeServiceProvider(IServiceProvider serviceProvider)
        {
            _staticServiceProvider = serviceProvider;
        }

        /// <summary>
        /// Static wrapper method for tool calling system compatibility
        /// </summary>
        public static async Task<ScriptExecutionResult> ExecuteDockerScript(
            string script,
            string containerName,
            ScriptType scriptType,
            InvocationContext? context = null)
        {
            if (_staticServiceProvider == null)
            {
                throw new InvalidOperationException("Service provider not initialized. Call InitializeServiceProvider during application startup.");
            }

            using var scope = _staticServiceProvider.CreateScope();
            var scriptService = scope.ServiceProvider.GetRequiredService<INotebookDockerScriptService>();
            
            return await scriptService.ExecuteDockerScriptAsync(script, containerName, scriptType, context);
        }
    }
}
