using System.Text;

namespace Sefirah.Services.Transfer;

/// <summary>Accepts line-delimited controls and legacy peers that omit the newline.</summary>
public sealed class TransferControlBuffer
{
    private string pending = string.Empty;
    public List<string> Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 4096) throw new InvalidDataException("Invalid transfer controls");
        pending += Encoding.ASCII.GetString(bytes);
        List<string> messages = [];
        while (pending.Length > 0)
        {
            pending = pending.TrimStart('\r', '\n');
            if (pending.Length == 0) break;
            var token = new[] { "start", "complete" }.FirstOrDefault(value => pending.StartsWith(value, StringComparison.Ordinal));
            if (token is null)
            {
                if ("start".StartsWith(pending, StringComparison.Ordinal) || "complete".StartsWith(pending, StringComparison.Ordinal)) break;
                throw new InvalidDataException("Invalid transfer control");
            }
            messages.Add(token);
            pending = pending[token.Length..];
        }
        return messages;
    }
}

public static class TransferFiles
{
    public static void ValidateName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\', ':']) >= 0 || fileName.Any(char.IsControl) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid remote file name");
    }

    public static FileStream CreateUnique(string directory, string fileName)
    {
        ValidateName(fileName);
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; ; index++)
        {
            var name = index == 0 ? fileName : $"{stem} ({index}){extension}";
            var path = Path.Combine(directory, name);
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
