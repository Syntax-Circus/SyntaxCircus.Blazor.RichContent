using Microsoft.AspNetCore.Components;

namespace SyntaxCircus.Blazor.RichContent;

public interface ICodeHighlighter
{
    /// <summary>Returns highlighted inner markup for a <c>code</c> element, or <c>null</c> when the language is not supported.</summary>
    MarkupString? Highlight(string code, string? language);
}
