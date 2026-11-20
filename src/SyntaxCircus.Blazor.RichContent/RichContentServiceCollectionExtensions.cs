using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SyntaxCircus.Blazor.RichContent;

public static class RichContentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the extension block registry and the default code highlighter.
    /// The app must also register an <see cref="IRichContentMediaResolver"/>.
    /// </summary>
    public static IServiceCollection AddRichContent(this IServiceCollection services, Action<ContentBlockRegistry>? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registry = new ContentBlockRegistry();
        blocks?.Invoke(registry);
        services.AddSingleton(registry);
        services.TryAddSingleton<ICodeHighlighter, ColorCodeHighlighter>();
        return services;
    }
}
