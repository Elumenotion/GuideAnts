using System.Net.Http;

namespace GuideAntsApi.Services.SystemGuide;

public interface ISystemGuideSandboxAdminProxy
{
    Task<IResult> ForwardAsync(
        HttpMethod method,
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        string? body,
        string? contentType,
        CancellationToken cancellationToken);
}
