using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.Infrastructure;

/// <summary>
/// Phase E (R-5.7): runs liveness probes against the runtime-owned dependency
/// values surfaced on the Infrastructure tab. The caller supplies explicit
/// <see cref="InfrastructureProbeRequestItemDto"/> items so the service does
/// not need to re-enumerate the dependency catalog or <c>IConfiguration</c>.
/// <para>URL items receive an HTTP HEAD probe (with a <c>GET Range: bytes=0-0</c>
/// accommodation for servers that reject HEAD with 405). Path items receive
/// an existence + writability probe. Individual probe failures are captured
/// per-row and MUST NOT surface as thrown exceptions.</para>
/// </summary>
public interface IInfrastructureProbeService
{
    /// <summary>
    /// Probes each requested item, returning a batch result keyed by each
    /// item's caller-supplied <c>Id</c>.
    /// </summary>
    /// <remarks>
    /// MUST NOT throw on individual item failures. Transport-level errors,
    /// malformed URLs, missing paths, etc. are surfaced per-row in
    /// <see cref="InfrastructureProbeResultDto.Error"/>.
    /// </remarks>
    Task<InfrastructureProbeBatchDto> ProbeAsync(
        IReadOnlyCollection<InfrastructureProbeRequestItemDto> items,
        CancellationToken cancellationToken = default);
}
