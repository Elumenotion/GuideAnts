using System.Data;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Auth;

public enum AdminUserListStatusFilter
{
    All = 0,
    Pending = 1,
    Active = 2,
    Inactive = 3
}

public enum AdminUserOperationError
{
    NotFound = 0,
    InvalidInput = 1,
    Guarded = 2,
    InvalidState = 3
}

public sealed record AdminUserOperationFailure(AdminUserOperationError Error, string Message);

public sealed record AdminUserOperationResult<T>(T? Value, AdminUserOperationFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static AdminUserOperationResult<T> Success(T value) => new(value, null);

    public static AdminUserOperationResult<T> Fail(AdminUserOperationError error, string message) =>
        new(default, new AdminUserOperationFailure(error, message));
}

public sealed record AdminUserSummary(
    Guid UserId,
    string Name,
    string Email,
    Role Role,
    bool IsActive,
    bool MustChangePassword,
    Guid? ApprovedByUserId,
    DateTime? ApprovedAt,
    DateTime? LastLoginAt,
    DateTime Created);

public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserSummary>> ListUsersAsync(
        Role? roleFilter,
        AdminUserListStatusFilter statusFilter,
        CancellationToken cancellationToken = default);

    Task<AdminUserOperationResult<AdminUserSummary>> ApproveUserAsync(
        Guid targetUserId,
        string roleValue,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default);

    Task<AdminUserOperationResult<AdminUserSummary>> ChangeRoleAsync(
        Guid targetUserId,
        string roleValue,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default);

    Task<AdminUserOperationResult<AdminUserSummary>> DeactivateUserAsync(
        Guid targetUserId,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default);

    Task<AdminUserOperationResult<AdminUserSummary>> ReactivateUserAsync(
        Guid targetUserId,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default);

    Task<AdminUserOperationResult<AdminUserSummary>> SetPasswordAsync(
        Guid targetUserId,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed class AdminUserService : IAdminUserService
{
    private const string AdminMutationLockSql =
        "EXEC sp_getapplock @Resource = N'GuideAnts.Auth.AdminUsers', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;";

    private readonly ApplicationDbContext _db;
    private readonly IUserPasswordHasher _passwordHasher;

    public AdminUserService(ApplicationDbContext db, IUserPasswordHasher passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<IReadOnlyList<AdminUserSummary>> ListUsersAsync(
        Role? roleFilter,
        AdminUserListStatusFilter statusFilter,
        CancellationToken cancellationToken = default)
    {
        var query = _db.UserRoles
            .AsNoTracking()
            .Select(userRole => new
            {
                userRole.UserId,
                userRole.Role,
                userRole.User.Name,
                userRole.User.Email,
                userRole.User.MustChangePassword,
                userRole.User.ApprovedByUserId,
                userRole.User.ApprovedAt,
                userRole.User.LastLoginAt,
                userRole.User.Created
            });

        if (roleFilter.HasValue)
        {
            query = query.Where(candidate => candidate.Role == roleFilter.Value);
        }

        query = statusFilter switch
        {
            AdminUserListStatusFilter.Pending => query.Where(candidate => candidate.Role == Role.Pending),
            AdminUserListStatusFilter.Active => query.Where(candidate =>
                candidate.Role != Role.Pending && candidate.ApprovedAt != null),
            AdminUserListStatusFilter.Inactive => query.Where(candidate =>
                candidate.Role != Role.Pending && candidate.ApprovedAt == null),
            _ => query
        };

        var users = await query
            .OrderBy(candidate => candidate.Name)
            .ThenBy(candidate => candidate.Email)
            .Select(candidate => new AdminUserSummary(
                candidate.UserId,
                candidate.Name,
                candidate.Email,
                candidate.Role,
                IsActive(candidate.Role, candidate.ApprovedAt),
                candidate.MustChangePassword,
                candidate.ApprovedByUserId,
                candidate.ApprovedAt,
                candidate.LastLoginAt,
                candidate.Created))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return users;
    }

    public Task<AdminUserOperationResult<AdminUserSummary>> ApproveUserAsync(
        Guid targetUserId,
        string roleValue,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async nowUtc =>
        {
            var requestedRoleResult = ParseAssignableRole(roleValue);
            if (!requestedRoleResult.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    requestedRoleResult.Failure!.Error,
                    requestedRoleResult.Failure.Message);
            }

            var target = await LoadTargetUserAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    target.Failure!.Error,
                    target.Failure.Message);
            }

            target.Value!.UserRole.Role = requestedRoleResult.Value;
            target.Value.UserRole.AssignedAt = nowUtc;
            target.Value.UserRole.AssignedByUserId = actingAdminUserId;
            target.Value.User.ApprovedAt = nowUtc;
            target.Value.User.ApprovedByUserId = actingAdminUserId;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AdminUserOperationResult<AdminUserSummary>.Success(ToSummary(target.Value.User, target.Value.UserRole));
        }, cancellationToken);

    public Task<AdminUserOperationResult<AdminUserSummary>> ChangeRoleAsync(
        Guid targetUserId,
        string roleValue,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async nowUtc =>
        {
            var requestedRoleResult = ParseAssignableRole(roleValue);
            if (!requestedRoleResult.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    requestedRoleResult.Failure!.Error,
                    requestedRoleResult.Failure.Message);
            }

            var target = await LoadTargetUserAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    target.Failure!.Error,
                    target.Failure.Message);
            }

            var requestedRole = requestedRoleResult.Value;
            if (target.Value!.UserRole.Role == Role.Pending)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    AdminUserOperationError.InvalidState,
                    "Pending users must be approved before changing role.");
            }

            if (target.Value.UserRole.Role == Role.Admin && requestedRole != Role.Admin)
            {
                if (target.Value.User.Id == actingAdminUserId)
                {
                    return AdminUserOperationResult<AdminUserSummary>.Fail(
                        AdminUserOperationError.Guarded,
                        "Admins cannot demote themselves.");
                }

                if (target.Value.User.ApprovedAt != null)
                {
                    var activeAdminCount = await CountActiveAdminsAsync(cancellationToken).ConfigureAwait(false);
                    if (activeAdminCount <= 1)
                    {
                        return AdminUserOperationResult<AdminUserSummary>.Fail(
                            AdminUserOperationError.Guarded,
                            "Cannot demote the last active admin.");
                    }
                }
            }

            target.Value.UserRole.Role = requestedRole;
            target.Value.UserRole.AssignedAt = nowUtc;
            target.Value.UserRole.AssignedByUserId = actingAdminUserId;
            // No SecurityStamp rotation: a role change is an authority change, not a revocation.
            // RBAC resolves the live role per request (see OnTokenValidated), so the new role
            // takes effect on the user's next request without forcing them to sign out and back in.

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AdminUserOperationResult<AdminUserSummary>.Success(ToSummary(target.Value.User, target.Value.UserRole));
        }, cancellationToken);

    public Task<AdminUserOperationResult<AdminUserSummary>> DeactivateUserAsync(
        Guid targetUserId,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async _ =>
        {
            var target = await LoadTargetUserAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    target.Failure!.Error,
                    target.Failure.Message);
            }

            if (target.Value!.User.Id == actingAdminUserId)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    AdminUserOperationError.Guarded,
                    "Admins cannot deactivate themselves.");
            }

            var isTargetActiveAdmin = target.Value.UserRole.Role == Role.Admin && target.Value.User.ApprovedAt != null;
            if (isTargetActiveAdmin)
            {
                var activeAdminCount = await CountActiveAdminsAsync(cancellationToken).ConfigureAwait(false);
                if (activeAdminCount <= 1)
                {
                    return AdminUserOperationResult<AdminUserSummary>.Fail(
                        AdminUserOperationError.Guarded,
                        "Cannot deactivate the last active admin.");
                }
            }

            target.Value.User.ApprovedAt = null;
            target.Value.User.ApprovedByUserId = null;
            target.Value.User.SecurityStamp = Guid.NewGuid();

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AdminUserOperationResult<AdminUserSummary>.Success(ToSummary(target.Value.User, target.Value.UserRole));
        }, cancellationToken);

    public Task<AdminUserOperationResult<AdminUserSummary>> ReactivateUserAsync(
        Guid targetUserId,
        Guid actingAdminUserId,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async nowUtc =>
        {
            var target = await LoadTargetUserAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    target.Failure!.Error,
                    target.Failure.Message);
            }

            if (target.Value!.UserRole.Role == Role.Pending)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    AdminUserOperationError.InvalidState,
                    "Pending users must be approved before reactivation.");
            }

            target.Value.User.ApprovedAt = nowUtc;
            target.Value.User.ApprovedByUserId = actingAdminUserId;
            target.Value.User.SecurityStamp = Guid.NewGuid();

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AdminUserOperationResult<AdminUserSummary>.Success(ToSummary(target.Value.User, target.Value.UserRole));
        }, cancellationToken);

    public Task<AdminUserOperationResult<AdminUserSummary>> SetPasswordAsync(
        Guid targetUserId,
        string password,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(async _ =>
        {
            var normalizedPassword = password?.Trim() ?? string.Empty;
            if (normalizedPassword.Length < 8)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    AdminUserOperationError.InvalidInput,
                    "Password must be at least 8 characters.");
            }

            var target = await LoadTargetUserAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            if (!target.IsSuccess)
            {
                return AdminUserOperationResult<AdminUserSummary>.Fail(
                    target.Failure!.Error,
                    target.Failure.Message);
            }

            target.Value!.User.PasswordHash = _passwordHasher.HashPassword(target.Value.User, normalizedPassword);
            target.Value.User.MustChangePassword = true;
            target.Value.User.SecurityStamp = Guid.NewGuid();

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AdminUserOperationResult<AdminUserSummary>.Success(ToSummary(target.Value.User, target.Value.UserRole));
        }, cancellationToken);

    private async Task<AdminUserOperationResult<T>> ExecuteMutationAsync<T>(
        Func<DateTime, Task<AdminUserOperationResult<T>>> mutation,
        CancellationToken cancellationToken)
    {
        var executionStrategy = _db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            await _db.Database.ExecuteSqlRawAsync(AdminMutationLockSql, cancellationToken).ConfigureAwait(false);

            var result = await mutation(DateTime.UtcNow).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private async Task<AdminUserOperationResult<(User User, UserRole UserRole)>> LoadTargetUserAsync(
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        var targetUser = await _db.Users
            .SingleOrDefaultAsync(user => user.Id == targetUserId, cancellationToken)
            .ConfigureAwait(false);
        if (targetUser == null)
        {
            return AdminUserOperationResult<(User User, UserRole UserRole)>.Fail(
                AdminUserOperationError.NotFound,
                "User not found.");
        }

        var targetRole = await _db.UserRoles
            .SingleOrDefaultAsync(userRole => userRole.UserId == targetUserId, cancellationToken)
            .ConfigureAwait(false);
        if (targetRole == null)
        {
            return AdminUserOperationResult<(User User, UserRole UserRole)>.Fail(
                AdminUserOperationError.InvalidState,
                "User role row is missing.");
        }

        return AdminUserOperationResult<(User User, UserRole UserRole)>.Success((targetUser, targetRole));
    }

    private async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) =>
        await _db.UserRoles
            .Where(userRole => userRole.Role == Role.Admin && userRole.User.ApprovedAt != null)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

    private static AdminUserOperationResult<Role> ParseAssignableRole(string roleValue)
    {
        if (!Enum.TryParse<Role>(roleValue, ignoreCase: true, out var parsedRole) || !IsAssignableRole(parsedRole))
        {
            return AdminUserOperationResult<Role>.Fail(
                AdminUserOperationError.InvalidInput,
                "Role must be one of: Reader, Contributor, Admin.");
        }

        return AdminUserOperationResult<Role>.Success(parsedRole);
    }

    private static bool IsAssignableRole(Role role) =>
        role is Role.Reader or Role.Contributor or Role.Admin;

    private static bool IsActive(Role role, DateTime? approvedAt) =>
        role != Role.Pending && approvedAt != null;

    private static AdminUserSummary ToSummary(User user, UserRole userRole) =>
        new(
            user.Id,
            user.Name,
            user.Email,
            userRole.Role,
            IsActive(userRole.Role, user.ApprovedAt),
            user.MustChangePassword,
            user.ApprovedByUserId,
            user.ApprovedAt,
            user.LastLoginAt,
            user.Created);
}
