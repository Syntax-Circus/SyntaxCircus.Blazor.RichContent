namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>A surface's or a card's place in a writing block, as what edits it sees it.</summary>
internal interface IWritingPart
{
    /// <summary>True once the writing block has rewritten or removed this place: nothing sent from it can be stored.</summary>
    bool IsGone { get; }
}
