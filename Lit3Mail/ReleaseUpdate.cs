using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lit3Mail;

internal static class ReleaseUpdate
{
    internal const string AdministratorUpdateArgument = "--install-update-as-administrator";
    private const string Repository = "maddielarsen47/lit3mail";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };

    static ReleaseUpdate() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("Lit3Mail-Updater");

    internal sealed record Release(Version Version, Uri Download, string? Digest);

    public static Version InstalledVersion => Version.Parse(typeof(ReleaseUpdate).Assembly.GetName().Version!.ToString(3));

    public static async Task<Release?> CheckAsync(CancellationToken token)
    {
        const int pageSize = 100;
        Version version = InstalledVersion;
        JsonElement release = default;
        string tag = "";
        for (int page = 1; ; page++)
        {
            using var response = await Client.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}", token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var candidate in json.RootElement.EnumerateArray())
            {
                if (candidate.GetProperty("draft").GetBoolean())
                    continue;

                string candidateTag = candidate.GetProperty("tag_name").GetString() ?? "";
                if (!Regex.IsMatch(candidateTag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
                    !Version.TryParse(candidateTag[1..], out var candidateVersion) || candidateVersion <= version)
                    continue;

                tag = candidateTag;
                version = candidateVersion;
                release = candidate.Clone();
            }

            if (json.RootElement.GetArrayLength() < pageSize)
                break;
        }

        if (release.ValueKind == JsonValueKind.Undefined)
            return null;

        var assets = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        string architecture = RuntimeInformation.ProcessArchitecture.ToString();
        var matching = assets.Where(asset => Regex.IsMatch(asset.GetProperty("name").GetString()!,
            $@"(?:^|[^a-z0-9]){architecture}(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase)).ToArray();
        if (matching.Length != 1 && (matching.Length != 0 || assets.Length != 1))
            throw new InvalidDataException("Could not identify a portable ZIP for this version of Lit3Mail.");

        var selected = matching.Length == 1 ? matching[0] : assets[0];
        if (!Uri.TryCreate(selected.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || !url.AbsolutePath.StartsWith($"/{Repository}/releases/download/{tag}/", StringComparison.Ordinal))
            throw new InvalidDataException("The release ZIP has an unexpected download address.");
        return new(version, url, selected.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }

    public static bool RequiresAdministrator()
    {
        try
        {
            CheckInstallWriteAccess(AppContext.BaseDirectory);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw;
            return true;
        }
    }

    public static void RestartAsAdministrator()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not locate Lit3Mail.exe.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(AdministratorUpdateArgument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Lit3Mail as administrator.");
    }

    private static void CheckInstallWriteAccess(string install)
    {
        using var probe = File.Create(Path.Combine(install, $".lit3mail-update-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose);
    }

    public static async Task<StagedRelease> StageAsync(Release release, CancellationToken token)
    {
        string executable = Environment.ProcessPath ?? "";
        string install = AppContext.BaseDirectory;
        if (!Path.GetFileName(executable).Equals("Lit3Mail.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(install, "Lit3Mail.dll")))
            throw new InvalidOperationException("Start Lit3Mail.exe from its portable installation folder to update.");

        string work = Directory.CreateTempSubdirectory("Lit3Mail-update-").FullName;
        try
        {
            using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
            download.CancelAfter(TimeSpan.FromMinutes(3));
            string zip = Path.Combine(work, "release.zip");
            await using (var file = File.Create(zip))
            {
                using var response = await Client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, download.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(file, download.Token);
            }
            if (release.Digest is { Length: > 0 } digest)
            {
                await using var file = File.OpenRead(zip);
                string actual = "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded ZIP failed its checksum verification.");
            }

            string extracted = Path.Combine(work, "extracted");
            string source = await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(zip, extracted);
                var candidates = Directory.GetFiles(extracted, "Lit3Mail.exe", SearchOption.AllDirectories);
                if (candidates.Length != 1)
                    throw new InvalidDataException("The release ZIP must contain one Lit3Mail.exe.");
                string root = Path.GetDirectoryName(candidates[0])!;
                string assembly = Path.Combine(root, "Lit3Mail.dll");
                if (!File.Exists(assembly) ||
                    AssemblyName.GetAssemblyName(assembly).Version?.ToString(3) != release.Version.ToString(3) ||
                    ReadMachine(executable) != ReadMachine(candidates[0]) ||
                    !File.Exists(Path.Combine(root, "Lit3Mail.runtimeconfig.json")) ||
                    !File.Exists(Path.Combine(root, "Lit3Mail.deps.json")))
                    throw new InvalidDataException("The ZIP does not contain the expected Lit3Mail version and architecture.");
                return root;
            }, token);

            // Check write access while Lit3Mail can still report an error and stay open.
            CheckInstallWriteAccess(install);
            return new(work, source, install, executable);
        }
        catch
        {
            DeleteStaging(work);
            throw;
        }
    }

    private static Machine ReadMachine(string executable)
    {
        using var stream = File.OpenRead(executable);
        using var reader = new PEReader(stream);
        return reader.PEHeaders.CoffHeader.Machine;
    }

    private static void DeleteStaging(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed record StagedRelease(string Work, string Source, string Install, string Executable) : IDisposable
    {
        public void Dispose() => DeleteStaging(Work);

        public void Start()
        {
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
            foreach (string option in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(InstallScript)) })
                info.ArgumentList.Add(option);
            info.Environment["LIT3MAIL_UPDATE_WORK"] = Work;
            info.Environment["LIT3MAIL_UPDATE_SOURCE"] = Source;
            info.Environment["LIT3MAIL_UPDATE_INSTALL"] = Install;
            info.Environment["LIT3MAIL_UPDATE_EXE"] = Executable;
            info.Environment["LIT3MAIL_UPDATE_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the updater.");
        }
    }

    private const string InstallScript = """
        $ErrorActionPreference = 'Stop'
        $work = $env:LIT3MAIL_UPDATE_WORK
        $source = $env:LIT3MAIL_UPDATE_SOURCE
        $install = $env:LIT3MAIL_UPDATE_INSTALL
        $exe = $env:LIT3MAIL_UPDATE_EXE
        $backup = Join-Path $work 'backup'
        $changed = [Collections.Generic.List[object]]::new()

        try {
            $app = Get-Process -Id ([int]$env:LIT3MAIL_UPDATE_PID) -ErrorAction SilentlyContinue
            if ($app -and !$app.WaitForExit(60000)) { throw 'Lit3Mail did not exit.' }

            foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse -Force) {
                $relative = $file.FullName.Substring($source.Length + 1)
                $target = Join-Path $install $relative
                $saved = Join-Path $backup $relative
                for ($path = $target; $path -and $path.Length -ge $install.TrimEnd('\').Length; $path = [IO.Path]::GetDirectoryName($path)) {
                    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        throw "Cannot replace a redirected path: $path"
                    }
                }
                $original = [IO.File]::Exists($target)
                if ($original) {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) > $null
                    [IO.File]::Copy($target, $saved)
                }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) > $null
                $temporary = "$target.lit3mail-$([Guid]::NewGuid().ToString('N')).tmp"
                try {
                    [IO.File]::Copy($file.FullName, $temporary)
                    if ($original) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
                    else { [IO.File]::Move($temporary, $target) }
                    $changed.Add(@{ Target = $target; Backup = $saved; Original = $original })
                }
                finally { [IO.File]::Delete($temporary) }
            }
            Start-Process -FilePath $exe -WorkingDirectory $install
            Set-Location -LiteralPath ([IO.Path]::GetTempPath())
            try { [IO.Directory]::Delete($work, $true) } catch { }
        }
        catch {
            $message = $_.Exception.Message
            $restored = $true
            for ($i = $changed.Count - 1; $i -ge 0; $i--) {
                $entry = $changed[$i]
                try {
                    if ($entry.Original) { [IO.File]::Copy($entry.Backup, $entry.Target, $true) }
                    else { [IO.File]::Delete($entry.Target) }
                }
                catch { $restored = $false }
            }
            if ($restored) { $message += "`nThe previous installation was kept." }
            else { $message += "`nSome files could not be restored. Backups are in $backup." }
            Add-Type -AssemblyName System.Windows.Forms
            [Windows.Forms.MessageBox]::Show($message, 'Lit3Mail update failed', 'OK', 'Error') > $null
        }
        """;
}
