// A stand-in for an MCP stdio server, used only by the test-support harness's own tests. It speaks just
// enough JSON-RPC for McpProcess.Run: it answers initialize (id 0), tools/list (id 1) and every
// tools/call (id >= 100). Each tools/call reply is delayed a little and its text lists the ids of every
// request that had ARRIVED on stdin before the reply was written, so a test can tell whether the driver
// pipelined its calls (the next id is already in the list) or waited for each reply first.
using System.Text.Json;

List<int> arrived = [];
object gate = new();
List<Task> pending = [];

string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (line.Trim().Length == 0) continue;
    int id;
    string method;
    try
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
        id = idEl.GetInt32();
        method = doc.RootElement.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
    }
    catch (JsonException) { continue; }

    lock (gate) arrived.Add(id);
    if (method == "tools/call")
    {
        pending.Add(Task.Run(async () =>
        {
            await Task.Delay(300);
            string seen;
            lock (gate) seen = string.Join(",", arrived.Where(i => i >= 100));
            Reply(id, "{\"content\":[{\"type\":\"text\",\"text\":\"arrived-before-reply:" + seen + "\"}]}");
        }));
    }
    else if (method == "tools/list")
    {
        Reply(id, "{\"tools\":[]}");
    }
    else
    {
        Reply(id, "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}");
    }
}
Task.WaitAll([.. pending]);

void Reply(int id, string resultJson)
{
    string text = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";
    lock (gate) { Console.Out.WriteLine(text); Console.Out.Flush(); }
}
