using Microsoft.UI.Xaml.Markup;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Sefirah.Helpers;


[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class ResourceString : MarkupExtension
{
    private static readonly ResourceLoader resourceLoader = new();

    public string Name { get; set; } = string.Empty;

    protected override object ProvideValue()
    {
        try
        {
            // resw keys use dots ("Connected.Text") which MRT compiles into
            // slash-separated subtrees ("Connected/Text"); normalize before lookup.
            var value = resourceLoader.GetString(Name.Replace('.', '/'));
            return string.IsNullOrEmpty(value) ? Name : value;
        }
        catch (Exception)
        {
            // ResourceLoader.GetString throws for unknown keys; fall back to the key
            // so a missing translation can never break the page load.
            return Name;
        }
    }
}

