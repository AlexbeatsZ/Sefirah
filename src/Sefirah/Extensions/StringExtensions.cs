using System.Collections.Concurrent;

namespace Sefirah.Extensions;

/// <summary>
/// Extension methods for working with localized resources and message formatting.
/// </summary>
public static class StringExtensions
{
    private static IStringLocalizer? _stringLocalizer;
    private static IStringLocalizer? StringLocalizer =>
        _stringLocalizer ??= Ioc.Default?.GetService<IStringLocalizer>();

    /// <summary>
    /// Retrieves a localized resource string from the resource map.
    /// </summary>
    /// <param name="resourceKey">The key for the resource string.</param>
    /// <returns>The localized resource string.</returns>
    public static string GetLocalizedResource(this string resourceKey)
    {
        try
        {
            var localizer = StringLocalizer;
            if (localizer is not null)
            {
                var localized = localizer[resourceKey];
                if (!string.IsNullOrEmpty(localized?.Value))
                    return localized.Value;
            }
        }
        catch
        {
            // fallback to key
        }
        return resourceKey;
    }
}
