using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ScriptExecutionAgent;

/// <summary>
/// Authenticates agent calls via the <c>X-Script-Agent-Token</c> header. When token enforcement
/// is disabled every request is treated as an authenticated agent caller.
/// </summary>
internal sealed class AgentTokenAuthenticationHandler : ScriptTokenAuthenticationHandler
{
    public const string SchemeName = "ScriptAgentToken";

    public AgentTokenAuthenticationHandler(
        IOptionsMonitor<AgentTokenOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override string HeaderName => "X-Script-Agent-Token";

    protected override string TokenLabel => "agent";

    protected override bool AllowsMissingToken => true;
}
