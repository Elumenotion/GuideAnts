namespace GuideAntsApi.Models;

public sealed record EnvironmentVariableDto(
    string Name,
    string? Value,
    bool IsSecret);
