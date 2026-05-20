using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public sealed record LocalModelOnboardingResult(
    string? OperationId,
    AddModelOperationDto AddOperation)
{
    public AddModelResponse ToResponse() => new(OperationId, AddOperation);
}
