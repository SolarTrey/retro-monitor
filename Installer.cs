// Retro Resolution & FPS - installer / uninstaller
// One exe does both jobs: run it normally to install, or with /uninstall to remove.
// Installs per-user (no administrator rights needed).

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const string AppName = "Retro Resolution & FPS";
    const string ExeName = "RetroMonitor.exe";
    const string FolderName = "RetroResolutionFPS";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RetroResolutionFPS";

    static string InstallDir
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                Path.Combine("Programs", FolderName));
        }
    }
    static string StartMenuLink
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        }
    }
    static string DesktopLink
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool uninstall = false;
        foreach (string a in args)
            if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase))
                uninstall = true;

        if (uninstall) RunUninstall();
        else Application.Run(new InstallForm());
    }

    // ---------------------------------------------------------------- install
    public static void Install(bool desktopShortcut)
    {
        string dir = InstallDir;

        // close a running copy so the file can be replaced
        foreach (Process p in Process.GetProcessesByName("RetroMonitor"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        Directory.CreateDirectory(dir);
        string exePath = Path.Combine(dir, ExeName);

        // the program is embedded inside this installer
        Assembly asm = Assembly.GetExecutingAssembly();
        using (Stream s = asm.GetManifestResourceStream(ExeName))
        {
            if (s == null) throw new InvalidOperationException("The program is missing from the installer.");
            using (FileStream f = new FileStream(exePath, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) f.Write(buf, 0, n);
            }
        }

        // keep a copy of this installer to act as the uninstaller
        string unPath = Path.Combine(dir, "Uninstall.exe");
        string self = Application.ExecutablePath;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(unPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, unPath, true);

        MakeShortcut(StartMenuLink, exePath, dir);
        if (desktopShortcut) MakeShortcut(DesktopLink, exePath, dir);
        else TryDelete(DesktopLink);

        // "Apps & features" entry
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", "1.0");
            k.SetValue("Publisher", "Retro Resolution & FPS");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", exePath);
            k.SetValue("UninstallString", "\"" + unPath + "\" /uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(exePath).Length / 1024), RegistryValueKind.DWord); } catch { }
        }
    }

    public static string ProgramPath { get { return Path.Combine(InstallDir, ExeName); } }

    static void MakeShortcut(string linkPath, string target, string workDir)
    {
        // WScript.Shell via late binding, so no extra references are needed
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // -------------------------------------------------------------- uninstall
    static void RunUninstall()
    {
        if (MessageBox.Show("Remove " + AppName + " from this computer?", AppName + " - Uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (Process p in Process.GetProcessesByName("RetroMonitor"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        TryDelete(ProgramPath);

        // this exe lives in the folder being removed, so a tiny helper deletes it once we exit
        string dir = InstallDir;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch { }

        MessageBox.Show(AppName + " has been removed.", AppName + " - Uninstall",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

// ------------------------------------------------------------------ window
class InstallForm : Form
{
    CheckBox cbDesktop, cbLaunch;
    Button btnInstall, btnCancel;
    Label status;

    public InstallForm()
    {
        Text = "Retro Resolution & FPS - Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 250);
        BackColor = Color.FromArgb(16, 20, 18);
        ForeColor = Color.FromArgb(185, 255, 195);
        Font = new Font("Consolas", 9.5f, FontStyle.Bold);

        Label title = new Label();
        title.Text = "RETRO RESOLUTION & FPS";
        title.Font = new Font("Consolas", 15f, FontStyle.Bold);
        title.AutoSize = true;
        title.Location = new Point(20, 18);
        Controls.Add(title);

        Label blurb = new Label();
        blurb.Text = "A vintage CRT monitor widget that shows the\r\nFPS and resolution of any video you drop on it.";
        blurb.ForeColor = Color.FromArgb(70, 200, 95);
        blurb.AutoSize = true;
        blurb.Location = new Point(22, 58);
        Controls.Add(blurb);

        cbDesktop = new CheckBox();
        cbDesktop.Text = "Create a desktop shortcut";
        cbDesktop.Checked = true;
        cbDesktop.AutoSize = true;
        cbDesktop.Location = new Point(24, 110);
        Controls.Add(cbDesktop);

        cbLaunch = new CheckBox();
        cbLaunch.Text = "Start the monitor when setup finishes";
        cbLaunch.Checked = true;
        cbLaunch.AutoSize = true;
        cbLaunch.Location = new Point(24, 138);
        Controls.Add(cbLaunch);

        status = new Label();
        status.Text = "Installs for your user account only. No administrator rights needed.";
        status.ForeColor = Color.FromArgb(70, 200, 95);
        status.AutoSize = false;
        status.Size = new Size(380, 32);
        status.Location = new Point(22, 172);
        Controls.Add(status);

        btnInstall = new Button();
        btnInstall.Text = "Install";
        btnInstall.Size = new Size(90, 30);
        btnInstall.Location = new Point(220, 206);
        btnInstall.FlatStyle = FlatStyle.Flat;
        btnInstall.FlatAppearance.BorderColor = Color.FromArgb(70, 200, 95);
        btnInstall.ForeColor = Color.FromArgb(185, 255, 195);
        btnInstall.Click += OnInstall;
        Controls.Add(btnInstall);

        btnCancel = new Button();
        btnCancel.Text = "Cancel";
        btnCancel.Size = new Size(90, 30);
        btnCancel.Location = new Point(316, 206);
        btnCancel.FlatStyle = FlatStyle.Flat;
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(70, 200, 95);
        btnCancel.ForeColor = Color.FromArgb(185, 255, 195);
        btnCancel.Click += delegate { Close(); };
        Controls.Add(btnCancel);

        AcceptButton = btnInstall;
        CancelButton = btnCancel;
    }

    void OnInstall(object sender, EventArgs e)
    {
        btnInstall.Enabled = false;
        btnCancel.Enabled = false;
        status.Text = "Installing...";
        Application.DoEvents();
        try
        {
            Program.Install(cbDesktop.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Setup could not finish:\r\n\r\n" + ex.Message, "Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            btnInstall.Enabled = true;
            btnCancel.Enabled = true;
            status.Text = "Setup failed.";
            return;
        }

        if (cbLaunch.Checked)
        {
            try { Process.Start(Program.ProgramPath); } catch { }
        }
        MessageBox.Show("Installed. Find it in the Start menu under \"Retro Resolution & FPS\".\r\n\r\n" +
                        "To remove it later: Settings > Apps > Installed apps.",
                        "Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
