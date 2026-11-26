using System.Text.Json.Nodes;
using SyntaxCircus.Blazor.RichContent.BrowserTests.Harness;

namespace SyntaxCircus.Blazor.RichContent.BrowserTests;

/// <summary>
/// The surface script on its own: attached to a detached element with a stand-in for the component, so that shapes
/// and markup the fixtures do not reach can be built and read.
/// </summary>
public class SurfaceDomTests(SampleHost host) : SurfaceTest(host)
{
    private const string AttachScript = """
        async ([nodes, profile]) => {
            const module = await import('/_content/SyntaxCircus.Blazor.RichContent/surface/surface.js');
            const element = document.createElement('div');
            element.id = 'lab';
            document.body.append(element);
            window.patches = [];
            const chunks = new Map();
            const component = {
                invokeMethodAsync(method, sequence, index, count, chunk) {
                    if (method === 'ReceiveEdit') {
                        chunks.set(sequence, (chunks.get(sequence) ?? '') + chunk);
                        if (index === count - 1) window.patches.push(JSON.parse(chunks.get(sequence)));
                    }
                    return Promise.resolve(true);
                },
            };
            module.attach(element, component, { profile });
            module.sync(element, JSON.parse(nodes).map((node, i) => ({ key: `s${i + 1}`, node })), false, true, 1);
            window.lab = { module, element, component };
        }
        """;

    private const string ReadScript =
        "() => JSON.stringify(window.lab.element.richContentSurface.reader.read().map(entry => entry.node))";

    private async Task AttachAsync(string nodes, string profile = "prose", bool strictCsp = false)
    {
        await OpenFixtureAsync("empty", strictCsp: strictCsp);
        await Page.EvaluateAsync(AttachScript, new[] { nodes, profile });
    }

    private async Task<string> ReadAsync() => Compact(JsonNode.Parse(await Page.EvaluateAsync<string>(ReadScript)));

    // Replaces the lab surface's content with markup, as the browser's editing might leave it, and reads it.
    private async Task<string> ReadMarkupAsync(string markup)
    {
        await Page.EvaluateAsync("markup => { window.lab.element.innerHTML = markup; }", markup);
        return await ReadAsync();
    }

    private static string Canonical(string json) => Compact(JsonNode.Parse(json));

    private static string Text(string text) => $$"""{"kind":"text","text":"{{text}}"}""";

    [Theory]
    // Ordering absent, false and true, also in a nested list; adjacent lists.
    [InlineData("""[{"kind":"list","children":[{"kind":"listItem","children":[{"kind":"text","text":"a"},{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"text","text":"b"},{"kind":"list","children":[{"kind":"listItem","children":[{"kind":"text","text":"c"}]}]}]}]}]}]},{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{"kind":"text","text":"d"}]}]},{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{"kind":"text","text":"e"}]}]}]""")]
    // Empty nodes: children absent, empty, and one empty text run.
    [InlineData("""[{"kind":"paragraph"},{"kind":"paragraph","children":[]},{"kind":"paragraph","children":[{"kind":"text","text":""}]},{"kind":"heading","level":3},{"kind":"list","ordered":false,"children":[{"kind":"listItem"},{"kind":"listItem","children":[]},{"kind":"listItem","children":[{"kind":"paragraph"}]}]},{"kind":"quote","children":[{"kind":"paragraph","children":[]}]}]""")]
    // List item forms: only nested lists, paragraphs, paragraphs then lists.
    [InlineData("""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{"kind":"text","text":"a"}]}]}]},{"kind":"listItem","children":[{"kind":"paragraph","children":[{"kind":"text","text":"b"}]}]},{"kind":"listItem","children":[{"kind":"paragraph","children":[{"kind":"text","text":"c"}]},{"kind":"paragraph","children":[{"kind":"text","text":"d"}]},{"kind":"list","children":[{"kind":"listItem","children":[{"kind":"paragraph","children":[{"kind":"text","text":"e"}]}]}]}]}]}]""")]
    // Inline kinds, spaces and line breaks.
    [InlineData("""[{"kind":"paragraph","children":[{"kind":"text","text":"  two  spaces\nand a line  "},{"kind":"strong","children":[{"kind":"emphasis","children":[{"kind":"link","url":"https://example.com/a?b=1&c=2","children":[{"kind":"inlineCode","text":" x < y "}]}]}]}]},{"kind":"quote","children":[{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{"kind":"text","text":"q"}]}]},{"kind":"paragraph","children":[{"kind":"text","text":"r"}]}]}]""")]
    public async Task EveryShape_ReadsBackAsItWasStored(string nodes)
    {
        await AttachAsync(nodes);

        (await ReadAsync()).ShouldBe(Canonical(nodes));
    }

    [Fact]
    public async Task ANestedListInsideAChangedNode_KeepsItsMissingOrdering()
    {
        await AttachAsync("""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"text","text":"a"},{"kind":"list","children":[{"kind":"listItem"},{"kind":"listItem","children":[]}]}]}]}]""");

        await Page.EvaluateAsync("() => { const e = window.lab.element; e.querySelector('li').firstChild.appendData('!'); e.dispatchEvent(new InputEvent('input', { bubbles: true })); }");

        var patch = await Page.EvaluateAsync<string>("() => JSON.stringify(window.patches[0].changed[0].node)");
        Canonical(patch).ShouldBe("""{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"text","text":"a!"},{"kind":"list","children":[{"kind":"listItem"},{"kind":"listItem","children":[]}]}]}]}""");
    }

    public static TheoryData<string, string> Markup => new()
    {
        // A <br> only separates words; the one kept at the end of a block means nothing.
        { "<p>one<br>two<br></p>", $$"""[{"kind":"paragraph","children":[{{Text("one two")}}]}]""" },
        { "<p>one <br> two</p>", $$"""[{"kind":"paragraph","children":[{{Text("one  two")}}]}]""" },
        // An item holding only nested lists gains no text.
        { "<ul><li><br><ul><li>a</li></ul></li></ul>", $$"""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("a")}}]}]}]}]}]""" },
        // A list directly inside a list belongs to the item before it.
        { "<ol><li>a</li><ol><li>b</li></ol></ol>", $$"""[{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{{Text("a")}},{"kind":"list","ordered":true,"children":[{"kind":"listItem","children":[{{Text("b")}}]}]}]}]}]""" },
        // A paragraph inside an item stays.
        { "<ul><li><p>a</p></li></ul>", $$"""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"paragraph","children":[{{Text("a")}}]}]}]}]""" },
        // Divisions inside an item become paragraphs.
        { "<ul><li><div>a</div><div>b</div></li></ul>", $$"""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{"kind":"paragraph","children":[{{Text("a")}}]},{"kind":"paragraph","children":[{{Text("b")}}]}]}]}]""" },
        // Content after a nested list starts another item, which keeps the order.
        { "<ul><li>a<ul><li>b</li></ul>c</li></ul>", $$"""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("a")}},{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("b")}}]}]}]},{"kind":"listItem","children":[{{Text("c")}}]}]}]""" },
        // Adjacent lists stay apart.
        { "<ul><li>a</li></ul><ul><li>b</li></ul>", $$"""[{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("a")}}]}]},{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("b")}}]}]}]""" },
        // Emptied containers are dropped.
        { "<p><a href=\"https://example.com\"></a><strong></strong>t</p><ul></ul><blockquote></blockquote><ol> </ol>", $$"""[{"kind":"paragraph","children":[{{Text("t")}}]}]""" },
        // A quote with anything left in it, even a filler or an emptied list, keeps a paragraph to type in.
        { "<blockquote><ul></ul></blockquote>", $$"""[{"kind":"quote","children":[{"kind":"paragraph","children":[{{Text("")}}]}]}]""" },
        { "<blockquote><br></blockquote>", $$"""[{"kind":"quote","children":[{"kind":"paragraph","children":[{{Text("")}}]}]}]""" },
        // Loose text in a quote, and a heading or quote inside one.
        { "<blockquote>a<h2>b</h2><blockquote><p>c</p></blockquote></blockquote>", $$"""[{"kind":"quote","children":[{"kind":"paragraph","children":[{{Text("a")}}]},{"kind":"paragraph","children":[{{Text("b")}}]},{"kind":"paragraph","children":[{{Text("c")}}]}]}]""" },
        // Only the allowed tags are read; everything else is unwrapped, and links do not nest.
        { "<p><u><span style=\"font-weight:bold\">a</span></u><font>b</font><img src=\"data:,\"><!-- c --></p>", $$"""[{"kind":"paragraph","children":[{{Text("ab")}}]}]""" },
        { "<h1>a</h1><table><tbody><tr><td>b</td><td>c</td></tr></tbody></table><pre>d</pre>", $$"""[{"kind":"paragraph","children":[{{Text("a")}}]},{"kind":"paragraph","children":[{{Text("b")}}]},{"kind":"paragraph","children":[{{Text("c")}}]},{"kind":"paragraph","children":[{{Text("d")}}]}]""" },
        { "<p><b><i>a</i></b><code><b>b</b>c</code><code></code></p>", $$"""[{"kind":"paragraph","children":[{"kind":"strong","children":[{"kind":"emphasis","children":[{{Text("a")}}]}]},{"kind":"inlineCode","text":"bc"}]}]""" },
        // A block inside formatting loses the formatting, not the text.
        { "<span><p>a</p></span>b", $$"""[{"kind":"paragraph","children":[{{Text("a")}}]},{"kind":"paragraph","children":[{{Text("b")}}]}]""" },
        // A link without any address is not a link.
        { "<p><a name=\"x\">a</a></p>", $$"""[{"kind":"paragraph","children":[{{Text("a")}}]}]""" },
    };

    [Theory]
    [MemberData(nameof(Markup))]
    public async Task BrowserMarkup_IsReadAsModelNodes(string markup, string expected)
    {
        await AttachAsync("[]");

        (await ReadMarkupAsync(markup)).ShouldBe(Canonical(expected));
    }

    private static string InlineCode(string text) => $$"""{"kind":"inlineCode","text":"{{text}}"}""";

    private static string Paragraph(params string[] children) => $$"""[{"kind":"paragraph","children":[{{string.Join(',', children)}}]}]""";

    // Code is what shows in the code font: the first family an element itself names, in its face or its style.
    public static TheoryData<string, string> CodeShapes => new()
    {
        { "<p>a <font face=\"rt-code, monospace\">b</font> c</p>", Paragraph(Text("a "), InlineCode("b"), Text(" c")) },
        { "<p>a <span style=\"font-family: rt-code, monospace;\">b</span></p>", Paragraph(Text("a "), InlineCode("b")) },
        { "<p><span style='font-family: \"rt-code\", monospace'>b</span></p>", Paragraph(InlineCode("b")) },
        // Code named again inside code changes nothing.
        { "<p><font face=\"rt-code\">a<font face=\"rt-code, monospace\">b</font>c</font></p>", Paragraph(InlineCode("abc")) },
        { "<p><font face=\"rt-code\">a<span style=\"font-family: rt-code, monospace\">b</span>c</font></p>", Paragraph(InlineCode("abc")) },
        { "<p><span style=\"font-family: rt-code\">a<font face=\"rt-code\">b</font></span></p>", Paragraph(InlineCode("ab")) },
        // Two elements side by side stay two runs, as two stored ones do.
        { "<p><font face=\"rt-code\">a</font><font face=\"rt-code, monospace\">b</font></p>", Paragraph(InlineCode("a"), InlineCode("b")) },
        // Another family inside code is not code, whichever way it is named.
        { "<p><font face=\"rt-code, monospace\">a<font face=\"rt-plain\">b</font></font></p>", Paragraph(InlineCode("a"), Text("b")) },
        { "<p><font face=\"rt-code\">a<font face=\"rt-plain, serif\">b</font></font></p>", Paragraph(InlineCode("a"), Text("b")) },
        { "<p><font face=\"rt-code\">a<span style=\"font-family: rt-plain\">b</span></font></p>", Paragraph(InlineCode("a"), Text("b")) },
        { "<p><span style=\"font-family: rt-code\">a<span style=\"font-family: serif\">b</span></span></p>", Paragraph(InlineCode("a"), Text("b")) },
        { "<p><span style=\"font-family: rt-code\">a<span style=\"font-family: inherit\">b</span></span></p>", Paragraph(InlineCode("ab")) },
        // A style outranks a face, except on the two faces the surface's own styles pin.
        { "<p><font face=\"rt-code, monospace\" style=\"font-family: serif\">a</font></p>", Paragraph(Text("a")) },
        { "<p><font face=\"serif\" style=\"font-family: rt-code\">a</font></p>", Paragraph(InlineCode("a")) },
        { "<p><font face=\"rt-code\" style=\"font-family: serif\">a</font></p>", Paragraph(InlineCode("a")) },
        // Any element can name it: formatting, a link, a block.
        { "<p><b style=\"font-family: rt-code\">a</b></p>", Paragraph($$"""{"kind":"strong","children":[{{InlineCode("a")}}]}""") },
        { "<p><a href=\"https://example.com/\" style=\"font-family: rt-code\">a</a></p>", Paragraph($$"""{"kind":"link","url":"https://example.com/","children":[{{InlineCode("a")}}]}""") },
        { "<p style=\"font-family: rt-code, monospace\">a<b>b</b></p>", Paragraph(InlineCode("a"), $$"""{"kind":"strong","children":[{{InlineCode("b")}}]}""") },
        // Only the first family counts, and only the attribute is the element's own.
        { "<p><span style=\"font-family: monospace, rt-code\">a</span><font face=\"monospace, rt-code\">b</font><span face=\"rt-code\">c</span></p>", Paragraph(Text("abc")) },
    };

    [Theory]
    [MemberData(nameof(CodeShapes))]
    public async Task Code_IsReadFromTheFamilyAnElementItselfNames(string markup, string expected)
    {
        await AttachAsync("[]");

        (await ReadMarkupAsync(markup)).ShouldBe(Canonical(expected));
    }

    // Neither a page's styles nor anything around the surface can make text code.
    [Fact]
    public async Task AFontFromOutsideTheElementsOfTheSurface_MakesNothingCode()
    {
        await AttachAsync(Paragraph(Text("plain")));
        await Page.EvaluateAsync("""
            () => {
                const sheet = new CSSStyleSheet();
                sheet.replaceSync('#lab, #lab p, .coded { font-family: rt-code, monospace; }');
                document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
                window.lab.element.firstElementChild.classList.add('coded');
                window.lab.element.style.fontFamily = 'rt-code';
                const around = document.createElement('code');
                around.style.fontFamily = 'rt-code';
                window.lab.element.replaceWith(around);
                around.append(window.lab.element);
            }
            """);

        (await ReadAsync()).ShouldBe(Canonical(Paragraph(Text("plain"))));
    }

    [Fact]
    public async Task AParagraphHoldingAList_IsReadAsTwoNodes()
    {
        await AttachAsync("[]");
        // The HTML parser cannot make this, but a browser's list command can.
        await Page.EvaluateAsync("""
            () => {
                const paragraph = document.createElement('p');
                const list = document.createElement('ul');
                list.appendChild(document.createElement('li')).textContent = 'b';
                paragraph.append('a', list);
                window.lab.element.replaceChildren(paragraph);
            }
            """);

        (await ReadAsync()).ShouldBe(Canonical($$"""[{"kind":"paragraph","children":[{{Text("a")}}]},{"kind":"list","ordered":false,"children":[{"kind":"listItem","children":[{{Text("b")}}]}]}]"""));
    }

    [Theory]
    [InlineData("javascript:window.ran=1")]
    [InlineData("JaVaScRiPt:window.ran=1")]
    [InlineData("java\\tscript:window.ran=1")]
    [InlineData(" javascript:window.ran=1")]
    [InlineData("data:text/html,<script>parent.ran=1</script>")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\\\evil.example/x")]
    [InlineData("https://user@evil.example/")]
    [InlineData("")]
    public async Task AnUnsafeStoredAddress_IsNeverALiveLink_AndIsReadBackUnchanged(string jsonUrl)
    {
        var nodes = $$"""[{"kind":"paragraph","children":[{"kind":"link","url":"{{jsonUrl}}","children":[{{Text("click")}}]}]}]""";
        await AttachAsync(nodes, strictCsp: true);

        (await Page.Locator("#lab a").GetAttributeAsync("href")).ShouldBeNull();
        await Page.Locator("#lab a").ClickAsync();

        (await ReadAsync()).ShouldBe(Canonical(nodes));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1#c")]
    [InlineData("/relative/page")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+1-555-0100")]
    public async Task ASafeStoredAddress_IsALink_ThatDoesNotNavigate(string url)
    {
        await AttachAsync($$"""[{"kind":"paragraph","children":[{"kind":"link","url":"{{url}}","children":[{{Text("click")}}]}]}]""", strictCsp: true);

        (await Page.Locator("#lab a").GetAttributeAsync("href")).ShouldBe(url);
        await Page.Locator("#lab a").ClickAsync();

        new Uri(Page.Url).AbsolutePath.ShouldBe("/fixture/empty");
    }

    [Fact]
    public async Task StoredMarkupInNodeText_IsShownAsText()
    {
        const string markup = "<img src=x onerror=window.pwned=1>";
        await AttachAsync($$"""[{"kind":"paragraph","children":[{{Text(markup)}},{"kind":"inlineCode","text":"{{markup}}"}]}]""");

        (await Page.Locator("#lab").TextContentAsync()).ShouldBe(markup + markup);
        (await Page.Locator("#lab img").CountAsync()).ShouldBe(0);
        (await Page.EvaluateAsync<bool>("() => window.pwned === undefined")).ShouldBeTrue();
    }

    [Fact]
    public async Task APatchIsSentInChunks_InOneTask_WithoutSplittingASurrogatePair()
    {
        await AttachAsync($$"""[{"kind":"paragraph","children":[{{Text("a")}}]}]""");

        // The calls are counted in the same task as the edit, so none of them waited for another.
        var result = await Page.EvaluateAsync<int[]>("""
            () => {
                const { element } = window.lab;
                const transport = element.richContentSurface.transport;
                const chunks = [];
                transport.invoke = (sequence, index, count, chunk) => {
                    chunks.push(chunk);
                    return new Promise(() => {});
                };
                element.firstChild.textContent = '\u{1F600}'.repeat(5000) + 'x' + '\u{1F600}'.repeat(5000);
                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
                const split = chunks.filter(c => /[\uD800-\uDBFF]$/.test(c) || /^[\uDC00-\uDFFF]/.test(c)).length;
                const whole = JSON.parse(chunks.join('')).changed[0].node.children[0].text;
                return [chunks.length, Math.max(...chunks.map(c => c.length)), split, whole.length];
            }
            """);

        result[0].ShouldBeGreaterThan(4);
        result[1].ShouldBeInRange(3999, 4000);
        result[2].ShouldBe(0);
        result[3].ShouldBe(20001);
    }

    [Fact]
    public async Task AnInlineSurface_ReadsInlineContent_AndSendsAllOfItWhenItChanges()
    {
        var nodes = $$"""[{{Text("a ")}},{"kind":"strong","children":[{{Text("b")}}]}]""";
        await AttachAsync(nodes, profile: "inline");
        (await ReadAsync()).ShouldBe(Canonical(nodes));

        await Page.EvaluateAsync("() => { const e = window.lab.element; e.dispatchEvent(new InputEvent('input', { bubbles: true })); }");
        (await Page.EvaluateAsync<int>("() => window.patches.length")).ShouldBe(0);
        await Page.EvaluateAsync("() => { const e = window.lab.element; e.append(document.createElement('div')); e.lastChild.textContent = ' c'; e.dispatchEvent(new InputEvent('input', { bubbles: true })); }");

        var patch = JsonNode.Parse(await Page.EvaluateAsync<string>("() => JSON.stringify(window.patches[0])"))!;
        patch["generation"]!.GetValue<int>().ShouldBe(1);
        patch["order"]!.AsArray().Count.ShouldBe(3);
        patch["changed"]!.AsArray().Select(entry => (string?)entry!["node"]!["kind"]).ShouldBe(["text", "strong", "text"]);
    }

    [Fact]
    public async Task ARedrawSentAgain_IsIgnored_AndSyncAnswersWithTheGenerationHeld()
    {
        await AttachAsync($$"""[{"kind":"paragraph","children":[{{Text("a")}}]}]""");

        var held = await Page.EvaluateAsync<int?[]>("""
            () => {
                const { module, element } = window.lab;
                const other = text => [{ key: 's9', node: { kind: 'paragraph', children: [{ kind: 'text', text }] } }];
                element.firstChild.textContent = 'typed since';
                // The same generation again: what has been typed since stays.
                const again = module.sync(element, other('again'), false, true, 1);
                const afterAgain = element.textContent;
                // While composing, a newer one waits, and the answer says it has not been drawn.
                element.dispatchEvent(new CompositionEvent('compositionstart'));
                const waiting = module.sync(element, other('newer'), false, true, 2);
                const whileWaiting = element.textContent;
                element.dispatchEvent(new CompositionEvent('compositionend'));
                const drawn = module.sync(element, null, true, false, 2);
                window.shown = [afterAgain, whileWaiting, element.textContent, element.contentEditable];
                element.remove();
                const gone = module.sync(null, null, false, false, 2);
                return [again, waiting, drawn, gone];
            }
            """);

        held.ShouldBe([1, 1, 2, null]);
        (await Page.EvaluateAsync<string[]>("() => window.shown")).ShouldBe(["typed since", "typed since", "newer", "false"]);
    }

    [Fact]
    public async Task ADisposedSurface_SendsNothingMore()
    {
        await AttachAsync($$"""[{"kind":"paragraph","children":[{{Text("a")}}]}]""");

        await Page.EvaluateAsync("""
            () => {
                const { module, element } = window.lab;
                element.remove();
                // A removed element cannot be passed from .NET, which passes null for it.
                module.dispose(null);
                element.firstChild.textContent = 'b';
                element.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
            """);

        (await Page.EvaluateAsync<bool>("() => window.patches.length === 0 && window.lab.element.richContentSurface === undefined")).ShouldBeTrue();
    }
}
