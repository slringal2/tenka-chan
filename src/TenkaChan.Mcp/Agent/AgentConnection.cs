using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

public sealed record AgentConnectionFile(int Schema, string Endpoint, string Token, string InstanceId);

/// <summary>Local machine credential, never serialized to the browser. Not protection against the same OS user.</summary>
public sealed class AgentConnection
{
    public bool Enabled { get; }
    public bool Published { get; private set; }
    public bool AllowBuild { get; }
    public bool AllowExecution { get; }
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string FilePath { get; }
    private readonly int _port;
    private readonly string _instance;
    public AgentConnection(string instance, int port, bool? enabled = null, bool? allowBuild = null, bool? allowExecution = null, string? filePath = null)
    {
        _instance = instance; _port = port;
        Enabled = enabled ?? (Environment.GetEnvironmentVariable("TENKA_ENABLE_AGENT_GATEWAY") != "0");
        AllowBuild = allowBuild ?? (Environment.GetEnvironmentVariable("TENKA_AGENT_ALLOW_BUILD") == "1");
        AllowExecution = allowExecution ?? (Environment.GetEnvironmentVariable("TENKA_AGENT_ALLOW_EXECUTION") == "1");
        FilePath = Path.GetFullPath(filePath ?? Environment.GetEnvironmentVariable("TENKA_AGENT_CONNECTION_FILE") ?? DefaultPath(port));
    }
    public static string DefaultPath(int port) => Path.Combine(
        Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tenka-chan")),
        "agent-connections", port + ".json");

    public bool Authorize(string? authorization, bool hasOrigin, bool hasBrowserFetchHeaders)
    {
        if (!Enabled || hasOrigin || hasBrowserFetchHeaders || authorization is null ||
            !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var supplied = authorization[7..];
        return supplied.Length == _token.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(_token));
    }
    public void Publish()
    {
        if (!Enabled) return;
        var path = Path.GetFullPath(FilePath);
        var directory = Path.GetDirectoryName(path)!;
        // Do not follow reparse points into another location when publishing credentials.
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Agent connection directory cannot use symbolic links/junctions.");
            current = current.Parent;
        }
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Agent connection file cannot be a symbolic link.");
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(pending, options))
                JsonSerializer.Serialize(stream, new AgentConnectionFile(1, $"http://127.0.0.1:{_port}", _token, _instance), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.Move(pending, path, true);
            Published = true;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public void RemoveOwnFile()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 16000) return;
            var saved = JsonSerializer.Deserialize<AgentConnectionFile>(File.ReadAllText(FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (saved?.InstanceId == _instance) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
