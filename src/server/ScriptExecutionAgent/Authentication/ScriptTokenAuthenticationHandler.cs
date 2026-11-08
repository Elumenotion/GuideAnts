using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using GuideAnts.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// Base handler for the agent's shared-secret header schemes. Challenges answer 401 with a
/// plain-text body, matching the response the agent wrote before authorization moved to policies.
/// </summary>
internal abstract class ScriptTokenAuthenticationHandler : AuthenticationHandler<AgentTokenOptions>
{
    private readonly ILogger _logger;

    protected ScriptTokenAuthenticationHandler(
        IOptionsMonitor<AgentTokenOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        // Preserve the log category the inline guards wrote under before endpoints left Program.cs.
        _logger = logger.CreateLogger("Program");
    }

    protected abstract string HeaderName { get; }

    protected abstract string TokenLabel { get; }

    protected virtual bool AllowsMissingToken => false;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (AllowsMissingToken && !Options.RequireToken)
        {
            return Task.FromResult(Success());
        }

        var suppliedToken = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrWhiteSpace(Options.Token))
        {
            _logger.LogWarning(
                $"SECURITY: {TokenLabel} token missing. path={{Path}}",
                LogValueSanitizer.Sanitize(Request.Path.Value));
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(suppliedToken, Options.Token, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                $"SECURITY: {TokenLabel} token mismatch. path={{Path}}",
                LogValueSanitizer.Sanitize(Request.Path.Value));
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(Success());
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "text/plain";
        return Response.WriteAsync("Unauthorized", Context.RequestAborted);
    }

    private AuthenticateResult Success() => AuthenticateResult.Success(
        new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(Scheme.Name)),
            Scheme.Name));
}
