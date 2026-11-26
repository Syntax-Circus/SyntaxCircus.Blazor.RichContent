using System.Text.Json;
using RichContent.Demo;
using RichContent.Demo.Components;
using SyntaxCircus.Blazor.RichContent;

var builder = WebApplication.CreateBuilder(args);

// Hub options stay at their defaults, so the demo has the message limits of a host that changes nothing.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<IRichContentMediaResolver, DemoMediaResolver>();
builder.Services.AddRichContent(
    blocks => blocks.Add("project-card", "Project card", typeof(ProjectCardView), typeof(ProjectCardEditor),
        () => JsonDocument.Parse("""{"name":"New project"}""").RootElement.Clone()),
    editor => editor.MediaPicker = typeof(DemoMediaPicker));

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();

// Stands in for a media library: every image id gets a labelled placeholder.
app.MapGet("/img/{id}.svg", (string id) => Results.Content(
    $"""<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200"><rect width="100%" height="100%" fill="#cdd9ee"/><text x="50%" y="50%" text-anchor="middle" font-size="24" fill="#1c2530">{System.Net.WebUtility.HtmlEncode(id)}</text></svg>""",
    "image/svg+xml"));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
