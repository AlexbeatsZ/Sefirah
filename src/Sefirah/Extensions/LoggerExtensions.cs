namespace Sefirah.Extensions;

/// <summary>
/// Uno.Extensions.Logging-style shorthand extensions over
/// Microsoft.Extensions.Logging.Kept so the many call sites
/// that use logger.Info/Error/Debug keep working unchanged.
/// </summary>
public static class LoggerExtensions
{
    public static void Trace(this ILogger logger, string message, params object?[] args)
        => logger.LogTrace(message, args);

    public static void Debug(this ILogger logger, string message, params object?[] args)
        => logger.LogDebug(message, args);

    public static void Debug(this ILogger logger, string message, Exception exception)
        => logger.LogDebug(exception, message);

    public static void Info(this ILogger logger, string message, params object?[] args)
        => logger.LogInformation(message, args);

    public static void Info(this ILogger logger, string message, Exception exception)
        => logger.LogInformation(exception, message);

    public static void Warning(this ILogger logger, string message, params object?[] args)
        => logger.LogWarning(message, args);

    public static void Warning(this ILogger logger, string message, Exception exception)
        => logger.LogWarning(exception, message);

    public static void Warn(this ILogger logger, string message, params object?[] args)
        => logger.LogWarning(message, args);

    public static void Warn(this ILogger logger, string message, Exception exception)
        => logger.LogWarning(exception, message);

    public static void Error(this ILogger logger, string message, params object?[] args)
        => logger.LogError(message, args);

    public static void Error(this ILogger logger, string message, Exception exception)
        => logger.LogError(exception, message);

    public static void Critical(this ILogger logger, string message, params object?[] args)
        => logger.LogCritical(message, args);

    public static void Critical(this ILogger logger, string message, Exception exception)
        => logger.LogCritical(exception, message);
}
