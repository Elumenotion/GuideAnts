using System.Security.Cryptography;
using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Auth;

// ── Public contract (pinned names — Task 4 endpoints depend on them) ──

public sealed record CliAuthSessionCreation(string SessionId, string DeviceSecret, DateTime ExpiresAt);

public enum CliAuthApproveOutcome { Approved, NotFound, Expired }

public enum CliAuthDenyOutcome { Denied, NotFound, Expired }

public enum CliAuthTokenStatus { Pending, Issued, InvalidSecret, NotFound, Expired, Consumed, Denied }

public sealed record CliAuthTokenResult(CliAuthTokenStatus Status, string? Token, DateTime? ExpiresAt);

/// <summary>
/// Drives the CLI device-code authorization flow.
/// The CLI creates a session (receiving a one-time device secret), the browser approves it,
/// and the CLI exchanges the secret for a short-lived JWT.
/// </summary>
public interface ICliAuthService
{
    /// <summary>
    /// Creates a new CLI auth session with a cryptographic session ID and device secret.
    /// The device secret is returned exactly once in plaintext; only its hash is persisted.
    /// </summary>
    Task<CliAuthSessionCreation> CreateSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending CLI auth session on behalf of the given user.
    /// Returns <see cref="CliAuthApproveOutcome.Approved"/> on success,
    /// <see cref="CliAuthApproveOutcome.NotFound"/> if the session does not exist,
    /// or <see cref="CliAuthApproveOutcome.Expired"/> if the session has expired or been consumed.
    /// </summary>
    Task<CliAuthApproveOutcome> ApproveAsync(string sessionId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges the device secret for a short-lived JWT after the session has been approved.
    /// The device secret is verified before any approval-state branching so that a caller
    /// who knows only the session ID cannot learn the approval state or obtain a token.
    /// The token is single-use: once issued, the session is marked Consumed.
    /// </summary>
    Task<CliAuthTokenResult> IssueTokenAsync(string sessionId, string deviceSecret, CancellationToken cancellationToken = default);

    Task<CliAuthDenyOutcome> DenyAsync(string sessionId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class CliAuthService : ICliAuthService
{
    private const int SessionLifetimeMinutes = 5;
    private const int TokenLifetimeMinutes = 10;

    private readonly ApplicationDbContext _db;
    private readonly IJwtTokenService _jwtTokenService;

    public CliAuthService(ApplicationDbContext db, IJwtTokenService jwtTokenService)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _jwtTokenService = jwtTokenService ?? throw new ArgumentNullException(nameof(jwtTokenService));
    }

    public async Task<CliAuthSessionCreation> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        await CleanupExpiredSessionsAsync(cancellationToken);

        var sessionId = CreateBase64Url(32);
        var deviceSecret = CreateBase64Url(32);
        var deviceSecretHash = HashSecret(deviceSecret);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(SessionLifetimeMinutes);

        var session = new CliAuthSession
        {
            SessionId = sessionId,
            DeviceSecretHash = deviceSecretHash,
            Status = CliAuthSessionStatus.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        _db.CliAuthSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return new CliAuthSessionCreation(sessionId, deviceSecret, expiresAt);
    }

    public async Task<CliAuthApproveOutcome> ApproveAsync(string sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        await CleanupExpiredSessionsAsync(cancellationToken);

        var session = await _db.CliAuthSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);

        if (session == null)
            return CliAuthApproveOutcome.NotFound;

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            _db.CliAuthSessions.Remove(session);
            await _db.SaveChangesAsync(cancellationToken);
            return CliAuthApproveOutcome.Expired;
        }

        if (session.Status == CliAuthSessionStatus.Consumed)
            return CliAuthApproveOutcome.Expired;

        if (session.Status == CliAuthSessionStatus.Pending)
        {
            session.Status = CliAuthSessionStatus.Approved;
            session.UserId = userId;
            await _db.SaveChangesAsync(cancellationToken);
            return CliAuthApproveOutcome.Approved;
        }

        // Already Approved — idempotent: ensure UserId is set
        if (session.Status == CliAuthSessionStatus.Approved)
        {
            session.UserId = userId;
            await _db.SaveChangesAsync(cancellationToken);
            return CliAuthApproveOutcome.Approved;
        }

        return CliAuthApproveOutcome.NotFound;
    }

    public async Task<CliAuthTokenResult> IssueTokenAsync(string sessionId, string deviceSecret, CancellationToken cancellationToken = default)
    {
        await CleanupExpiredSessionsAsync(cancellationToken);

        var session = await _db.CliAuthSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);

        if (session == null)
            return new CliAuthTokenResult(CliAuthTokenStatus.NotFound, null, null);

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            _db.CliAuthSessions.Remove(session);
            await _db.SaveChangesAsync(cancellationToken);
            return new CliAuthTokenResult(CliAuthTokenStatus.Expired, null, null);
        }

        // SECURITY-CRITICAL: Verify the device secret BEFORE branching on status.
        // A caller who knows only the sessionId cannot learn the approval state or get a token.
        var providedHash = HashSecret(deviceSecret);
        var storedHashBytes = Encoding.UTF8.GetBytes(session.DeviceSecretHash);
        var providedHashBytes = Encoding.UTF8.GetBytes(providedHash);

        if (!CryptographicOperations.FixedTimeEquals(storedHashBytes, providedHashBytes))
            return new CliAuthTokenResult(CliAuthTokenStatus.InvalidSecret, null, null);

        // Now branch on status (secret is verified)
        if (session.Status == CliAuthSessionStatus.Pending)
            return new CliAuthTokenResult(CliAuthTokenStatus.Pending, null, null);

        if (session.Status == CliAuthSessionStatus.Consumed)
            return new CliAuthTokenResult(CliAuthTokenStatus.Consumed, null, null);

        if (session.Status == CliAuthSessionStatus.Denied)
            return new CliAuthTokenResult(CliAuthTokenStatus.Denied, null, null);

        // Status == Approved — issue the token
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);

        var role = user == null
            ? null
            : await _db.UserRoles
                .AsNoTracking()
                .Where(ur => ur.UserId == user.Id)
                .Select(ur => (Role?)ur.Role)
                .SingleOrDefaultAsync(cancellationToken);

        if (user == null || role == null)
        {
            _db.CliAuthSessions.Remove(session);
            await _db.SaveChangesAsync(cancellationToken);
            return new CliAuthTokenResult(CliAuthTokenStatus.NotFound, null, null);
        }

        var issued = _jwtTokenService.IssueToken(user, role.Value, TimeSpan.FromMinutes(TokenLifetimeMinutes));

        session.Status = CliAuthSessionStatus.Consumed;
        await _db.SaveChangesAsync(cancellationToken);

        return new CliAuthTokenResult(CliAuthTokenStatus.Issued, issued.Token, issued.ExpiresAtUtc);
    }

    public async Task<CliAuthDenyOutcome> DenyAsync(string sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        await CleanupExpiredSessionsAsync(cancellationToken);

        var session = await _db.CliAuthSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);

        if (session == null)
            return CliAuthDenyOutcome.NotFound;

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            _db.CliAuthSessions.Remove(session);
            await _db.SaveChangesAsync(cancellationToken);
            return CliAuthDenyOutcome.Expired;
        }

        if (session.Status == CliAuthSessionStatus.Consumed)
            return CliAuthDenyOutcome.Expired;

        if (session.Status == CliAuthSessionStatus.Pending || session.Status == CliAuthSessionStatus.Denied)
        {
            session.Status = CliAuthSessionStatus.Denied;
            session.UserId = userId;
            await _db.SaveChangesAsync(cancellationToken);
            return CliAuthDenyOutcome.Denied;
        }

        return CliAuthDenyOutcome.NotFound;
    }

    // ── Private helpers ──

    private async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiredRows = await _db.CliAuthSessions
            .Where(s => s.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (expiredRows.Count == 0)
            return;

        _db.CliAuthSessions.RemoveRange(expiredRows);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string HashSecret(string secret)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(secret));
        return ToBase64Url(bytes);
    }

    private static string CreateBase64Url(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
