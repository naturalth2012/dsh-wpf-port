using System.IO;
using System.Text;
using ZstdNet;

namespace Dsh.Viewer;

/// <summary>
/// L1 disk-format decode: transparently reads a session log, decompressing <c>.zstd</c> files
/// via <see cref="ZstdNet.DecompressionStream"/> and reading plain UTF-8 otherwise. Returns one
/// line at a time (the JSONL record), with the caller responsible for JSON parsing.
/// </summary>
public static class ZstdReader
{
    /// <summary>Read lines of a session file, transparently decompressing if it's zstd.</summary>
    public static IEnumerable<string> ReadLines(string path, bool isZstd)
    {
        if (isZstd)
        {
            using var file = File.OpenRead(path);
            using var decompress = new DecompressionStream(file);
            using var reader = new StreamReader(decompress, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                yield return line;
            }
        }
        else
        {
            foreach (var line in File.ReadLines(path)) yield return line;
        }
    }
}
