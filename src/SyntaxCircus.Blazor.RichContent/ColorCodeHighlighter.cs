using ColorCode;
using Microsoft.AspNetCore.Components;

namespace SyntaxCircus.Blazor.RichContent;

/// <summary>
/// Server-side highlighting with ColorCode. Emits CSS classes (no inline styles), so the host app themes the colours.
/// </summary>
public sealed class ColorCodeHighlighter : ICodeHighlighter
{
    private static readonly Dictionary<string, ILanguage> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = Languages.CSharp,
        ["csharp"] = Languages.CSharp,
        ["js"] = Languages.JavaScript,
        ["javascript"] = Languages.JavaScript,
        ["ts"] = Languages.Typescript,
        ["typescript"] = Languages.Typescript,
        ["html"] = Languages.Html,
        ["xml"] = Languages.Xml,
        ["css"] = Languages.Css,
        ["sql"] = Languages.Sql,
        ["powershell"] = Languages.PowerShell,
        ["ps1"] = Languages.PowerShell,
        ["python"] = Languages.Python,
        ["py"] = Languages.Python,
        ["java"] = Languages.Java,
        ["cpp"] = Languages.Cpp,
        ["fsharp"] = Languages.FSharp,
        ["php"] = Languages.Php,
    };

    public MarkupString? Highlight(string code, string? language)
    {
        if (string.IsNullOrEmpty(code) || language is null || !Map.TryGetValue(language, out var lang))
        {
            return null;
        }

        try
        {
            var formatter = new HtmlClassFormatter();
            return new MarkupString(Unwrap(formatter.GetHtmlString(code, lang)));
        }
        catch (Exception)
        {
            // Highlighting is decoration; a parser failure falls back to escaped plain text.
            return null;
        }
    }

    // ColorCode wraps its output in <div ...><pre>...</pre></div>; the renderer supplies its own pre/code.
    private static string Unwrap(string html)
    {
        var start = html.IndexOf("<pre", StringComparison.Ordinal);
        if (start >= 0)
        {
            var open = html.IndexOf('>', start);
            var end = html.LastIndexOf("</pre>", StringComparison.Ordinal);
            if (open >= 0 && end > open)
            {
                return html[(open + 1)..end];
            }
        }

        return html;
    }
}
