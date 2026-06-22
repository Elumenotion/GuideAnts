using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace GuideAntsApi.Services.Auth;

public interface IAppJwtValidator
{
    /// <summary>
    /// Validates JWT signature, issuer, audience, and lifetime using the same parameters
    /// as <c>RequireApprovedUser</c> / JwtBearer middleware.
    /// </summary>
    AppJwtValidationResult Validate(string token);
}

public sealed class AppJwtValidationResult
{
    public bool IsValid { get; init; }
    public ClaimsPrincipal? Principal { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class AppJwtValidator : IAppJwtValidator
{
    private readonly TokenValidationParameters _parameters;
    private readonly JwtSecurityTokenHandler _handler = new();

    public AppJwtValidator(Microsoft.Extensions.Options.IOptions<GuideAntsApi.Options.JwtOptions> options)
    {
        _parameters = JwtTokenValidation.CreateParameters(options.Value);
    }

    public AppJwtValidationResult Validate(string token)
    {
        try
        {
            var principal = _handler.ValidateToken(token, _parameters, out _);
            return new AppJwtValidationResult { IsValid = true, Principal = principal };
        }
        catch (SecurityTokenExpiredException)
        {
            return new AppJwtValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_token",
                ErrorMessage = "Token has expired"
            };
        }
        catch (Exception)
        {
            return new AppJwtValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_token",
                ErrorMessage = "Token validation failed"
            };
        }
    }
}
