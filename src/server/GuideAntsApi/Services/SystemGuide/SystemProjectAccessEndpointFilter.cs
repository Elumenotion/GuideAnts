namespace GuideAntsApi.Services.SystemGuide;

public sealed class SystemProjectAccessEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!TryGetProjectId(context, out var projectId))
        {
            return await next(context);
        }

        var guard = context.HttpContext.RequestServices.GetRequiredService<ISystemProjectAccessGuard>();
        var blocked = await guard.EnsureReadAccessAsync(projectId, context.HttpContext.RequestAborted);
        if (blocked != null)
        {
            return blocked;
        }

        return await next(context);
    }

    private static bool TryGetProjectId(EndpointFilterInvocationContext context, out Guid projectId)
    {
        projectId = default;

        if (!context.HttpContext.Request.RouteValues.TryGetValue("projectId", out var raw)
            || raw is not string projectIdText
            || !Guid.TryParse(projectIdText, out projectId))
        {
            return false;
        }

        return true;
    }
}

public static class SystemProjectAccessEndpointExtensions
{
    public static RouteGroupBuilder WithSystemProjectAccessGuard(this RouteGroupBuilder group)
    {
        return group.AddEndpointFilter<SystemProjectAccessEndpointFilter>();
    }
}
