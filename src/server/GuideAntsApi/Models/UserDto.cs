namespace GuideAntsApi.Models;

public record UserDto(
    Guid Id,
    string Name,
    string Email
);

public record UpdateCurrentUserRequest(
    string Name,
    string Email
);
