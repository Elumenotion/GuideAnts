using System.ComponentModel.DataAnnotations;
using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat
{

    /// <summary>
    /// Represents the options for running an assistant.
    /// </summary>
    public class ChatRunOptions
    {
        /// <summary>
        /// Gets or sets the name of the assistant.
        /// </summary>
        [Required]
        public required string AssistantName { get; set; }

        /// <summary>
        /// Gets or sets the instructions for the assistant.
        /// </summary>
        public string Instructions { get; set; } = string.Empty;

        /// <summary>
        /// Passed in from the starter. The web api gets the Authorization header value if it exists, otherwise null.
        /// </summary>
        public string? oAuthUserAccessToken { get; set; }

        /// <summary>
        /// Optional external OAuth tokens for accessing external APIs (e.g., Microsoft Graph, GitHub).
        /// Key is the provider ID (e.g., "graph.microsoft.com"), value is the formatted token (e.g., "Bearer token").
        /// </summary>
        public Dictionary<string, string>? ExternalAuthTokens { get; set; }

        /// <summary>
        /// The optional name of an assistant to use for evaluation of the run.
        /// Note that the named assistant must be created ahead of time.
        /// </summary>
        public string? Evaluator { get; set; }

        /// <summary>
        /// Optional method to run after the assistant completes.
        /// Must be a static method in a loaded assembly as follows:
        /// public static async Task&lt;ThreadRunOutput&gt; PostProcessor(ThreadRunOutput threadRunOutput)
        /// 
        /// Example value: WebSearchFunctions.SearchTool.PostProcessor
        /// </summary>
        public string? PostProcessor { get; set; }
        
        /// <summary>
        /// The deployment Id of the model, e.g. 03-mini
        /// </summary>
        public string? DeploymentId { get; set; }

        /// <summary>
        /// Resolved execution policy for this run. When present, execution must consume this
        /// policy as the single source-of-truth for model parameters.
        /// </summary>
        public ResolvedExecutionPolicy? ExecutionPolicy { get; set; }
    }
}
