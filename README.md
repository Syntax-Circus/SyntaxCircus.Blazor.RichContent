# SyntaxCircus.Blazor.RichContent

Block-based rich content for Blazor apps, shipped as two NuGet packages that share one version:

- **SyntaxCircus.RichContent** - the framework-free document model (`ContentDocument`, blocks, rich text nodes) and its
  versioned JSON serializer. Unknown block types survive a read/write round trip untouched.
- **SyntaxCircus.Blazor.RichContent** - Blazor components for rendering (and later editing) that content.

## Quick start

```csharp
var json = ContentDocumentSerializer.Serialize(ContentDocument.Empty("b1"));
var result = ContentDocumentSerializer.Deserialize(json); // result.Document or result.Error
```
