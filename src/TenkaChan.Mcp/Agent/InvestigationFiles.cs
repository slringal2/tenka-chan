using System.Security.Cryptography;
using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

// Ported from PR #20. Private local persistence, not an OS sandbox or identity provider.
internal static class InvestigationFiles
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tenka-chan"));
    internal static string Id(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Expected an exact GUID in N format.");
        return id;
    }
    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Symlink/reparse paths are not supported by investigation storage.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static string DirectoryAt(string path)
    {
        NoLinks(path); Directory.CreateDirectory(path); NoLinks(path); return path;
    }
    internal static byte[] Bytes(string path, int limit = 4_000_000)
    {
        NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("Stored input exceeds its size limit.");
        using var bytes = new MemoryStream(); var block = new byte[8192];
        while (true)
        {
            var read = file.Read(block, 0, (int)Math.Min(block.Length, limit + 1L - bytes.Length));
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > limit) throw new InvalidDataException("Stored input exceeds its size limit.");
            bytes.Write(block, 0, read);
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(Bytes(path), Json)
        ?? throw new InvalidDataException("Stored record is invalid.");
    internal static void Write<T>(string path, T data)
    {
        NoLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        if (bytes.Length > 4_000_000) throw new InvalidDataException("Stored record budget exceeded.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            NoLinks(path); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static FileStream Lock(string directory, string name)
    {
        var path = Path.Combine(directory, name); NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("investigation_busy: Another operation owns this investigation. No action was queued or cancelled."); }
    }
}
