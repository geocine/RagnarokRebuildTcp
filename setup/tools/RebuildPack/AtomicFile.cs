using System.Text;

// Outputs are written to "<path>.rr.tmp", flushed to disk and renamed over the target, so a killed
// run or a power cut leaves the old file or the new one, never a torn one. Leftover temp files are
// never listed in a manifest, so the stale-file sweep removes them.
static class AtomicFile
{
    public const string TempSuffix = ".rr.tmp";

    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = path + TempSuffix;
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteText(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

    public static void Copy(string source, string path)
    {
        var temp = path + TempSuffix;
        File.Copy(source, temp, overwrite: true);
        using (var fs = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            fs.Flush(flushToDisk: true);
        File.Move(temp, path, overwrite: true);
    }
}
