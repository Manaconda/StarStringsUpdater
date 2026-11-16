using System.IO.Compression;
using System.Text;
using StarStringsUpdater;

var workspace = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var testRoot = Path.Combine(workspace, "test-results", Guid.NewGuid().ToString("N"));
var live = Path.Combine(testRoot, "LIVE");
Directory.CreateDirectory(Path.Combine(live, "Bin64"));
File.WriteAllText(Path.Combine(live, "Bin64", "StarCitizen.exe"), "fixture");
int passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
var cfg = Encoding.UTF8.GetBytes("; My settings\r\nr_VSync = 0\r\ng_language = german\r\n");
var merged = Updater.MergeConfig(cfg);
Assert(Encoding.UTF8.GetString(merged) == "; My settings\r\nr_VSync = 0\r\ng_language = english\r\n", "Preserve unrelated USER.cfg settings and CRLF");
Assert(merged.SequenceEqual(Updater.MergeConfig(merged)), "Config merge is idempotent");
Assert(Encoding.UTF8.GetString(Updater.MergeConfig(Encoding.UTF8.GetBytes("r_VSync = 0"))) == "r_VSync = 0\ng_language = english\n", "Append missing setting with a line boundary");
byte[] unicode = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("r_VSync = 0\r\n")];
Assert(Updater.MergeConfig(unicode).AsSpan().StartsWith(Encoding.Unicode.GetPreamble()), "Preserve UTF-16 encoding");
Assert(Detection.IsInstall(live), "Recognize LIVE folder");
Assert(!Detection.IsInstall(testRoot), "Reject incorrect destination");
Assert(InstallPermissions.Check(live).Writable, "Writable installation passes setup without elevation");
Assert(!InstallPermissions.Check(testRoot).CanRepair, "Invalid destination never offers permission repair");
Assert(!Directory.Exists(Path.Combine(live, "Data")), "Write probe removes newly created empty directories");
var permissionFile = Path.Combine(live, "USER.cfg");
File.WriteAllText(permissionFile, "original");
File.SetAttributes(permissionFile, FileAttributes.ReadOnly);
Assert(!InstallPermissions.Check(live).CanRepair && !InstallPermissions.Check(live).Writable, "Read-only attribute is not mistaken for an ACL problem");
File.SetAttributes(permissionFile, FileAttributes.Normal);
using (var locked = new FileStream(permissionFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Assert(!InstallPermissions.Check(live).Writable && !InstallPermissions.Check(live).CanRepair, "Locked files do not trigger elevation");
Assert(File.ReadAllText(permissionFile) == "original", "Access probe never modifies existing payload");
var originalAcl = new DirectoryInfo(live).GetAccessControl();
try
{
    var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny);
    var restricted = new DirectoryInfo(live).GetAccessControl(); restricted.AddAccessRule(deny); new DirectoryInfo(live).SetAccessControl(restricted);
    var denied = InstallPermissions.Check(live);
    Assert(!denied.Writable && denied.CanRepair, "ACL denial is recognized as requiring permission setup");
}
finally
{
    var restore = new System.Security.AccessControl.DirectorySecurity();
    restore.SetSecurityDescriptorSddlForm(originalAcl.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
    new DirectoryInfo(live).SetAccessControl(restore);
}
var unrelatedAcl = new FileInfo(Path.Combine(live, "Bin64", "StarCitizen.exe")).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
InstallPermissions.Grant(live, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
var granted = InstallPermissions.Check(live);
Assert(granted.Writable, "Permission grant is followed by a successful access probe: " + granted.Message);
Assert(new FileInfo(Path.Combine(live, "Bin64", "StarCitizen.exe")).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access) == unrelatedAcl, "Permission setup leaves unrelated game executable ACL unchanged");
Assert(InstallPermissions.RunHelper(["--grant-permissions"]) == 2, "Helper rejects malformed command before mutation");
var release = new Release("SC LIVE Build (release-test)", "test-1", "", null, 0);
var realZip = Path.Combine(workspace, ".tools", "fixtures", "StarStrings-LIVE.zip");
File.WriteAllBytes(Path.Combine(live, "USER.cfg"), cfg);
Updater.InstallArchive(realZip, live, release);
Assert(Updater.Installed(live)?.Identity == "test-1", "Install real release ZIP and verify receipt/hash");
Assert(File.ReadAllBytes(Path.Combine(live, "USER.cfg")).SequenceEqual(merged), "Installation preserves custom settings");
Assert(Updater.LanguageEnabled(live), "Language enabled");
var globalPath = Path.Combine(live, Updater.GlobalRelative);
var before = File.ReadAllBytes(globalPath);
var receiptBefore = File.ReadAllBytes(Path.Combine(live, Updater.ReceiptName));
try { Updater.InstallArchive(realZip, live, release with { Identity = "test-2" }, count => { if (count == 2) throw new IOException("Simulated disk failure"); }); throw new Exception("Expected failure"); }
catch (IOException) { }
Assert(File.ReadAllBytes(globalPath).SequenceEqual(before) && File.ReadAllBytes(Path.Combine(live, Updater.ReceiptName)).SequenceEqual(receiptBefore), "Failed install rolls back payload and retains receipt");
File.AppendAllText(globalPath, "changed");
Assert(Updater.Installed(live) == null, "Detect altered or stale installation");
foreach (var evil in new[] { "../escape.txt", "Bin64/evil.exe", "Data/Localization/english/global.ini:stream" })
{
    var zipPath = Path.Combine(testRoot, Guid.NewGuid() + ".zip");
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(evil).Open()); writer.Write("test"); }
    bool rejected = false;
    try { Updater.InstallArchive(zipPath, live, release); } catch (IOException) { rejected = true; }
    Assert(rejected, "Reject unsafe/unexpected ZIP path: " + evil);
}
using var updater = new Updater();
var latest = await updater.Check();
Assert(latest.Url.EndsWith("StarStrings-LIVE.zip") && latest.Identity != "latest", "Live GitHub lookup selects LIVE asset and immutable identity");
await updater.Install(latest, live, Console.WriteLine);
Assert(Updater.Installed(live)?.Identity == latest.Identity, "End-to-end download, checksum, extraction and receipt");
try { await updater.Install(latest with { Digest = "sha256:incorrect" }, live, _ => {}); throw new Exception("Checksum test did not fail"); }
catch (IOException ex) { Assert(ex.Message.Contains("checksum"), "Reject checksum mismatch before installation"); }
Console.WriteLine($"{passed} tests passed. Test fixture: {live}");
