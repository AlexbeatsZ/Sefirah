using System.Runtime.InteropServices;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// Reads and writes the parts of the general NSPasteboard that Uno's macOS clipboard does not cover:
/// copied file URLs, files to paste in Finder, and image formats Android cannot decode.
/// </summary>
public static class MacClipboardHelper
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const nuint NSBitmapImageFileTypePNG = 4;

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nuint arg1, nint arg2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, byte[] bytes, nuint length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint arg1, nint arg2);

    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);
    private static nint GeneralPasteboard => Send(Class("NSPasteboard"), Sel("generalPasteboard"));
    private static nint NSString(string value) => Send(Class("NSString"), Sel("stringWithUTF8String:"), value);
    private static string? ToManagedString(nint nsString) =>
        nsString == nint.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    /// <summary>
    /// Increments on every pasteboard write, including the ones made here.
    /// </summary>
    public static long ChangeCount => Send(GeneralPasteboard, Sel("changeCount"));

    /// <summary>
    /// Path of the first file copied in Finder. Uno drops it whenever the pasteboard
    /// also carries image data, which Finder adds for the file icon.
    /// </summary>
    public static string? GetCopiedFilePath()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var urlString = Send(GeneralPasteboard, Sel("stringForType:"), NSString("public.file-url"));
            if (urlString == nint.Zero) return null;

            // Finder may hand out file reference URLs (file:///.file/id=...)
            var url = Send(Class("NSURL"), Sel("URLWithString:"), urlString);
            var fileUrl = url == nint.Zero ? nint.Zero : Send(url, Sel("filePathURL"));
            return fileUrl == nint.Zero ? null : ToManagedString(Send(fileUrl, Sel("path")));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Re-encodes any image format ImageIO can read (TIFF, HEIC, ...) as PNG.
    /// </summary>
    public static byte[]? ConvertToPng(byte[] imageData)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var png = EncodePng(imageData);
            if (png == nint.Zero) return null;

            var length = (int)Send(png, Sel("length"));
            var result = new byte[length];
            Marshal.Copy(Send(png, Sel("bytes")), result, 0, length);
            return result;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Puts an image on the pasteboard as PNG so it can be pasted into any app.
    /// </summary>
    /// <returns>The pasteboard change count after the write, or -1 if the image could not be decoded.</returns>
    public static long WriteImage(byte[] imageData)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var png = EncodePng(imageData);
            if (png == nint.Zero) return -1;

            var pasteboard = GeneralPasteboard;
            Send(pasteboard, Sel("clearContents"));
            SendBool(pasteboard, Sel("setData:forType:"), png, NSString("public.png"));
            return Send(pasteboard, Sel("changeCount"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Puts a file on the pasteboard the way Finder's Copy does.
    /// </summary>
    /// <returns>The pasteboard change count after the write.</returns>
    public static long WriteFile(string path)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
            var items = Send(Class("NSArray"), Sel("arrayWithObject:"), url);

            var pasteboard = GeneralPasteboard;
            Send(pasteboard, Sel("clearContents"));
            SendBool(pasteboard, Sel("writeObjects:"), items);
            return Send(pasteboard, Sel("changeCount"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint EncodePng(byte[] imageData)
    {
        var data = Send(Class("NSData"), Sel("dataWithBytes:length:"), imageData, (nuint)imageData.Length);
        var rep = Send(Class("NSBitmapImageRep"), Sel("imageRepWithData:"), data);
        if (rep == nint.Zero) return nint.Zero;

        var properties = Send(Class("NSDictionary"), Sel("dictionary"));
        return Send(rep, Sel("representationUsingType:properties:"), NSBitmapImageFileTypePNG, properties);
    }
}
