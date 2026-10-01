using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Grimoira.Server.Data;

/// <summary>
/// RESTRUCTURE.md Slice P1: the two routes `grimoira mcp` forwards to over the pipe. <c>GET /tools</c> lists every
/// registered tool as <c>{ name, description, inputSchema }</c>. <c>POST /tools/{name}</c> takes the tool's JSON
/// arguments and answers its text result (200, text/plain), resolving the project through
/// <see cref="RequestProjectResolver"/> like /mcp and running under the same per-project writer gate
/// (<see cref="LockingAiFunction"/>), so there is still one writer. An unknown name is 404, arguments the tool
/// cannot bind are 400, any other failure is 500; the body is a one-line reason, never a stack trace.
/// </summary>
public static class ToolsEndpoint
{
    public static IResult List(IReadOnlyList<AIFunction> tools) =>
        Results.Json(tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.JsonSchema }));

    public static async Task<IResult> Call(string name, HttpContext context, IReadOnlyList<AIFunction> tools)
    {
        AIFunction? tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return Results.Text($"no such tool: {name}", statusCode: StatusCodes.Status404NotFound);

        JsonElement body;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            body = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Results.Text($"the arguments are not valid JSON: {FirstLine(ex.Message)}", statusCode: StatusCodes.Status400BadRequest);
        }

        // Binding is its own step: only an argument that does not fit the tool's parameters is a 400.
        if (!TryBind(tool, body, out AIFunctionArguments arguments, out string bindError))
            return Results.Text(bindError, statusCode: StatusCodes.Status400BadRequest);

        try
        {
            object? result = await tool.InvokeAsync(arguments, context.RequestAborted);
            return Results.Text(result?.ToString() ?? "");
        }
        catch (ProjectBusyException ex)
        {
            return Results.Text(FirstLine(ex.Message), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw; // the client went away; there is nobody to answer
        }
        catch (Exception ex)
        {
            // Anything thrown while the tool runs is the tool's own failure, whatever its type.
            return Results.Text($"{name} failed ({ex.GetType().Name}: {FirstLine(ex.Message)})", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryBind(AIFunction tool, JsonElement body, out AIFunctionArguments arguments, out string error)
    {
        arguments = [];
        error = "";
        Dictionary<string, JsonElement> byName = body.ValueKind == JsonValueKind.Object
            ? body.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : [];
        if (tool.UnderlyingMethod is null)
        {
            foreach ((string key, JsonElement value) in byName) arguments[key] = value;
            return true;
        }

        foreach (System.Reflection.ParameterInfo parameter in tool.UnderlyingMethod.GetParameters())
        {
            Type type = parameter.ParameterType;
            if (type == typeof(Microsoft.Data.Sqlite.SqliteConnection) || type == typeof(CancellationToken)
                || type == typeof(IServiceProvider) || type == typeof(AIFunctionArguments)) continue;
            string parameterName = parameter.Name ?? "";
            if (!byName.TryGetValue(parameterName, out JsonElement value))
            {
                if (parameter.HasDefaultValue || Nullable.GetUnderlyingType(type) is not null) continue;
                error = $"missing argument '{parameterName}'";
                return false;
            }
            try
            {
                arguments[parameterName] = value.Deserialize(type, AIJsonUtilities.DefaultOptions);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
            {
                error = $"argument '{parameterName}' does not fit {type.Name}: {FirstLine(ex.Message)}";
                return false;
            }
        }
        return true;
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end >= 0 ? text[..end] : text;
    }
}
