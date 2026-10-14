using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Security;

public sealed record NativePathSelection(bool Cancelled, string? Path);
public sealed record NativeDialogFilter(string Title, string Filter, string[] Extensions, bool Folder = false);

/// <summary>Interactive Windows picker only. Never grants access or executes the selected file.</summary>
public sealed class NativePathDialog(WorkspaceGuard guard)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool Available => OperatingSystem.IsWindows() && Environment.UserInteractive
        && Environment.GetEnvironmentVariable("TENKA_ENABLE_NATIVE_DIALOGS") == "1";

    public static NativeDialogFilter FilterFor(string kind) => kind switch
    {
        "project" => new("プロジェクトを選択", "Solution / Project|*.sln;*.slnx;*.csproj", [".sln", ".slnx", ".csproj"]),
        "assembly" => new("DLL・EXEを選択", "Managed assembly|*.dll;*.exe", [".dll", ".exe"]),
        "pdb" => new("PDBを選択", "Symbols|*.pdb", [".pdb"]),
        "source" => new("C#ソースを選択", "C# source|*.cs", [".cs"]),
        "profile" => new("プロファイルJSONを選択", "JSON profile|*.json", [".json"]),
        "trace" => new("トレースを選択", "EventPipe trace|*.nettrace", [".nettrace"]),
        "listing" => new("JIT一覧を選択", "JIT listing|*.txt", [".txt"]),
        "folder" => new("許可済みフォルダーを選択", "", [], true),
        _ => throw new ArgumentException("Unknown path selection kind.", nameof(kind))
    };

    public static string ValidateSelection(WorkspaceGuard guard, string kind, string path)
    {
        var filter = FilterFor(kind);
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute selected path is required.");
        if (filter.Folder) return guard.RequireDirectory(path);
        var extension = Path.GetExtension(path);
        if (!filter.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("選択したファイルの種類が違います。参照ボタンから選び直してください。");
        return guard.RequireFile(path, extension);
    }

    public async Task<NativePathSelection> PickAsync(string kind, string? initialPath, CancellationToken ct)
    {
        var filter = FilterFor(kind);
        if (!Available) throw new PlatformNotSupportedException(
            "標準ダイアログはWindowsの対話セッション専用です。start.batで起動するか、パスを直接入力してください。");
        if (!await Gate.WaitAsync(0, ct)) throw new InvalidOperationException(
            "選択画面がすでに開いています。タスクバーを確認してください。");
        try
        {
            string initial = "";
            // Untrusted hints never bypass the directory allowlist or probe arbitrary paths.
            if (!string.IsNullOrWhiteSpace(initialPath) && initialPath.Length <= 4096 && Path.IsPathFullyQualified(initialPath))
            {
                try
                {
                    initial = guard.RequireDirectory(Path.GetDirectoryName(initialPath)!);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = new Process { StartInfo = CreateStartInfo(filter, initial) };
            if (!process.Start()) throw new IOException("選択画面を起動できませんでした。");
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var error = ReadBounded(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = await output.WaitAsync(timeout.Token);
                await error.WaitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new IOException(
                    "Windowsの選択画面を開けませんでした。パスの手入力も利用できます。");
                var selected = JsonSerializer.Deserialize<NativePathSelection>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Empty dialog response.");
                if (selected.Cancelled) return new(true, null);
                try { return new(false, ValidateSelection(guard, kind, selected.Path ?? "")); }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException(
                        "この場所は許可範囲外、またはリンク経由のパスです。通常のフォルダーを選んでください。" +
                        "許可範囲を変えるには、甜花ちゃんを終了してsetup.batから解析フォルダーを選び直します。");
                }
            }
            finally
            {
                timeout.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            }
        }
        finally { Gate.Release(); }
    }

    public static ProcessStartInfo CreateStartInfo(NativeDialogFilter filter, string initialDirectory)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(DialogScript)) }) info.ArgumentList.Add(arg);
        // The command is a fixed script. File paths and labels are data, not PowerShell expressions.
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["TENKA_PICK_TITLE"] = filter.Title;
        info.Environment["TENKA_PICK_FILTER"] = filter.Filter;
        info.Environment["TENKA_PICK_FOLDER"] = filter.Folder ? "1" : "0";
        info.Environment["TENKA_PICK_INITIAL"] = initialDirectory;
        return info;
    }

    private static async Task<string> ReadBounded(StreamReader stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + read > 16384) throw new IOException("Dialog response was too large.");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private const string DialogScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        Add-Type -AssemblyName System.Windows.Forms
        $owner = New-Object System.Windows.Forms.Form
        $owner.ShowInTaskbar = $false
        $owner.TopMost = $true
        $owner.Opacity = 0
        $dialog = $null
        try {
            $owner.Show()
            if ($env:TENKA_PICK_FOLDER -eq '1') {
                $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
                $dialog.Description = $env:TENKA_PICK_TITLE
                $dialog.ShowNewFolderButton = $false
                $dialog.SelectedPath = $env:TENKA_PICK_INITIAL
            } else {
                $dialog = New-Object System.Windows.Forms.OpenFileDialog
                $dialog.Title = $env:TENKA_PICK_TITLE
                $dialog.Filter = $env:TENKA_PICK_FILTER
                $dialog.InitialDirectory = $env:TENKA_PICK_INITIAL
                $dialog.Multiselect = $false
                $dialog.CheckFileExists = $true
                $dialog.RestoreDirectory = $true
            }
            $ok = $dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK
            $path = $null
            if ($ok) {
                if ($env:TENKA_PICK_FOLDER -eq '1') { $path = $dialog.SelectedPath }
                else { $path = $dialog.FileName }
            }
            [Console]::WriteLine((@{ cancelled = -not $ok; path = $path } | ConvertTo-Json -Compress))
        } finally {
            if ($null -ne $dialog) { $dialog.Dispose() }
            $owner.Dispose()
        }
        """;
}
