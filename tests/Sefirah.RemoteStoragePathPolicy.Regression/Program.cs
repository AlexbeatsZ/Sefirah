using Sefirah.Platforms.Windows.Helpers;

Assert(!FileHelper.IsSystemDirectory("Android/data"), "Android/data must be visible.");
Assert(
    !FileHelper.IsSystemDirectory("/Android/data/com.example/files"),
    "Files beneath Android/data must be visible.");
Assert(
    !FileHelper.IsSystemDirectory("android/DATA/com.example/cache"),
    "Android/data matching must not depend on case.");
Assert(FileHelper.IsSystemDirectory("Android/obb"), "Android/obb must remain hidden.");
Assert(
    FileHelper.IsSystemDirectory("/Android/obb/com.example"),
    "Files beneath Android/obb must remain hidden.");

var shortPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sefirah-short-path"));
Assert(
    LongPath.EnsureExtendedPrefix(shortPath) == shortPath,
    "A short absolute path was unexpectedly rewritten.");

var longPath = Path.Combine(Path.GetTempPath(), new string('a', 280));
Assert(
    LongPath.EnsureExtendedPrefix(longPath).StartsWith(@"\\?\", StringComparison.Ordinal),
    "A path beyond MAX_PATH did not receive the Win32 extended-length prefix.");

const string extendedPath = @"\\?\C:\already-extended";
Assert(
    LongPath.EnsureExtendedPrefix(extendedPath) == extendedPath,
    "An existing extended-length prefix was changed.");

Console.WriteLine("Remote storage path policy regression: PASS");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
