using Sefirah.Data.Enums;
using Sefirah.Services;

AssertEqual(
    InitialWindowState.Default,
    StartupWindowPolicy.Resolve(isLoginStartup: false, StartupOptions.InTray),
    "A manual launch must show the main window even when login startup uses the tray.");

AssertEqual(
    InitialWindowState.Hidden,
    StartupWindowPolicy.Resolve(isLoginStartup: true, StartupOptions.InTray),
    "A login launch configured for the tray must not show the main window.");

AssertEqual(
    InitialWindowState.Minimized,
    StartupWindowPolicy.Resolve(isLoginStartup: true, StartupOptions.Minimized),
    "A login launch configured as minimized must preserve that explicit choice.");

AssertEqual(
    InitialWindowState.Maximized,
    StartupWindowPolicy.Resolve(isLoginStartup: true, StartupOptions.Maximized),
    "A login launch configured as maximized must preserve that explicit choice.");

AssertEqual(
    InitialWindowState.Default,
    StartupWindowPolicy.Resolve(isLoginStartup: true, StartupOptions.Disabled),
    "A stale login-startup argument must not hide the app after startup is disabled.");

Console.WriteLine("macOS lifecycle policy regression: PASS");

static void AssertEqual<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
}
