using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StarStringsUpdater;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Elevated setup is a short-lived helper, separate from the unelevated tray instance.
        if (args.Contains("--grant-permissions")) { Environment.ExitCode = InstallPermissions.RunHelper(args); return; }
        using var mutex = new Mutex(true, @"Local\StarStringsUpdater", out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StarStringsUpdater.Show");
        if (!first) { if (!args.Contains("--startup")) show.Set(); return; }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Storage.Log(e.Exception.ToString());
        try { Application.Run(new TrayApp(args.Contains("--startup"), show)); }
        catch (Exception ex)
        {
            Storage.Log(ex.ToString());
            if (!args.Contains("--startup")) MessageBox.Show(ex.Message, "StarStrings Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    readonly string settingsPath = Path.Combine(Storage.Root, "settings.json");
    readonly NotifyIcon tray;
    readonly ConfigForm form;
    readonly Updater updater = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly EventWaitHandle showEvent;
    readonly CancellationTokenSource cancellation = new();
    readonly Icon icon;
    Settings settings;
    Release? available;
    bool busy, exiting;
    DateTime nextCheck = DateTime.UtcNow;
    string status = "Ready to check for updates.";
    public TrayApp(bool silent, EventWaitHandle showEvent)
    {
        this.showEvent = showEvent;
        try { settings = Storage.Read<Settings>(settingsPath) ?? new(); }
        catch (Exception ex) { settings = new(); status = "Could not read settings. Please save your configuration again."; Storage.Log(ex.ToString()); }
        if (string.IsNullOrEmpty(settings.InstallPath)) settings.InstallPath = Detection.Find();
        icon = MakeIcon();
        form = new ConfigForm(icon, settings.InstallPath, Startup.Enabled);
        form.SaveRequested += async () => { await Save(); };
        form.UpdateRequested += async () => { if (await Save()) await RunUpdate(true); };
        form.Shown += async (_, _) =>
        {
            if (!settings.Configured && Detection.IsInstall(form.InstallPath) && !await CheckAccess(form.InstallPath)) return;
            if (!exiting && !busy) await RunUpdate(false);
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open configuration", null, (_, _) => Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());
        tray = new NotifyIcon { Icon = icon, Text = "StarStrings Updater", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Open(); };
        timer.Tick += async (_, _) =>
        {
            if (showEvent.WaitOne(0)) Open();
            if (settings.Configured && !busy && DateTime.UtcNow >= nextCheck)
            {
                nextCheck = DateTime.UtcNow.AddHours(1);
                await RunUpdate(true);
            }
        };
        Refresh();
        if (!silent) { Open(); nextCheck = DateTime.UtcNow.AddHours(1); }
        timer.Start();
    }
    async Task<bool> CheckAccess(string path)
    {
        if (busy || exiting) return false;
        busy = true; status = "Checking write access…"; Refresh();
        try
        {
            var check = await Task.Run(() => InstallPermissions.Check(path));
            if (!check.Writable && check.CanRepair && !exiting)
            {
                var answer = MessageBox.Show(form,
                    "Silent updates need write access to:\n\n" + path +
                    "\n\nAllow a one-time administrator prompt to grant your Windows account Modify access to the updater’s folders and files? Other game files keep their current permissions.",
                    "Set up silent updates", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    status = "Waiting for administrator permission…"; Refresh();
                    bool completed = await InstallPermissions.RequestElevation(path);
                    check = await Task.Run(() => InstallPermissions.Check(path));
                    if (!completed && !check.Writable) status = "Permission setup did not complete. " + check.Message;
                    else status = check.Message;
                }
                else status = "Permission setup skipped. Save settings to try again. Silent updates need write access.";
            }
            else status = check.Message;
            return check.Writable;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { status = "Administrator prompt cancelled. Save settings to try again."; return false; }
        catch (Exception ex) { status = "Permission setup failed: " + ex.Message; Storage.Log(ex.ToString()); return false; }
        finally { busy = false; if (exiting) FinishExit(); else Refresh(); }
    }
    async Task<bool> Save()
    {
        if (busy) return false;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(form.InstallPath.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar);
            if (!Detection.IsInstall(path)) throw new IOException("Select the LIVE folder containing Bin64\\StarCitizen.exe.");
            if (!await CheckAccess(path) || exiting) return false;
            Startup.Set(form.RunAtStartup);
            settings.InstallPath = path;
            settings.Configured = true;
            Storage.Write(settingsPath, settings);
            status = "Settings saved. Background checks run hourly while this app is in the tray.";
            nextCheck = DateTime.UtcNow.AddHours(1);
            Refresh(); return true;
        }
        catch (Exception ex) { status = ex.Message; Refresh(); return false; }
    }
    async Task RunUpdate(bool install)
    {
        if (busy || exiting) return;
        busy = true; status = "Checking GitHub for the latest LIVE release…"; Refresh();
        try
        {
            available = await updater.Check(cancellation.Token);
            var receipt = await Task.Run(() => Updater.Installed(settings.InstallPath));
            var current = receipt?.Identity == available.Identity && Updater.LanguageEnabled(settings.InstallPath);
            if (current) status = "You’re up to date. Last checked " + DateTime.Now.ToString("g") + ".";
            else if (!install) status = "LIVE release available. Choose Check / update to install or verify it.";
            else
            {
                await updater.Install(available, settings.InstallPath, s => { status = s; Refresh(); }, cancellation.Token);
                status = "StarStrings is up to date. Installed " + DateTime.Now.ToString("g") + ".";
            }
            Storage.Log(status);
        }
        catch (OperationCanceledException) { status = "Update cancelled."; }
        catch (UnauthorizedAccessException ex)
        {
            status = "Windows denied write access. Open configuration and choose Save settings to check access and set up permissions.";
            Storage.Log(ex.ToString());
        }
        catch (Exception ex) { status = "Update failed: " + ex.Message; Storage.Log(ex.ToString()); }
        finally
        {
            busy = false;
            if (exiting) FinishExit(); else Refresh();
        }
    }
    void Refresh()
    {
        var installed = Updater.Installed(settings.InstallPath);
        var installedText = installed?.Version ?? (File.Exists(Path.Combine(settings.InstallPath, Updater.GlobalRelative)) ? "Untracked or changed files" : "Not installed");
        var availableText = available?.Version ?? "Not checked yet";
        form.UpdateState(installedText, availableText, status, busy);
        static string Short(string value)
        {
            var match = Regex.Match(value, @"release-([^)]+)");
            var result = match.Success ? match.Groups[1].Value : value;
            return result.Length > 39 ? result[..36] + "…" : result;
        }
        tray.Text = $"StarStrings LIVE\nInstalled: {Short(installedText)}\nAvailable: {Short(availableText)}";
    }
    void Open() { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); }
    void Quit()
    {
        exiting = true; timer.Stop(); form.Hide(); cancellation.Cancel();
        if (!busy) FinishExit();
    }
    void FinishExit()
    {
        tray.Visible = false; tray.Dispose(); timer.Dispose(); form.AllowClose = true; form.Close(); form.Dispose();
        updater.Dispose(); cancellation.Dispose(); icon.Dispose(); ExitThread();
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(64, 64); using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(20, 29, 46));
        using var brush = new SolidBrush(Color.FromArgb(100, 211, 230));
        g.FillPolygon(brush, new PointF[] { new(32, 6), new(39, 24), new(58, 32), new(39, 39), new(32, 58), new(25, 39), new(6, 32), new(25, 24) });
        var handle = bitmap.GetHicon(); using var borrowed = Icon.FromHandle(handle); var result = (Icon)borrowed.Clone(); DestroyIcon(handle); return result;
    }
}

internal sealed class ConfigForm : Form
{
    readonly TextBox path = new() { Dock = DockStyle.Fill };
    readonly CheckBox startup = new() { Text = "Run at Windows startup · check and install silently", AutoSize = true };
    readonly Label installed = new() { AutoSize = true };
    readonly Label available = new() { AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Button update = new() { Text = "Check / update StarStrings", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    readonly Button save = new() { Text = "Save settings", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    readonly Button browse = new() { Text = "Browse…", AutoSize = true };
    readonly Button detect = new() { Text = "Detect", AutoSize = true };
    public event Action? SaveRequested;
    public event Action? UpdateRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }
    public string InstallPath => path.Text;
    public bool RunAtStartup => startup.Checked;
    public ConfigForm(Icon icon, string initialPath, bool runAtStartup)
    {
        Text = "StarStrings Updater"; Icon = icon; StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(670, 445); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        BackColor = Color.FromArgb(245, 247, 250); ForeColor = Color.FromArgb(27, 39, 57);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 10 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(new Label { Text = "StarStrings", Font = new Font("Segoe UI", 22, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Keep your Star Citizen LIVE strings up to date.", AutoSize = true }, 0, 1);
        layout.Controls.Add(new Label { Text = "Star Citizen LIVE installation folder", AutoSize = true }, 0, 2);
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        path.Text = initialPath; pathRow.Controls.Add(path, 0, 0); pathRow.Controls.Add(browse, 1, 0); pathRow.Controls.Add(detect, 2, 0); layout.Controls.Add(pathRow, 0, 3);
        startup.Checked = runAtStartup; layout.Controls.Add(startup, 0, 4);
        layout.Controls.Add(installed, 0, 5); layout.Controls.Add(available, 0, 6);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        update.BackColor = Color.FromArgb(26, 90, 132); update.ForeColor = Color.White; update.FlatStyle = FlatStyle.Flat;
        buttons.Controls.Add(update); buttons.Controls.Add(save); layout.Controls.Add(buttons, 0, 7);
        layout.Controls.Add(status, 0, 8);
        layout.Controls.Add(new Label { Text = "Closing this window keeps the app in the tray. Right-click its icon to exit.", AutoSize = true, ForeColor = Color.FromArgb(85, 96, 111), Font = new Font("Segoe UI", 9) }, 0, 9);
        Controls.Add(layout);
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select Star Citizen’s LIVE folder", UseDescriptionForTitle = true, InitialDirectory = path.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; };
        detect.Click += (_, _) => { var found = Detection.Find(); if (found.Length > 0) path.Text = found; else status.Text = "No LIVE installation found in common locations. Please browse to it."; };
        save.Click += (_, _) => SaveRequested?.Invoke();
        update.Click += (_, _) => UpdateRequested?.Invoke();
        FormClosing += (_, e) => { if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }
    public void UpdateState(string installedVersion, string availableVersion, string message, bool busy)
    {
        installed.Text = "Installed:  " + installedVersion; available.Text = "Available:  " + availableVersion;
        status.Text = message; update.Enabled = save.Enabled = path.Enabled = startup.Enabled = browse.Enabled = detect.Enabled = !busy;
        update.Text = busy ? "Working…" : "Check / update StarStrings";
    }
}
