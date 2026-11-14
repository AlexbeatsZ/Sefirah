using System.Text;

namespace Sefirah.Services.Socket;

/// <summary>Frames UTF-8 on byte boundaries, decoding each complete line exactly once.</summary>
public sealed class Utf8LineBuffer
{
    private readonly MemoryStream pending = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxLineBytes = 16 * 1024 * 1024;

    public List<string> Append(ReadOnlySpan<byte> bytes)
    {
        lock (pending)
        {
            List<string> lines = [];
            while (!bytes.IsEmpty)
            {
                var newline = bytes.IndexOf((byte)'\n');
                var count = newline < 0 ? bytes.Length : newline;
                if (pending.Length + count > MaxLineBytes) throw new InvalidDataException("Message exceeds frame limit");
                pending.Write(bytes[..count]);
                if (newline < 0) break;
                lines.Add(Utf8.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimEnd('\r'));
                pending.SetLength(0);
                bytes = bytes[(newline + 1)..];
            }
            return lines;
        }
    }
}
