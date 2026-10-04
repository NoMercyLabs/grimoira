using System.Text.Json;
using Grimoira.Hooks.Tools;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// The CLI's UserPromptSubmit answer is two handlers in one envelope: the local compaction restore first,
/// then the server's prompt recall. Two JSON envelopes on stdout would be broken JSON, so they are merged.
/// </summary>
public class HookEnvelopeTests
{
    private static string Envelope(string context) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = context },
    });

    private static string ContextOf(string output)
    {
        using JsonDocument doc = JsonDocument.Parse(output);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    [Fact]
    public void MergeEmptyLeft() => Assert.Equal(Envelope("recall"), HookEnvelope.Merge("", Envelope("recall")));

    [Fact]
    public void MergeEmptyRight() => Assert.Equal(Envelope("restore"), HookEnvelope.Merge(Envelope("restore"), ""));

    [Fact]
    public void MergeBothKeepsRestoreFirst()
    {
        string merged = HookEnvelope.Merge(Envelope("restore text"), Envelope("recall text"));

        Assert.Equal("restore text\n\nrecall text", ContextOf(merged));
        using JsonDocument doc = JsonDocument.Parse(merged);
        Assert.Equal("UserPromptSubmit", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
    }

    [Fact]
    public void MergeBadJsonReturnsLeft()
    {
        string left = Envelope("restore");
        Assert.Equal(left, HookEnvelope.Merge(left, "{not json"));
        Assert.Equal(left, HookEnvelope.Merge(left, "{\"other\":1}"));
    }
}
