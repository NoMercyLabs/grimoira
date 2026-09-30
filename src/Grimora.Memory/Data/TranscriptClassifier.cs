using System.Text.Json;
using System.Text.RegularExpressions;
using Grimora.Store.Data;
using static Grimora.Store.Data.JsonShape;

namespace Grimora.Memory.Data;

/// <summary>
/// The one recogniser for what was said in a Claude Code transcript: the owner's typed turns, his mid-turn
/// <c>queued_command</c> prompts (a string, or an array of text and image blocks), his AskUserQuestion
/// answers (the structured <c>toolUseResult.answers</c> map), pasted document blocks, a peer session's
/// queued messages, and Arc's own replies and questions. The PreCompact ledger (Grimora.Hooks) and the
/// SessionEnd chat index (<c>IndexChatTool</c>) both read through it, so neither can drift from the other.
/// Every property read goes through <see cref="JsonShape"/>: a string, array or null where an object was
/// expected is skipped, never thrown on.
///
/// Grimora.Cli compiles this file as a linked copy (it references no Grimora project) and defines
/// GRIMORA_CLI_LINKED so its copy stays internal, the same way <see cref="RegexTimeout"/> is linked.
/// </summary>
#if GRIMORA_CLI_LINKED
internal static partial class TranscriptClassifier
#else
public static partial class TranscriptClassifier
#endif
{
    /// <summary>One transcript entry, recognised as the owner's, a peer's, or Arc's own words.
    /// <paramref name="Index"/> is the entry's position in the transcript the classifier read, so a caller
    /// can cut at a compaction boundary computed on the same list.</summary>
    public readonly record struct ClassifiedEntry(int Index, string Timestamp, string Who, string Text);

    /// <summary>the owner's own words, whichever shape they arrived in: typed, a mid-turn queued message, or
    /// an AskUserQuestion answer.</summary>
    public static bool IsOwnerFamily(string who) => who is "the owner" or "the owner (mid-turn)" or "the owner (answer)";

    /// <summary>Every parseable line of a JSONL transcript, cloned out of its document. A partial write
    /// (the last line while Claude Code is still appending) is skipped.</summary>
    public static List<JsonElement> ReadEntries(string path)
    {
        List<JsonElement> outp = [];
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument d = JsonDocument.Parse(line);
                outp.Add(d.RootElement.Clone());
            }
            catch
            {
                // partial write
            }
        }
        return outp;
    }

    private static bool IsNoiseEntry(JsonElement e) => IsTrue(e, "isSidechain") || IsTrue(e, "isMeta");

    /// <summary>The index of the last compaction-summary turn, or -1. The real marker Claude Code writes on
    /// the injected summary turn is <c>isCompactSummary</c>; the "This session is being continued..." text
    /// match is only a fallback for a transcript that predates the marker.</summary>
    public static int LastCompactionBoundaryIndex(List<JsonElement> entries)
    {
        int boundary = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            JsonElement e = entries[i];
            if (GetString(e, "type") != "user") continue;
            string text = ExtractUserText(e).Trim();
            if (IsTrue(e, "isCompactSummary") || ContinuedSummaryStart().IsMatchOrFalse(text)) boundary = i;
        }
        return boundary;
    }

    /// <summary>A user turn's own text: the plain string it usually is, or the text blocks joined with a
    /// space. A pasted file arrives as its own <c>document</c> block, separate from the typed text around
    /// it; it is kept whole and ahead of the typed text, because losing which lines came from the paste
    /// versus what the owner typed about it is exactly the kind of impression a summary would leave.</summary>
    public static string ExtractUserText(JsonElement e) => JoinUserText(SplitUserText(e, " "));

    /// <summary>The pasted documents ahead of the typed text, as one string; either part may be empty.</summary>
    public static string JoinUserText((string Documents, string Typed) parts)
    {
        if (parts.Documents.Length == 0) return parts.Typed;
        if (parts.Typed.Length == 0) return parts.Documents;
        return $"{parts.Documents}\n\n{parts.Typed}";
    }

    /// <summary>A user turn's text in two parts: the pasted <c>document</c> blocks, and the typed text blocks
    /// joined with <paramref name="textSeparator"/> (the brief uses a space; the chat index keeps the line
    /// break between blocks so a re-index stores the same text it always did). A caller that classifies the
    /// turn does so on <c>Typed</c>, so a document pasted with a slash command is still a slash command.</summary>
    public static (string Documents, string Typed) SplitUserText(JsonElement e, string textSeparator)
    {
        if (!TryGetObjectProperty(e, "message", out JsonElement msg) || !TryGetObjectProperty(msg, "content", out JsonElement content)) return ("", "");
        if (content.ValueKind == JsonValueKind.String) return ("", content.GetString() ?? "");
        if (content.ValueKind != JsonValueKind.Array) return ("", "");

        List<string> documents = [];
        List<string> textParts = [];
        foreach (JsonElement b in content.EnumerateArray())
        {
            string? btype = GetString(b, "type");
            if (btype == "text")
            {
                textParts.Add(GetString(b, "text") ?? "");
            }
            else if (btype == "document")
            {
                string title = GetString(b, "title") ?? "";
                string data = TryGetObjectProperty(b, "source", out JsonElement source) ? GetString(source, "data") ?? "" : "";
                documents.Add($"[document: {title}]\n{data}");
            }
        }

        return (string.Join("\n\n", documents), string.Join(textSeparator, textParts));
    }

    /// <summary>If <paramref name="e"/> is a user turn answering one of <paramref name="askUserQuestionToolIds"/>,
    /// the question/answer pairs Claude Code recorded in its own <c>toolUseResult.answers</c> map — the
    /// structured record, not the "The user answered: ..." string glued together for display.
    /// <c>toolUseResult</c> is not always an object (a bare string or an array on a malformed or unrelated
    /// entry); those read as "no answers" instead of throwing.</summary>
    private static List<(string question, string answer)>? TryGetAnswers(JsonElement e, HashSet<string> askUserQuestionToolIds)
    {
        bool answersAskUserQuestion = false;
        foreach (JsonElement b in BlocksOf(e))
        {
            if (GetString(b, "type") != "tool_result") continue;
            string? toolUseId = GetString(b, "tool_use_id");
            if (toolUseId is not null && askUserQuestionToolIds.Contains(toolUseId)) answersAskUserQuestion = true;
        }
        if (!answersAskUserQuestion) return null;

        if (!TryGetObjectProperty(e, "toolUseResult", out JsonElement tur) || !TryGetObjectProperty(tur, "answers", out JsonElement answersEl)) return null;
        if (answersEl.ValueKind != JsonValueKind.Object) return null;

        List<(string question, string answer)> outp = [];
        foreach (JsonProperty p in answersEl.EnumerateObject())
        {
            outp.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : ""));
        }
        return outp;
    }

    /// <summary>The content blocks of one entry's message, or nothing when the message or its content is
    /// not the object/array shape.</summary>
    public static IEnumerable<JsonElement> BlocksOf(JsonElement e)
    {
        if (!TryGetObjectProperty(e, "message", out JsonElement msg) || !TryGetObjectProperty(msg, "content", out JsonElement content)) yield break;
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement b in content.EnumerateArray()) yield return b;
    }

    // One pass, in transcript order, with the dedupe rules applied once: two recognisers used to drift (a
    // mid-turn queued message landed after every typed message regardless of when it was queued, and a
    // duplicate queued_command could be counted twice by one and once by the other). The PreCompact ledger
    // takes every entry, never cut; the brief takes only the the owner-family entries after the compaction
    // boundary; the chat index stores the answers, mid-turn prompts and peer messages the streaming pass
    // cannot see. Re-running this on the same transcript is deterministic.
    public static (List<ClassifiedEntry> Entries, int Skipped) ClassifyEntries(List<JsonElement> entries)
    {
        List<ClassifiedEntry> outp = [];
        HashSet<string> seenAssistantText = [];
        HashSet<string> seenQueued = [];
        HashSet<string> askUserQuestionToolIds = [];
        int skipped = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            JsonElement e = entries[i];
            if (e.ValueKind != JsonValueKind.Object || IsNoiseEntry(e)) continue;

            // One bad entry must never kill the whole classification pass: every guard above this method
            // already refuses to read a property of the wrong shape, but this is the backstop for whatever
            // shape nobody has seen yet — it costs one entry, counted and reported, not the whole run.
            try
            {
                string type = GetString(e, "type") ?? "";
                string timestamp = GetString(e, "timestamp") ?? "";

                if (type == "user")
                {
                    List<(string question, string answer)>? answers = TryGetAnswers(e, askUserQuestionToolIds);
                    if (answers is { Count: > 0 })
                    {
                        foreach ((string question, string answer) in answers)
                        {
                            outp.Add(new ClassifiedEntry(i, timestamp, "the owner (answer)", $"{question}: {answer}"));
                        }
                        continue;
                    }

                    if (IsTrue(e, "isCompactSummary")) continue;

                    string trimmed = ExtractUserText(e).Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)) continue;
                    if (ContinuedSummaryStart().IsMatchOrFalse(trimmed)) continue;
                    if (HookFeedbackNotice().IsMatchOrFalse(trimmed[..Math.Min(60, trimmed.Length)])) continue;
                    outp.Add(new ClassifiedEntry(i, timestamp, "the owner", trimmed));
                }
                else if (type == "assistant")
                {
                    string? msgId = TryGetObjectProperty(e, "message", out JsonElement m) ? GetString(m, "id") : null;
                    foreach (JsonElement b in BlocksOf(e))
                    {
                        string? btype = GetString(b, "type");
                        if (btype == "text")
                        {
                            string text = (GetString(b, "text") ?? "").Trim();
                            if (text.Length == 0) continue;
                            if (!seenAssistantText.Add($"{msgId}\u0000{text}")) continue;
                            outp.Add(new ClassifiedEntry(i, timestamp, "Arc", text));
                        }
                        else if (btype == "tool_use" && GetString(b, "name") == "AskUserQuestion")
                        {
                            string? toolId = GetString(b, "id");
                            if (toolId is not null) askUserQuestionToolIds.Add(toolId);
                            string question = AskUserQuestionText(b);
                            if (question.Length > 0) outp.Add(new ClassifiedEntry(i, timestamp, "Arc (question)", question));
                        }
                    }
                }
                else if (type == "attachment")
                {
                    if (!TryGetObjectProperty(e, "attachment", out JsonElement att)) continue;
                    if (GetString(att, "type") != "queued_command") continue;
                    if (GetString(att, "commandMode") == "task-notification") continue;

                    string dedupeKey = GetString(att, "source_uuid") ?? GetString(e, "uuid") ?? "";
                    if (dedupeKey.Length > 0 && !seenQueued.Add(dedupeKey)) continue;

                    string prompt = ExtractPrompt(att);
                    bool isPeer = TryGetObjectProperty(att, "origin", out JsonElement origin) && GetString(origin, "kind") == "peer";
                    if (isPeer)
                    {
                        string sender = FromNameAttribute().MatchOrEmpty(prompt) is { Success: true } m2
                            ? m2.Groups[1].Value
                            : GetString(origin, "name") ?? "unknown";
                        outp.Add(new ClassifiedEntry(i, timestamp, $"Peer {sender}", prompt));
                    }
                    else
                    {
                        outp.Add(new ClassifiedEntry(i, timestamp, "the owner (mid-turn)", prompt));
                    }
                }
            }
            catch
            {
                skipped++;
            }
        }
        return (outp, skipped);
    }

    /// <summary>A queued command's prompt: the plain string it usually is, or — 601 of 2,476 real
    /// <c>queued_command</c> prompts on the 8 largest transcripts sampled, every one of them the owner's own
    /// mid-turn words — an array of blocks (text, image, or a mix) the way a pasted screenshot arrives.
    /// Image blocks become the literal marker "[image]"; blocks are joined in order, one per line.</summary>
    private static string ExtractPrompt(JsonElement att)
    {
        if (!TryGetObjectProperty(att, "prompt", out JsonElement promptEl)) return "";
        if (promptEl.ValueKind == JsonValueKind.String) return (promptEl.GetString() ?? "").Trim();
        if (promptEl.ValueKind != JsonValueKind.Array) return "";

        List<string> parts = [];
        foreach (JsonElement block in promptEl.EnumerateArray())
        {
            string? btype = GetString(block, "type");
            if (btype == "image") parts.Add("[image]");
            else if (btype == "text") parts.Add(GetString(block, "text") ?? "");
        }
        return string.Join("\n", parts).Trim();
    }

    private static string AskUserQuestionText(JsonElement toolUseBlock)
    {
        if (!TryGetObjectProperty(toolUseBlock, "input", out JsonElement input)) return "";
        if (!TryGetObjectProperty(input, "questions", out JsonElement questions) || questions.ValueKind != JsonValueKind.Array) return "";
        List<string> parts = [];
        foreach (JsonElement q in questions.EnumerateArray())
        {
            string? text = GetString(q, "question");
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return string.Join(" | ", parts);
    }

    [GeneratedRegex(@"^(This session is being continued|Caveat: The messages below|\[Request interrupted)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ContinuedSummaryStart();
    [GeneratedRegex("hook (feedback|additional context)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex HookFeedbackNotice();
    [GeneratedRegex("from-name=\"([^\"]*)\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex FromNameAttribute();
}
