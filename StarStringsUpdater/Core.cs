using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StarStringsUpdater;

public sealed class Settings
{
    public string InstallPath { get; set; } = "";
    public bool Configured { get; set; }
}

public sealed class Receipt
{
    public string Identity { get; set; } = "";
    public string Version { get; set; } = "";
    public string GlobalHash { get; set; } = "";
}

public sealed record Release(string Version, string Identity, string Url, string? Digest, long Size);

public static class Storage
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarStringsUpdater");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "updater.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch { /* Logging must never interrupt a silent startup. */ }
    }
}

public static class Detection
{
    public static bool IsInstall(string path) => Directory.Exists(path) && File.Exists(Path.Combine(path, "Bin64", "StarCitizen.exe")) && Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("LIVE", StringComparison.OrdinalIgnoreCase);
    public static string Find()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var keyName in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = hive.OpenSubKey(keyName);
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var sub = key!.OpenSubKey(name);
                    var label = sub?.GetValue("DisplayName") as string ?? "";
                    if (!label.Contains("RSI", StringComparison.OrdinalIgnoreCase) && !label.Contains("Star Citizen", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sub?.GetValue("InstallLocation") is string dir && !string.IsNullOrWhiteSpace(dir))
                    {
                        candidates.Add(dir);
                        candidates.Add(Path.Combine(dir, "StarCitizen", "LIVE"));
                        candidates.Add(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) ?? dir, "StarCitizen", "LIVE"));
                    }
                }
            }
            catch (Exception ex) { Storage.Log("Detection: " + ex.Message); }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        foreach (var relative in new[] { @"Program Files\Roberts Space Industries\StarCitizen\LIVE", @"Roberts Space Industries\StarCitizen\LIVE", @"Games\Roberts Space Industries\StarCitizen\LIVE", @"Games\StarCitizen\LIVE", @"StarCitizen\LIVE" })
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, relative));
        return candidates.FirstOrDefault(IsInstall) ?? "";
    }
}

public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "StarStringsUpdater";
    public static bool Enabled { get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) is string; } }
    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --startup");
        else k.DeleteValue(Name, false);
    }
}

public sealed class Updater : IDisposable
{
    public const string GlobalRelative = "Data/Localization/english/global.ini";
    public const string ReceiptName = ".starstrings-updater.json";
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    public Updater() { http.DefaultRequestHeaders.UserAgent.ParseAdd("StarStringsUpdater/1.0"); }
    public async Task<Release> Check(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("https://api.github.com/repos/MrKraken/StarStrings/releases/latest", ct);
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429) throw new IOException("GitHub is rate limiting checks. Please try again later.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean()) throw new IOException("No stable LIVE release is available.");
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == "StarStrings-LIVE.zip").ToArray();
        if (assets.Length != 1) throw new IOException("The LIVE ZIP asset is missing or ambiguous. Check the release page.");
        var a = assets[0];
        var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var url = a.GetProperty("browser_download_url").GetString()!;
        if (!url.StartsWith("https://github.com/MrKraken/StarStrings/releases/download/", StringComparison.Ordinal)) throw new IOException("Unexpected download URL.");
        return new(root.GetProperty("name").GetString() ?? "Unknown", $"{root.GetProperty("id")}:{a.GetProperty("id")}:{a.GetProperty("updated_at")}:{digest}", url, digest, a.GetProperty("size").GetInt64());
    }
    public static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    public static Receipt? Installed(string target)
    {
        try
        {
            var r = Storage.Read<Receipt>(Path.Combine(target, ReceiptName));
            return r != null && File.Exists(Path.Combine(target, GlobalRelative)) && Hash(Path.Combine(target, GlobalRelative)) == r.GlobalHash ? r : null;
        }
        catch { return null; }
    }
    public static bool LanguageEnabled(string target)
    {
        var path = Path.Combine(target, "USER.cfg");
        if (!File.Exists(path)) return false;
        var matches = Regex.Matches(File.ReadAllText(path), @"(?im)^\s*g_language\s*=\s*([^\r\n]*)");
        return matches.Count > 0 && Regex.IsMatch(matches[^1].Groups[1].Value, "^\"?english\"?\\s*(?://.*|;.*)?$", RegexOptions.IgnoreCase);
    }
    public async Task Install(Release release, string target, Action<string> status, CancellationToken ct = default)
    {
        if (!Detection.IsInstall(target)) throw new IOException("Choose the Star Citizen LIVE folder containing Bin64\\StarCitizen.exe.");
        if (Process.GetProcessesByName("StarCitizen").Any()) throw new IOException("Close Star Citizen before updating. The app will try again at its next background check.");
        var temp = Path.Combine(Path.GetTempPath(), "StarStringsUpdater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            status("Downloading the LIVE release…");
            var zipPath = Path.Combine(temp, "release.zip");
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(zipPath);
                var buffer = new byte[81920]; long total = 0; int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > 100_000_000) throw new IOException("Release exceeds the 100 MB download limit.");
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                if (total != release.Size) throw new IOException("The download is incomplete or the release changed. Try again.");
            }
            if (!string.IsNullOrEmpty(release.Digest) && !string.Equals("sha256:" + Hash(zipPath), release.Digest, StringComparison.OrdinalIgnoreCase)) throw new IOException("The release checksum does not match. Nothing was installed.");
            ct.ThrowIfCancellationRequested();
            if (Process.GetProcessesByName("StarCitizen").Any()) throw new IOException("Star Citizen started during the download. Close it before updating.");
            status("Verifying and installing…");
            // Once committing starts, finish or roll back before honouring an exit request.
            await Task.Run(() => InstallArchive(zipPath, target, release));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    public static void InstallArchive(string zipPath, string target, Release release, Action<int>? afterWrite = null)
    {
        var root = Path.GetFullPath(target);
        if (!Detection.IsInstall(root)) throw new IOException("Not a Star Citizen LIVE installation.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Any(p => p == ".." || p.Contains(':'))) throw new IOException("Unsafe path in release archive.");
                if (name.EndsWith('/')) continue;
                // Install only the documented payload; never game binaries or arbitrary archive paths.
                if (!name.Equals(GlobalRelative, StringComparison.OrdinalIgnoreCase) && !name.Equals("USER.cfg", StringComparison.OrdinalIgnoreCase) && !name.Equals("readme.md", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected file in release archive: " + name);
                if ((total += entry.Length) > 100_000_000) throw new IOException("Release exceeds the extraction limit.");
                using var stream = entry.Open(); using var ms = new MemoryStream(); stream.CopyTo(ms);
                if (!files.TryAdd(name, ms.ToArray())) throw new IOException("Duplicate file in release archive.");
            }
        }
        if (!files.TryGetValue(GlobalRelative, out var global) || global.Length == 0) throw new IOException("Release does not contain the expected English strings.");
        files.Remove("readme.md"); // Documentation is not part of the game configuration.
        var cfg = Path.Combine(root, "USER.cfg");
        EnsureNoLinks(root, cfg);
        files["USER.cfg"] = MergeConfig(File.Exists(cfg) ? File.ReadAllBytes(cfg) : []);
        var receipt = new Receipt { Identity = release.Identity, Version = release.Version, GlobalHash = Convert.ToHexString(SHA256.HashData(global)) };
        files[ReceiptName] = JsonSerializer.SerializeToUtf8Bytes(receipt, Storage.Json);
        foreach (var relative in files.Keys) EnsureNoLinks(root, Path.Combine(root, relative));
        var backup = Path.Combine(Storage.Root, "Backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var previous = new Dictionary<string, string?>();
        foreach (var relative in files.Keys)
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path))
            {
                var saved = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(path, saved);
                previous[relative] = saved;
            }
            else previous[relative] = null;
        }
        Storage.Write(Path.Combine(backup, "backup.json"), new { Target = root, Files = previous });
        var changed = new List<string>();
        try
        {
            foreach (var (relative, bytes) in files)
            {
                var path = Path.Combine(root, relative);
                AtomicBytes(path, bytes); changed.Add(relative); afterWrite?.Invoke(changed.Count);
            }
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            foreach (var relative in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var path = Path.Combine(root, relative);
                    if (previous[relative] is string saved) AtomicBytes(path, File.ReadAllBytes(saved)); else File.Delete(path);
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 1) throw new AggregateException("Update and rollback failed. Restore files from " + backup, errors);
            throw;
        }
        Storage.Log($"Installed {release.Version} to {root}. Backup: {backup}");
    }
    static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(tmp, bytes); InstallPermissions.PreserveCreatorAccess(tmp); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    static void EnsureNoLinks(string root, string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folders/files are not supported: " + current);
        }
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid destination.");
    }
    public static byte[] MergeConfig(byte[] original)
    {
        Encoding encoding = new UTF8Encoding(false, true); int skip = 0;
        if (original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(true, true); skip = 3; }
        else if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; skip = 2; }
        else if (original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        string text;
        try { text = encoding.GetString(original, skip, original.Length - skip); }
        catch (DecoderFallbackException) { encoding = Encoding.Latin1; text = encoding.GetString(original); }
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var pattern = @"(?im)^[\t ]*g_language[\t ]*=[^\r\n]*";
        if (Regex.IsMatch(text, pattern)) text = Regex.Replace(text, pattern, "g_language = english");
        else text += (text.Length > 0 && !text.EndsWith('\n') ? nl : "") + "g_language = english" + nl;
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }
    public void Dispose() => http.Dispose();
}
