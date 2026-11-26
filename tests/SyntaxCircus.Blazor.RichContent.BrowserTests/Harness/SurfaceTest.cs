using System.Text.Json;
using System.Text.Json.Nodes;
using RichContent.Sample;
using SyntaxCircus.RichContent;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

/// <summary>Helpers for tests of the writing surface.</summary>
public abstract class SurfaceTest(SampleHost host) : BrowserTest(host)
{
    /// <summary>
    /// Wraps every surface's sender, which records each message in <c>window.sent</c> and treats each patch as
    /// <c>window.plan</c> says, in order, then passes: <c>pass</c>; <c>drop</c> fails without sending;
    /// <c>lose-reply</c> delivers, then fails; <c>hang</c> neither sends nor settles; <c>hold</c> sends only when
    /// <c>window.release()</c> is called. <c>window.answers</c> holds what .NET returned.
    /// </summary>
    private const string TapScript = """
        plan => {
            window.sent = [];
            window.answers = [];
            window.plan = [...plan];
            const modes = new Map();
            const held = [];
            window.release = () => Promise.all(held.splice(0).map(send => send()));
            for (const element of document.querySelectorAll('[data-rt-surface]')) {
                const transport = element.richContentSurface.transport;
                const real = transport.invoke;
                transport.invoke = (sequence, index, count, chunk) => {
                    if (!modes.has(sequence)) {
                        modes.set(sequence, window.plan.shift() ?? 'pass');
                    }

                    const mode = modes.get(sequence);
                    window.sent.push({ sequence, index, count, chunk, mode });
                    const deliver = () => real(sequence, index, count, chunk).then(answer => {
                        window.answers.push({ sequence, index, answer });
                        return answer;
                    });
                    switch (mode) {
                        case 'drop': return Promise.reject(new Error('forced failure'));
                        case 'lose-reply': return deliver().then(() => { throw new Error('forced lost reply'); });
                        case 'hang': return new Promise(() => {});
                        case 'hold': return new Promise((resolve, reject) => held.push(() => deliver().then(resolve, reject)));
                        default: return deliver();
                    }
                };
            }
        }
        """;

    // Puts the caret at a character offset within the element's text, or at its end for -1.
    private const string PlaceCaretScript = """
        (element, at) => {
            element.closest('[data-rt-surface]').focus();
            const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
            let left = at < 0 ? element.textContent.length : at;
            const range = document.createRange();
            range.selectNodeContents(element);
            range.collapse(true);
            for (let text = walker.nextNode(); text; text = walker.nextNode()) {
                if (left <= text.length) {
                    range.setStart(text, left);
                    range.collapse(true);
                    break;
                }

                left -= text.length;
            }

            getSelection().removeAllRanges();
            getSelection().addRange(range);
        }
        """;

    /// <summary>The nth writing surface of a block.</summary>
    protected ILocator Surface(string blockId, int index = 0) =>
        Page.Locator($"[data-block-id='{blockId}'] [data-rt-surface]").Nth(index);

    protected static Task PlaceCaretAsync(ILocator element, int at = -1) => element.EvaluateAsync(PlaceCaretScript, at);

    protected Task TapAsync(params string[] plan) => Page.EvaluateAsync(TapScript, plan);

    protected Task<int> SentPatchesAsync() =>
        Page.EvaluateAsync<int>("() => new Set(window.sent.map(message => message.sequence)).size");

    /// <summary>The patches handed to the sender, oldest first.</summary>
    protected async Task<JsonObject[]> PatchesAsync()
    {
        var json = await Page.EvaluateAsync<string[]>("""
            () => [...new Set(window.sent.map(m => m.sequence))]
                .map(s => window.sent.filter(m => m.sequence === s).map(m => m.chunk).join(''))
            """);
        return [.. json.Select(patch => JsonNode.Parse(patch)!.AsObject())];
    }

    /// <summary>Waits until the host's change count reaches at least <paramref name="count"/>.</summary>
    protected Task WaitForChangesAsync(int count) =>
        Page.WaitForFunctionAsync("count => Number(document.getElementById('changes').textContent) >= count", count);

    /// <summary>Waits until no surface is still waiting for an acknowledgement.</summary>
    protected Task WaitUntilAcknowledgedAsync() =>
        Page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('[data-rt-surface]')].every(e => e.richContentSurface.transport.unacknowledged === 0)",
            null,
            new() { Timeout = 30_000 });

    protected static ContentDocument Parse(string json) => ContentDocumentSerializer.Deserialize(json).Document!;

    protected static string Serialized(ContentDocument document) => ContentDocumentSerializer.Serialize(document);

    protected static string Original(string fixture) => Serialized(Fixtures.Get(fixture)!);

    /// <summary>The stored JSON of a rich text block's nodes.</summary>
    protected static JsonArray Nodes(string documentJson, string blockId) =>
        JsonNode.Parse(documentJson)!["blocks"]!.AsArray()
            .Single(block => (string?)block!["id"] == blockId)!["nodes"]!.AsArray();

    protected static string Compact(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";

    protected static string[] CompactAll(IEnumerable<JsonNode?> nodes) => [.. nodes.Select(Compact)];

    /// <summary>All the text of a node, in order.</summary>
    protected static string TextOf(JsonNode? node) =>
        (string?)node!["text"] ?? string.Concat(node["children"]?.AsArray().Select(TextOf) ?? []);

    /// <summary>
    /// Asserts that, in the block, the nodes from <paramref name="start"/> were replaced by
    /// <paramref name="replacement"/> nodes and that every other node and block is as it was.
    /// </summary>
    protected static JsonNode?[] AssertOnlyNodesReplaced(string before, string after, string blockId, int start, int removed, int replacement)
    {
        var old = Nodes(before, blockId);
        var now = Nodes(after, blockId);
        now.Count.ShouldBe(old.Count - removed + replacement, Compact(now));
        CompactAll(now.Take(start)).ShouldBe(CompactAll(old.Take(start)));
        CompactAll(now.Skip(start + replacement)).ShouldBe(CompactAll(old.Skip(start + removed)));

        var otherBlocks = (string json) => CompactAll(JsonNode.Parse(json)!["blocks"]!.AsArray().Where(block => (string?)block!["id"] != blockId));
        otherBlocks(after).ShouldBe(otherBlocks(before));
        return [.. now.Skip(start).Take(replacement)];
    }
}
