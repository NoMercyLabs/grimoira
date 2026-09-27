using Microsoft.Extensions.AI;

namespace Aitm.Server.Data;

/// <summary>
/// Wraps a store-backed tool's <see cref="AIFunction"/> so every call — read or write, from any
/// session — acquires the calling project's single <see cref="ProjectHandle.Gate"/> before the real
/// tool method runs and always releases it afterwards, even when the call throws. This is the "one
/// writer" half of RESTRUCTURE.md "Slice 26"; <see cref="ProjectStore"/> is the "one open store" half.
/// </summary>
internal sealed class LockingAiFunction(AIFunction inner, ProjectStore store, IHttpContextAccessor httpContextAccessor, TimeSpan? gateTimeout = null) : AIFunction
{
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => inner.JsonSchema;
    public override System.Text.Json.JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
    public override System.Reflection.MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string instance = RequestProjectResolver.Resolve(httpContextAccessor.HttpContext);
        ProjectHandle handle = store.Acquire(instance);
        if (gateTimeout is { } limit)
        {
            if (!await handle.Gate.WaitAsync(limit, cancellationToken))
                throw new ProjectBusyException($"project busy: another call has held project '{instance}' for over {limit.TotalSeconds:0} s.");
        }
        else
        {
            await handle.Gate.WaitAsync(cancellationToken);
        }
        try
        {
            return await inner.InvokeAsync(arguments, cancellationToken);
        }
        finally
        {
            handle.Gate.Release();
        }
    }
}

/// <summary>A tool call could not get its project's writer gate within the limit; /tools answers 503.</summary>
public sealed class ProjectBusyException(string message) : Exception(message);
