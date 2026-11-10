using Sefirah.Extensions;
using Sefirah.Utils;

namespace Sefirah.Actions.Power;

public sealed class PowerSettings
{
    public PowerKind Kind { get; set; } = PowerKind.Sleep;
}

public sealed partial class PowerAction(ActionItem item) : IAction, IActionSettings
{
    public static ActionMetadata Metadata { get; } = new(
        "Power",
        "PowerButton",
        AskForConfirmationByDefault: true);

    public string DefaultIcon => GetIcon(item.Get<PowerSettings>().Kind);

    public bool IsValid => true;

    public UIElement CreateSettingPanel() => new PowerActionSettings(item);

    public static string GetIcon(PowerKind kind) => kind switch
    {
        PowerKind.Lock => "Lock",
        PowerKind.LogOff => "SignOut",
        PowerKind.Sleep => "QuietHours",
        PowerKind.Hibernate => "Recent",
        PowerKind.Restart => "UpdateRestore",
        PowerKind.Shutdown => "PowerButton",
        _ => "PowerButton",
    };

    public static string GetName(PowerKind kind) => kind.ToString().GetLocalizedResource();

    // English names seeded by current and earlier releases. A stored name that still equals one
    // of these was never customized, so it follows the app language instead of staying English.
    private static readonly Dictionary<PowerKind, string[]> builtInNames = new()
    {
        [PowerKind.Lock] = ["Lock", "Lock Screen"],
        [PowerKind.LogOff] = ["Log off", "Log Off", "Logoff", "Sign out"],
        [PowerKind.Sleep] = ["Sleep"],
        [PowerKind.Hibernate] = ["Hibernate"],
        [PowerKind.Restart] = ["Restart"],
        [PowerKind.Shutdown] = ["Shutdown", "Shut down"],
    };

    /// <summary>Name shown to peers: localized for untouched built-in names, otherwise the user's name.</summary>
    public static string GetDisplayName(ActionItem item)
    {
        if (item.ActionId != Metadata.ActionId) return item.Name;
        var kind = item.Get<PowerSettings>().Kind;
        return builtInNames.TryGetValue(kind, out var names) && names.Contains(item.Name, StringComparer.OrdinalIgnoreCase)
            ? GetName(kind)
            : item.Name;
    }
}
