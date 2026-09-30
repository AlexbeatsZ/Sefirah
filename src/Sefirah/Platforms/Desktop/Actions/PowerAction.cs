using Sefirah.Actions.Power;
using Sefirah.Utils;

namespace Sefirah.Actions.Power;

public sealed partial class PowerAction
{
    public Task ExecuteAsync()
    {
        var kind = item.Get<PowerSettings>().Kind;
        var logger = Ioc.Default.GetRequiredService<ILogger>();

        return Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    ExecuteMacPowerAction(kind, logger);
                    return;
                }
                switch (kind)
                {
                    case PowerKind.Lock:
                        Run("loginctl", "lock-session");
                        break;
                    case PowerKind.LogOff:
                        LogOff();
                        break;
                    case PowerKind.Sleep:
                        Run("systemctl", "suspend");
                        break;
                    case PowerKind.Hibernate:
                        Run("systemctl", "hibernate");
                        break;
                    case PowerKind.Restart:
                        Run("shutdown", "-r now");
                        break;
                    case PowerKind.Shutdown:
                        Run("shutdown", "-h now");
                        break;
                    default:
                        logger.Warn($"Unhandled power kind: {kind}");
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Error executing power kind {kind}", ex);
            }
        });
    }

    private static void LogOff()
    {
        var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            Run("loginctl", $"terminate-session {sessionId}");
            return;
        }

        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
        {
            Run("loginctl", $"terminate-user {user}");
        }
    }

    private static void ExecuteMacPowerAction(PowerKind kind, ILogger logger)
    {
        if (kind == PowerKind.Lock)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", "tell application \"System Events\" to key code 12 using {control down, command down}" },
                UseShellExecute = false
            });
            return;
        }
        if (kind == PowerKind.Sleep)
        {
            Run("/usr/bin/pmset", "sleepnow");
            return;
        }
        string? script = kind switch
        {
            PowerKind.LogOff => "tell application \"System Events\" to log out",
            PowerKind.Restart => "tell application \"System Events\" to restart",
            PowerKind.Shutdown => "tell application \"System Events\" to shut down",
            _ => null
        };
        if (script is null)
        {
            logger.Warn($"Power action {kind} has no supported macOS equivalent");
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList = { "-e", script },
            UseShellExecute = false
        });
    }

    private static void Run(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
