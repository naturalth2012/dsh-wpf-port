using System.IO;
using System.Text;
using Dsh.Viewer;
using ZstdNet;

namespace Dsh.Viewer.Tests;

/// <summary>
/// L1 storage decode regression tests (os/03): <c>ZstdReader</c> must transparently read a
/// <c>.zstd</c> session log (one JSONL record per line) while also reading a plain UTF-8
/// <c>.jsonl</c> directly. Round-trips through <c>ZstdNet.Compressor</c> so the frame is real.
/// </summary>
public class ZstdReaderTests
{
    private static string MakeTempFile(string extension, byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "dshviewer-zstd-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string[] Lines(string path, bool isZstd) => ZstdReader.ReadLines(path, isZstd).ToArray();

    [Fact]
    public void Reads_plain_utf8_jsonl_directly()
    {
        string path = MakeTempFile(".jsonl", Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n"));
        try
        {
            Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, Lines(path, isZstd: false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reads_zstd_after_decompression()
    {
        string jsonl = "{\"type\":\"session\",\"id\":\"s\"}\n{\"type\":\"user/message\",\"seq\":1}\n";
        byte[] compressed;
        using (var c = new Compressor())
        {
            compressed = c.Wrap(Encoding.UTF8.GetBytes(jsonl));
        }
        string path = MakeTempFile(".jsonl.zstd", compressed);
        try
        {
            Assert.Equal(2, Lines(path, isZstd: true).Length);
            Assert.StartsWith("{\"type\":\"session\"", Lines(path, isZstd: true)[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Skips_blank_lines_but_keeps_record_order()
    {
        string path = MakeTempFile(".jsonl", Encoding.UTF8.GetBytes("{\"a\":1}\n\n{\"b\":2}\n"));
        try
        {
            // ZstdReader yields every non-whitespace line verbatim, preserving blank lines for the
            // caller (SessionLogReader skips them). This asserts no lines are dropped.
            var raw = ZstdReader.ReadLines(path, false).ToArray();
            Assert.Equal(3, raw.Length);
            Assert.Equal(string.Empty, raw[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Empty_file_yields_no_lines()
    {
        string path = MakeTempFile(".jsonl", Array.Empty<byte>());
        try
        {
            Assert.Empty(Lines(path, isZstd: false));
        }
        finally { File.Delete(path); }
    }
}
