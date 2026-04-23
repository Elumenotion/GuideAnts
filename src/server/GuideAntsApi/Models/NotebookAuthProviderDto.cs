namespace GuideAntsApi.Models;

public sealed record NotebookAuthProviderDto(
    string Id,
    NotebookAuthType AuthType,
    string? ClientId,
    List<string>? Scopes,
    string? Tenant,
    string? ValueTemplate,
    string? HeaderName,
    NotebookUserConfigPolicy UserConfigPolicy = NotebookUserConfigPolicy.None);


