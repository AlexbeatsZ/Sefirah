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

Console.WriteLine("Remote storage path policy regression: PASS");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
