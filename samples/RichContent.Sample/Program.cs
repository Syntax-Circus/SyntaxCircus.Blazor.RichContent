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

// Off by default. The browser tests turn it on to show the editor needs no inline script, eval or other origin.
var strictCsp = app.Configuration.GetValue<bool>("Sample:StrictCsp");
if (strictCsp)
{
    const string policy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:";
    app.Use((context, next) =>
    {
        context.Response.Headers.Append("Content-Security-Policy", policy);
        return next(context);
    });
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/img/{id}.svg", (string id) => Results.Content(
    $"""<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200"><rect width="100%" height="100%" fill="#cdd9ee"/><text x="50%" y="50%" text-anchor="middle" font-size="24">{System.Net.WebUtility.HtmlEncode(id)}</text></svg>""",
    "image/svg+xml"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
{
    // Blazor adds its own frame-ancestors policy header. The strict mode must send only the header above:
    // Playwright's WebKit on Windows keeps just the last header of a name, which would drop the strict policy.
    if (strictCsp)
    {
        options.ContentSecurityFrameAncestorsPolicy = null;
    }
});

app.Run();
