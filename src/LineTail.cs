using System;
using System.Buffers;
using System.IO;

namespace AIAnnounce;

delegate void LineHandler(ReadOnlySpan<byte> line);

// Follows a file that only grows (a Codex rollout, an agy log). Each Read hands over the complete
// lines appended since the previous one; a partial last line waits for its newline.
sealed class LineTail
{
    const int ChunkSize = 1 << 20;
    const int MaxLine = 64 << 20;

    long offset;
    bool skipping;
    MemoryStream carry = new();

    public LineTail(string path) => Path = path;

    public string Path { get; }

    // False once the file is gone. A file that's locked for a moment just yields nothing this time.
    public bool Read(LineHandler onLine)
    {
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length < offset)
            {
                // truncated or replaced: start over
                offset = 0;
                skipping = false;
                carry.SetLength(0);
            }
            if (length == offset) return true;
            fs.Position = offset;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                while (offset < length)
                {
                    int n = fs.Read(buffer, 0, (int)Math.Min(ChunkSize, length - offset));
                    if (n <= 0) break;
                    offset += n;
                    int start = 0;
                    while (start < n)
                    {
                        int nl = Array.IndexOf(buffer, (byte)'\n', start, n - start);
                        if (nl < 0) break;
                        EndLine(buffer.AsSpan(start, nl - start), onLine);
                        start = nl + 1;
                    }
                    Carry(buffer.AsSpan(start, n - start));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    void EndLine(ReadOnlySpan<byte> tail, LineHandler onLine)
    {
        if (skipping)
        {
            skipping = false;
            carry.SetLength(0);
            return;
        }
        if (carry.Length == 0)
        {
            Emit(tail, onLine);
            return;
        }
        carry.Write(tail);
        Emit(carry.GetBuffer().AsSpan(0, (int)carry.Length), onLine);
        // one tail per live session, idle most of the time: don't sit on a big buffer
        if (carry.Capacity > 256 << 10) carry = new MemoryStream();
        else carry.SetLength(0);
    }

    static void Emit(ReadOnlySpan<byte> line, LineHandler onLine)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.Length > 0) onLine(line);
    }

    void Carry(ReadOnlySpan<byte> rest)
    {
        if (skipping || rest.Length == 0) return;
        if (carry.Length + rest.Length > MaxLine)
        {
            // nothing we read is this long; drop it up to its newline
            skipping = true;
            carry = new MemoryStream();
            return;
        }
        carry.Write(rest);
    }
}
