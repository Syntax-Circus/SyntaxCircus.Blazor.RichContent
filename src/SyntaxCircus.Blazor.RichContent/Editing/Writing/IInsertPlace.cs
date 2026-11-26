namespace SyntaxCircus.Blazor.RichContent.Editing.Writing;

/// <summary>Where Insert puts things for one writing surface.</summary>
internal interface IInsertPlace
{
    IReadOnlyList<InsertChoice> Choices { get; }

    /// <summary>
    /// Inserts a choice after <paramref name="nodesBefore"/> of the surface's nodes. A choice that does not exist
    /// or is not available does nothing.
    /// </summary>
    Task InsertAsync(int choice, int nodesBefore);

    /// <summary>Tells the author why something asked for at this surface was not done.</summary>
    void Refuse(string reason);
}
