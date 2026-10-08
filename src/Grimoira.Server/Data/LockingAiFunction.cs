using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace Grimoira.Server.Data;

/// <summary>
/// Wraps a store-backed tool's <see cref="AIFunction"/> so every writing call, from any session,
/// acquires the calling project's single <see cref="ProjectHandle.Gate"/> before the real tool method
/// runs and always releases it afterwards, even when the call throws. This is the "one writer" half of
/// RESTRUCTURE.md "Slice 26"; <see cref="ProjectStore"/> is the "one open store" half.
/// A read-only tool (<see cref="Grimoira.Store.Tools.ITool.IsReadOnly"/>) skips the gate and runs on
/// its own <see cref="ProjectHandle.OpenReader"/> connection, handed to the tool's
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> parameter through <see cref="ReaderKey"/>, so a
/// recall or graph query answers while a write or a session-end index job holds the gate (issue #24).
/// </summary>
internal sealed class LockingAiFunction(AIFunction inner, ProjectStore store, IHttpContextAccessor httpContextAccessor, bool readOnly, TimeSpan? gateTimeout = null) : AIFunction
{
    /// <summary>The <see cref="AIFunctionArguments.Context"/> key under which a read-only call's own
    /// connection travels to the parameter binder in <see cref="McpToolFactory"/>.</summary>
    public static readonly object ReaderKey = new();

    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => inner.JsonSchema;
    public override System.Text.Json.JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
    public override System.Reflection.MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string instance = RequestProjectResolver.Resolve(httpContextAccessor.HttpContext);
        ProjectHandle handle = store.Acquire(instance);
        if (readOnly)
        {
            using Microsoft.Data.Sqlite.SqliteConnection reader = handle.OpenReader();
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Context[ReaderKey] = reader;
            try { return await inner.InvokeAsync(arguments, cancellationToken); }
            finally { CallLog.Record(handle, Name, instance, 0, 0); }
        }

        TimeSpan limit = gateTimeout ?? McpToolFactory.DefaultGateTimeout;
        long waitStart = Stopwatch.GetTimestamp();
        long waitedMs = 0;
        long holdStart = 0;
        bool entered = false;
        try
        {
            if (!await handle.Gate.WaitAsync(limit, cancellationToken))
                throw new ProjectBusyException($"project busy: another call has held project '{instance}' for over {limit.TotalSeconds:0} s.");
            entered = true;
            waitedMs = CallLog.Milliseconds(waitStart);
            holdStart = Stopwatch.GetTimestamp();
            return await inner.InvokeAsync(arguments, cancellationToken);
        }
        finally
        {
            if (entered) handle.Gate.Release();
            CallLog.Record(handle, Name, instance,
                entered ? waitedMs : CallLog.Milliseconds(waitStart), entered ? CallLog.Milliseconds(holdStart) : 0);
        }
    }
}

/// <summary>A tool call could not get its project's writer gate within the limit; /tools answers 503.</summary>
public sealed class ProjectBusyException(string message) : Exception(message);
