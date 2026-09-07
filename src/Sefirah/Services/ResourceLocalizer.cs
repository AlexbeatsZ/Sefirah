using System.Globalization;
#if WINDOWS
using Microsoft.Windows.ApplicationModel.Resources;
#else
using Windows.ApplicationModel.Resources;
#endif

namespace Sefirah.Services;

/// <summary>
/// Microsoft.Extensions.Localization adapter over the packaged resource map (.resw),
/// replacing the localization provider previously supplied by Uno.Extensions.
/// </summary>
public sealed class ResourceLocalizer : IStringLocalizer
{
#if WINDOWS
    private static readonly ResourceLoader resourceLoader = new();
#else
    private static ResourceLoader? resourceLoader;
    private static bool resourceLoaderFailed;
    private static ResourceLoader? GetResourceLoader()
    {
        if (resourceLoaderFailed) return null;
        if (resourceLoader is not null) return resourceLoader;
        try
        {
            resourceLoader = ResourceLoader.GetForViewIndependentUse();
            return resourceLoader;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] ResourceLoader initialization failed: {ex.Message}");
            resourceLoaderFailed = true;
            return null;
        }
    }
#endif

    public LocalizedString this[string name]
    {
        get
        {
            var value = Lookup(name);
            return new LocalizedString(name, string.IsNullOrEmpty(value) ? name : value, resourceNotFound: string.IsNullOrEmpty(value));
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var value = Lookup(name);
            var formatted = string.IsNullOrEmpty(value) ? name : string.Format(value, arguments);
            return new LocalizedString(name, formatted, resourceNotFound: string.IsNullOrEmpty(value));
        }
    }

    // ResourceLoader.GetString throws COMException for missing keys; callers such as
    // GetLocalizedResource expect a graceful fallback to the key like Uno's localizer.
    // resw keys use dots ("Connected.Text") which MRT compiles into slash-separated
    // subtrees ("Connected/Text"), so normalize dots before lookup.
    private static string Lookup(string name)
    {
        try
        {
            var normalized = name.Replace('.', '/');
#if WINDOWS
            return resourceLoader.GetString(normalized);
#else
            var loader = GetResourceLoader();
            return loader?.GetString(normalized) ?? string.Empty;
#endif
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];

    public IStringLocalizer WithCulture(CultureInfo? culture) => this;
}
