using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GuideAntsApi.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GuideAntsApi.Services.SandboxWireApi;

public static class SandboxWireJwtClaimTypes
{
    public const string ExecutionId = "sandbox_execution_id";
    public const string ProjectId = "sandbox_project_id";
    public const string NotebookId = "sandbox_notebook_id";
    public const string OwnerAssistantId = "sandbox_owner_assistant_id";
    public const string TargetAssistantId = "sandbox_target_assistant_id";
    public const string TargetAssistantName = "sandbox_target_assistant_name";
    public const string AllowedEndpoints = "sandbox_allowed_endpoints";
    public const string AttributionConversationId = "sandbox_attribution_conversation_id";
    public const string AncestorAssistantIds = "sandbox_ancestor_assistant_ids";
    public const string DailyLimitUsd = "sandbox_daily_limit_usd";
    public const string MonthlyLimitUsd = "sandbox_monthly_limit_usd";
}

public sealed record IssuedSandboxWireJwt(string Token, DateTime ExpiresAtUtc, Guid ExecutionId);

public interface ISandboxWireJwtService
{
    IssuedSandboxWireJwt Mint(SandboxWireExecutionGrant grant);

    bool TryValidate(string bearerToken, out SandboxWireExecutionGrant? grant, out string? failureReason);
}

public sealed class SandboxWireJwtService : ISandboxWireJwtService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SandboxWireApiOptions _options;
    private readonly TokenValidationParameters _validationParameters;

    public SandboxWireJwtService(IOptions<SandboxWireApiOptions> options)
    {
        _options = options.Value;
        ValidateOptions(_options);
        _validationParameters = CreateValidationParameters(_options);
    }

    public IssuedSandboxWireJwt Mint(SandboxWireExecutionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.Lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(grant), "Lifetime must be positive.");
        }

        if (grant.TargetAssistantId == grant.OwnerAssistantId)
        {
            throw new InvalidOperationException("Target assistant cannot equal owner assistant.");
        }

        if (grant.AncestorAssistantIds.Contains(grant.TargetAssistantId))
        {
            throw new InvalidOperationException("Circular sandbox wire reference detected.");
        }

        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc + grant.Lifetime;
        var claims = new List<Claim>
        {
            new(SandboxWireJwtClaimTypes.ExecutionId, grant.ExecutionId.ToString("D")),
            new(SandboxWireJwtClaimTypes.ProjectId, grant.ProjectId.ToString("D")),
            new(SandboxWireJwtClaimTypes.NotebookId, grant.NotebookId.ToString("D")),
            new(SandboxWireJwtClaimTypes.OwnerAssistantId, grant.OwnerAssistantId.ToString("D")),
            new(SandboxWireJwtClaimTypes.TargetAssistantId, grant.TargetAssistantId.ToString("D")),
            new(SandboxWireJwtClaimTypes.TargetAssistantName, grant.TargetAssistantName),
            new(SandboxWireJwtClaimTypes.AllowedEndpoints, JsonSerializer.Serialize(grant.AllowedEndpoints, JsonOptions)),
        };

        if (grant.AttributionConversationId.HasValue)
        {
            claims.Add(new Claim(
                SandboxWireJwtClaimTypes.AttributionConversationId,
                grant.AttributionConversationId.Value.ToString("D")));
        }

        if (grant.AncestorAssistantIds.Count > 0)
        {
            claims.Add(new Claim(
                SandboxWireJwtClaimTypes.AncestorAssistantIds,
                JsonSerializer.Serialize(grant.AncestorAssistantIds.Select(id => id.ToString("D")).ToArray(), JsonOptions)));
        }

        if (grant.DailyLimitUsd.HasValue)
        {
            claims.Add(new Claim(
                SandboxWireJwtClaimTypes.DailyLimitUsd,
                grant.DailyLimitUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (grant.MonthlyLimitUsd.HasValue)
        {
            claims.Add(new Claim(
                SandboxWireJwtClaimTypes.MonthlyLimitUsd,
                grant.MonthlyLimitUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresUtc,
            signingCredentials: credentials);

        return new IssuedSandboxWireJwt(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresUtc,
            grant.ExecutionId);
    }

    public bool TryValidate(string bearerToken, out SandboxWireExecutionGrant? grant, out string? failureReason)
    {
        grant = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            failureReason = "Missing bearer token.";
            return false;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(bearerToken, _validationParameters, out _);
            grant = ParseGrant(principal.Claims);
            if (grant.TargetAssistantId == grant.OwnerAssistantId)
            {
                failureReason = "Token targets owner assistant.";
                grant = null;
                return false;
            }

            if (grant.AncestorAssistantIds.Contains(grant.TargetAssistantId))
            {
                failureReason = "Circular sandbox wire reference detected.";
                grant = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private static SandboxWireExecutionGrant ParseGrant(IEnumerable<Claim> claims)
    {
        var map = claims.GroupBy(c => c.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        string Require(string type) =>
            map.TryGetValue(type, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new SecurityTokenException($"Missing claim '{type}'.");

        var allowedEndpoints = JsonSerializer.Deserialize<string[]>(
            Require(SandboxWireJwtClaimTypes.AllowedEndpoints),
            JsonOptions) ?? [];

        Guid? attributionConversationId = null;
        if (map.TryGetValue(SandboxWireJwtClaimTypes.AttributionConversationId, out var attributionRaw)
            && Guid.TryParse(attributionRaw, out var parsedAttribution))
        {
            attributionConversationId = parsedAttribution;
        }

        IReadOnlyList<Guid> ancestors = [];
        if (map.TryGetValue(SandboxWireJwtClaimTypes.AncestorAssistantIds, out var ancestorsRaw)
            && !string.IsNullOrWhiteSpace(ancestorsRaw))
        {
            var ancestorStrings = JsonSerializer.Deserialize<string[]>(ancestorsRaw, JsonOptions) ?? [];
            ancestors = ancestorStrings
                .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToArray();
        }

        decimal? dailyLimitUsd = null;
        if (map.TryGetValue(SandboxWireJwtClaimTypes.DailyLimitUsd, out var dailyRaw)
            && decimal.TryParse(dailyRaw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedDaily))
        {
            dailyLimitUsd = parsedDaily;
        }

        decimal? monthlyLimitUsd = null;
        if (map.TryGetValue(SandboxWireJwtClaimTypes.MonthlyLimitUsd, out var monthlyRaw)
            && decimal.TryParse(monthlyRaw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedMonthly))
        {
            monthlyLimitUsd = parsedMonthly;
        }

        return new SandboxWireExecutionGrant(
            ExecutionId: Guid.Parse(Require(SandboxWireJwtClaimTypes.ExecutionId)),
            ProjectId: Guid.Parse(Require(SandboxWireJwtClaimTypes.ProjectId)),
            NotebookId: Guid.Parse(Require(SandboxWireJwtClaimTypes.NotebookId)),
            OwnerAssistantId: Guid.Parse(Require(SandboxWireJwtClaimTypes.OwnerAssistantId)),
            TargetAssistantId: Guid.Parse(Require(SandboxWireJwtClaimTypes.TargetAssistantId)),
            TargetAssistantName: Require(SandboxWireJwtClaimTypes.TargetAssistantName),
            AllowedEndpoints: allowedEndpoints,
            AttributionConversationId: attributionConversationId,
            AncestorAssistantIds: ancestors,
            Lifetime: TimeSpan.Zero,
            DailyLimitUsd: dailyLimitUsd,
            MonthlyLimitUsd: monthlyLimitUsd);
    }

    internal static void ValidateOptions(SandboxWireApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            throw new InvalidOperationException("SandboxWireApi:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("SandboxWireApi:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("SandboxWireApi:SigningKey must be at least 32 characters.");
        }

        if (options.DefaultLifetimeMinutes <= 0)
        {
            throw new InvalidOperationException("SandboxWireApi:DefaultLifetimeMinutes must be greater than 0.");
        }
    }

    private static TokenValidationParameters CreateValidationParameters(SandboxWireApiOptions options)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
    }
}
