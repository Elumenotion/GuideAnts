using System.Security.Claims;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Auth;

/// <summary>
/// Synchronizes the authenticated principal's role claim with the live role from the system
/// of record. The cookie/JWT proves identity only; role is mutable authority that must be
/// evaluated per request, so the (possibly stale) role claim minted at login is replaced with
/// the current role before authorization policies run. This is what lets an admin's role
/// changes — including approving a pending user — take effect on the next request instead of
/// requiring the affected user to sign out and back in.
/// </summary>
public static class AuthRoleClaims
{
    public static void ApplyLiveRole(ClaimsIdentity identity, Role liveRole)
    {
        foreach (var staleRoleClaim in identity.FindAll(ClaimTypes.Role).ToList())
        {
            identity.RemoveClaim(staleRoleClaim);
        }

        identity.AddClaim(new Claim(ClaimTypes.Role, liveRole.ToString()));
    }
}
