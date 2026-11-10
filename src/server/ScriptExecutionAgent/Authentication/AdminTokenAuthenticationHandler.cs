using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ScriptExecutionAgent;

/// <summary>
/// Authenticates sandbox admin calls via the <c>X-Script-Agent-Admin-Token</c> header.
/// </summary>
internal sealed class AdminTokenAuthenticationHandler : ScriptTokenAuthenticationHandler
{
    public const string SchemeName = "ScriptAgentAdminToken";

    public AdminTokenAuthenticationHandler(
        IOptionsMonitor<AgentTokenOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override string HeaderName => "X-Script-Agent-Admin-Token";

    protected override string TokenLabel => "admin";
}
