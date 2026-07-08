using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.Models.Guides;

public class SandboxWireApiConfigDto
{
    public bool Enabled { get; set; }

    public Guid? TargetAssistantId { get; set; }

    public PublishedWireApiEndpointFlagsDto? EndpointFlags { get; set; }

    public Dictionary<string, string>? AliasMap { get; set; }

    public PublishedWireApiMaxRequestSizesDto? MaxRequestSizes { get; set; }

    public decimal? DailyLimitUsd { get; set; }

    public decimal? MonthlyLimitUsd { get; set; }
}
