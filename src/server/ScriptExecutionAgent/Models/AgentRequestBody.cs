using System.Text;
using System.Text.Json;

namespace ScriptExecutionAgent;

/// <summary>
/// Reads request bodies with the agent's original tolerant settings. JSON bodies are
/// deserialized streaming, exactly as the pre-refactor code did, so malformed JSON keeps
/// producing the plain-text 400 messages the agent has always returned instead of an
/// ASP.NET problem+json response. JSON body size is bounded by Kestrel's default 30 MB
/// request body limit (no route in this agent needs more; the largest legal JSON payload
/// is an /execute request with the max 1 MB script plus the maximum environment, 128
/// values x 64 KB, ~9.4 MB). Text bodies are read with a per-route streaming cap that
/// throws <see cref="RequestBodyTooLargeException"/> the moment the limit is crossed -
/// authoritative against a faked Content-Length header, which Kestrel's own limit trusts.
/// </summary>
internal static class AgentRequestBody
{
    /// <summary>
    /// Cap for the /admin/install-scripts text body: the legal maximum document is 32 scripts
    /// x 256 KB of script text plus JSON overhead, ~8.4 MB; 16 MB leaves headroom.
    /// </summary>
    public const long InstallScriptsBodyMaxBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Cap for the /admin/apply body: the request only ever carries optional target
    /// selection, so 64 KB is far beyond any real payload.
    /// </summary>
    public const long ApplyBodyMaxBytes = 64 * 1024;

    /// <summary>
    /// Cap for the /admin/requirements text body: requirements lists are line-oriented
    /// package references; 100 KB is far beyond any real payload.
    /// </summary>
    public const long RequirementsBodyMaxBytes = 100 * 1024;

    /// <summary>
    /// Cap for the /admin/apt-packages text body: one package name per line; 64 KB is far
    /// beyond any real payload.
    /// </summary>
    public const long AptPackagesBodyMaxBytes = 64 * 1024;

    public static async Task<T?> ReadJsonAsync<T>(HttpContext context) where T : class
    {
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        return await JsonSerializer.DeserializeAsync<T>(
            context.Request.Body,
            AgentJson.RequestRead,
            context.RequestAborted);
    }

    public static async Task<string> ReadTextAsync(HttpContext context, long maxBytes)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var builder = new StringBuilder(64 * 1024);
        var buffer = new byte[64 * 1024];
        var total = 0L;
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0)
            {
                return builder.ToString();
            }

            total += read;
            if (total > maxBytes)
            {
                throw new RequestBodyTooLargeException(maxBytes);
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}
