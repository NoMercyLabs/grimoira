namespace Grimora.Server.Data;

/// <summary>
/// The session identity a request carries, the same "one header, one rule" shape as
/// <see cref="RequestProjectResolver"/>: the <c>Grimora-Session</c> header, or "" (no session context)
/// when the request sent none — an older bridge/thin client, or a direct HTTP call. "" is never treated as
/// a session name of its own; <see cref="Grimora.Brain.Tools.BrainStageTool.LedgerPath"/> falls back to the
/// original shared ledger for it, unchanged from before session keying existed.
/// </summary>
public static class RequestSessionResolver
{
    public const string SessionHeader = "Grimora-Session";

    public static string Resolve(HttpContext? context)
    {
        string? header = context?.Request.Headers[SessionHeader].FirstOrDefault();
        return string.IsNullOrWhiteSpace(header) ? "" : header;
    }
}
