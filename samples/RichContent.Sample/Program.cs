using RichContent.Sample;
using RichContent.Sample.Components;
using SyntaxCircus.Blazor.RichContent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<IRichContentMediaResolver, SampleMediaResolver>();
builder.Services.AddRichContent(
    blocks => blocks.Add("project-card", "Project card", typeof(ProjectCardView), typeof(ProjectCardEditor),
        () => System.Text.Json.JsonDocument.Parse("""{"name":"New project"}""").RootElement.Clone()),
    editor => editor.MediaPicker = typeof(SampleMediaPicker));

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/img/{id}.svg", (string id) => Results.Content(
    $"""<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200"><rect width="100%" height="100%" fill="#cdd9ee"/><text x="50%" y="50%" text-anchor="middle" font-size="24">{System.Net.WebUtility.HtmlEncode(id)}</text></svg>""",
    "image/svg+xml"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
