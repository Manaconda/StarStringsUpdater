using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace StarStringsUpdater;

public sealed record AccessCheck(bool Writable, bool CanRepair, string Message);

public static class InstallPermissions
{
    static readonly string[] Directories = ["", "Data", "Data/Localization", "Data/Localization/english"];
    static readonly string[] Files = ["USER.cfg", Updater.GlobalRelative, Updater.ReceiptName];

    static string Validate(string target)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Detection.IsInstall(root)) throw new IOException("Select the LIVE folder containing Bin64\\StarCitizen.exe.");
        foreach (var relative in Directories.Concat(Files))
        for (string? path = Path.Combine(root, relative); path != null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Permission setup does not support linked folders or files: " + path);
        }
        return root;
    }

    public static AccessCheck Check(string target)
    {
        var created = new List<string>();
        try
        {
            var root = Validate(target);
            foreach (var relative in Directories)
            {
                var dir = Path.Combine(root, relative);
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); created.Add(dir); }
                // Exercise the same create/replace/delete operations used by installation.
                var first = Path.Combine(dir, ".starstrings-write-test-" + Guid.NewGuid().ToString("N"));
                var second = first + ".replacement";
                try
                {
                    File.WriteAllText(first, "test"); PreserveCreatorAccess(first);
                    File.WriteAllText(second, "replacement"); PreserveCreatorAccess(second);
                    File.Move(second, first, true); File.Delete(first);
                }
                finally
                {
                    if (File.Exists(second)) File.Delete(second);
                    if (File.Exists(first)) File.Delete(first);
                }
            }
            foreach (var relative in Files)
            {
                var path = Path.Combine(root, relative);
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    return new(false, false, "Remove the read-only attribute from " + path + " before updating.");
                // OPEN_EXISTING never truncates or changes contents. DELETE access is needed for replacement.
                using var handle = CreateFile(path, 0x80000000 | 0x40000000 | 0x00010000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 5) return new(false, true, "Windows denies update access to " + path);
                    return new(false, false, "Cannot access " + path + ": " + new Win32Exception(error).Message);
                }
            }
            return new(true, false, "Write access verified. Silent updates are ready.");
        }
        catch (UnauthorizedAccessException ex) { return new(false, true, ex.Message); }
        catch (Exception ex) { return new(false, false, ex.Message); }
        finally
        {
            foreach (var dir in created.AsEnumerable().Reverse())
                try { Directory.Delete(dir, false); } catch { /* Never remove other files. */ }
        }
    }

    public static void Grant(string target, string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        if (!sid.IsAccountSid()) throw new ArgumentException("A Windows account SID is required.");
        _ = sid.Translate(typeof(NTAccount));
        var root = Validate(target);
        var backup = new Dictionary<string, string>();
        var backupPath = Path.Combine(Storage.Root, "PermissionBackups", Guid.NewGuid().ToString("N") + ".json");
        // Non-inheriting grants avoid changing permissions on game executables or unrelated files.
        foreach (var relative in Directories)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(path);
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl(AccessControlSections.Access);
            backup[path] = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Storage.Write(backupPath, backup);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        foreach (var relative in Files)
        {
            var info = new FileInfo(Path.Combine(root, relative));
            if (!info.Exists) continue;
            var acl = info.GetAccessControl(AccessControlSections.Access);
            backup[info.FullName] = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Storage.Write(backupPath, backup);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
    }

    // Files created by this account must remain writable after inheriting a restrictive folder ACL.
    // Only call this on our own newly created temporary files, never existing game files.
    public static void PreserveCreatorAccess(string path)
    {
        var info = new FileInfo(path);
        var acl = info.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    public static async Task<bool> RequestElevation(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Cannot identify your Windows account.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--grant-permissions"); start.ArgumentList.Add(Path.GetFullPath(path)); start.ArgumentList.Add(sid);
        using var process = Process.Start(start) ?? throw new IOException("Could not start permission setup.");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    public static int RunHelper(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--grant-permissions") return 2;
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
            Grant(args[1], args[2]);
            return 0;
        }
        catch (Exception ex) { Storage.Log("Permission setup: " + ex); return 1; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
