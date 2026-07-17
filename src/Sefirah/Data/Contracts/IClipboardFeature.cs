using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface IClipboardFeature : IFeature
{
    Task SetContentAsync(ClipboardInfo clipboard, PairedDevice sourceDevice);

    /// <summary>
    /// Sets the content of the clipboard.
    /// </summary>
    Task SetContentAsync(object content, PairedDevice sourceDevice);
}
