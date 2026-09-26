using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace Aitm.Server.Data;

/// <summary>
/// RESTRUCTURE.md Slice P1: the two routes `aitm mcp` forwards to over the pipe. <c>GET /tools</c> lists every
/// registered tool as <c>{ name, description, inputSchema }</c>. <c>POST /tools/{name}</c> takes the tool's JSON
/// arguments and answers its text result (200, text/plain), resolving the project through
/// <see cref="RequestProjectResolver"/> like /mcp and running under the same per-project writer gate
/// (<see cref="LockingAIFunction"/>), so there is still one writer. An unknown name is 404, arguments the tool
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

        AIFunctionArguments arguments = new();
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (body.RootElement.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in body.RootElement.EnumerateObject())
                    arguments[property.Name] = property.Value.Clone();
        }
        catch (JsonException ex)
        {
            return Results.Text($"the arguments are not valid JSON: {FirstLine(ex.Message)}", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            object? result = await tool.InvokeAsync(arguments, context.RequestAborted);
            return Results.Text(result?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or FormatException)
        {
            return Results.Text($"{name}: bad arguments ({FirstLine(ex.Message)})", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Text($"{name} failed ({ex.GetType().Name}: {FirstLine(ex.Message)})", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end >= 0 ? text[..end] : text;
    }
}
