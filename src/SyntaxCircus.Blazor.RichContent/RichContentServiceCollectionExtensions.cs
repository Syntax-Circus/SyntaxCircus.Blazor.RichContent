using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Blazor.RichContent.Editing;

namespace SyntaxCircus.Blazor.RichContent;

public static class RichContentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the extension block registry, the editor options and the default code highlighter.
    /// The app must also register an <see cref="IRichContentMediaResolver"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="blocks">Registers app-defined extension blocks.</param>
    /// <param name="editor">Configures the editor components, e.g. the media picker.</param>
    public static IServiceCollection AddRichContent(
        this IServiceCollection services,
        Action<ContentBlockRegistry>? blocks = null,
        Action<RichContentEditorOptions>? editor = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registry = new ContentBlockRegistry();
        blocks?.Invoke(registry);
        services.AddSingleton(registry);

        var options = new RichContentEditorOptions();
        editor?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton<ICodeHighlighter, ColorCodeHighlighter>();

        // Hosts without AddRazorComponents (e.g. a bare HtmlRenderer for RSS) have no boundary logger,
        // and a failing extension renderer would otherwise throw while the ErrorBoundary is constructed.
        services.TryAddSingleton<IErrorBoundaryLogger, LoggingErrorBoundaryLogger>();
        return services;
    }

    private sealed class LoggingErrorBoundaryLogger(IServiceProvider services) : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception)
        {
            services.GetService<ILoggerFactory>()
                ?.CreateLogger<LoggingErrorBoundaryLogger>()
                .LogError(exception, "A rich content block failed to render.");
            return ValueTask.CompletedTask;
        }
    }
}
