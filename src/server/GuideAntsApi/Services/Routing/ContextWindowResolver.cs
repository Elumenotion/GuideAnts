namespace GuideAntsApi.Services.Routing;

/// <inheritdoc />
public sealed class ContextWindowResolver : IContextWindowResolver
{
    private readonly IChatTargetResolver _targets;
    private readonly ILearnedContextWindowCache _learned;

    public ContextWindowResolver(
        IChatTargetResolver targets,
        ILearnedContextWindowCache learned)
    {
        _targets = targets;
        _learned = learned;
    }

    public ContextWindowInfo Resolve(string modelId, int? liveRuntimeContextSize)
    {
        // An unknown model is not an error here: a missing window is a safe, explicit state.
        ChatTarget? target;
        try
        {
            target = _targets.Resolve(modelId);
        }
        catch (RoutingException)
        {
            target = null;
        }

        // The output cap is only ever known by the catalog — a runtime window says nothing
        // about how much the model is willing to generate.
        var maxOutput = target?.MaxOutputTokens;

        if (liveRuntimeContextSize is > 0)
        {
            return new ContextWindowInfo(
                liveRuntimeContextSize, maxOutput, ContextWindowSource.LiveRuntime);
        }

        if (target?.ContextWindowTokens is > 0)
        {
            return new ContextWindowInfo(
                target.ContextWindowTokens, maxOutput, ContextWindowSource.Catalog);
        }

        var learned = _learned.Get(modelId);
        if (learned is > 0)
        {
            return new ContextWindowInfo(learned, maxOutput, ContextWindowSource.Learned);
        }

        return new ContextWindowInfo(null, maxOutput, ContextWindowSource.Unknown);
    }
}
