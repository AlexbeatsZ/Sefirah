using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Uno.UI;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// Registers the bundled Fluent icon font with CoreText for this process.
/// </summary>
public static class MacSymbolFontHelper
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";

    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateFromFileSystemRepresentation(
        nint allocator,
        byte[] buffer,
        nint bufferLength,
        [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);

    [DllImport(CoreText)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CTFontManagerRegisterFontsForURL(nint fontUrl, uint scope, out nint error);

    public static void RegisterFluentSymbols()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var fontPath = Path.Combine(
                AppContext.BaseDirectory,
                "Uno.Fonts.Fluent",
                "Fonts",
                "uno-fluentui-assets.ttf");
            if (!File.Exists(fontPath))
            {
                Console.Error.WriteLine($"[WARN] Fluent symbol font is missing: {fontPath}");
                return;
            }

            var pathBytes = Encoding.UTF8.GetBytes(fontPath);
            var fontUrl = CFURLCreateFromFileSystemRepresentation(nint.Zero, pathBytes, pathBytes.Length, false);
            if (fontUrl == nint.Zero)
            {
                Console.Error.WriteLine($"[WARN] Could not create a CoreText URL for: {fontPath}");
                return;
            }

            try
            {
                if (!CTFontManagerRegisterFontsForURL(fontUrl, 1, out var error))
                {
                    if (error != nint.Zero)
                        CFRelease(error);
                    Console.Error.WriteLine("[WARN] CoreText rejected the bundled Fluent symbol font.");
                    return;
                }
            }
            finally
            {
                CFRelease(fontUrl);
            }

            using var typeface = SKTypeface.FromFamilyName("Symbols");
            using var font = typeface is null ? null : new SKFont(typeface);
            if (typeface is null || font!.GetGlyph(0xE713) == 0)
            {
                Console.Error.WriteLine("[WARN] CoreText registered the Fluent font, but Skia could not resolve its glyphs.");
                return;
            }

            // Use the process-registered family instead of Uno's stream-backed ms-appx
            // typeface. On macOS, that stream-backed route can fall back while drawing
            // private-use glyphs even after layout resolved the intended Symbols typeface.
            FeatureConfiguration.Font.SymbolsFont = "Symbols";
            Console.WriteLine("[DEBUG] Registered Fluent symbol font with CoreText for this process.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Unable to register the Fluent symbol font: {ex}");
        }
    }
}
