using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class DeepExeScanner
{
    static int started;

    static readonly HashSet<string> IgnoredExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins000.exe", "uninstall.exe", "uninstaller.exe", "setup.exe", "install.exe", "update.exe", "updater.exe",
        "launcherupdate.exe", "crashreportclient.exe", "unitycrashhandler32.exe", "unitycrashhandler64.exe",
        "ue-prereqsetup_x64.exe", "dotnet.exe", "msbuild.exe", "devenv.exe", "explorer.exe", "python.exe", "pythonw.exe",
        "node.exe", "java.exe", "javaw.exe", "conhost.exe", "reg.exe", "cmd.exe", "powershell.exe", "pwsh.exe"
    };

    static readonly string[] IgnoredPathParts =
    {
        "\\Windows\\", "\\WinSxS\\", "\\System32\\", "\\SysWOW64\\", "\\Microsoft.NET\\",
        "\\WindowsApps\\", "\\Common Files\\", "\\Windows Kits\\", "\\dotnet\\", "\\node_modules\\",
        "\\ProgramData\\Microsoft\\", "\\$Recycle.Bin\\", "\\System Volume Information\\"
    };

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += AttachWhenReady;

    static void AttachWhenReady(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Application.Idle -= AttachWhenReady;
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;
        _ = ScanAndMergeAsync(form);
    }

    static async Task ScanAndMergeAsync(MainForm form)
    {
        try
        {
            SetStatus(form, "Recherche approfondie des jeux installés...");
            var found = await Task.Run(ScanAllFixedDrives);
            if (found.Count == 0) return;

            var field = typeof(MainForm).GetField("games", BindingFlags.Instance | BindingFlags.NonPublic);
            var render = typeof(MainForm).GetMethod("RenderCards", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(form) is not List<Game> games || render == null) return;

            foreach (var candidate in found)
            {
                var existing = games.FirstOrDefault(g => g.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    games.Add(candidate);
                    continue;
                }

                foreach (var option in candidate.Options)
                {
                    if (!existing.Options.Any(o => string.Equals(o.Target, option.Target, StringComparison.OrdinalIgnoreCase)))
                        existing.Options.Add(option);
                }
            }

            // RenderCards touches WinForms controls, so marshal the reflection call to the UI thread.
            form.Invoke(new Action(() => render.Invoke(form, new object?[] { })));
        }
        catch
        {
            // The primary scanner remains usable even when this best-effort pass fails.
        }
    }

    static List<Game> ScanAllFixedDrives()
    {
        var byName = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(drive.RootDirectory.FullName, "*.exe", options); }
            catch { continue; }

            foreach (var exe in files)
            {
                if (!IsCandidate(exe)) continue;
                var name = GetGameName(exe);
                if (string.IsNullOrWhiteSpace(name) || IsGenericOrSystemName(name)) continue;

                var install = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(install)) continue;

                var option = new LaunchOption(FriendlyOptionName(exe), "exe", exe, install);
                if (!byName.TryGetValue(name, out var game))
                {
                    byName[name] = new Game(name, "SCAN PC", install, new List<LaunchOption> { option }, exe);
                }
                else if (!game.Options.Any(o => string.Equals(o.Target, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    game.Options.Add(option);
                }
            }
        }

        return byName.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsCandidate(string path)
    {
        var file = Path.GetFileName(path);
        if (IgnoredExeNames.Contains(file)) return false;
        if (IgnoredPathParts.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    static string? GetGameName(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var product = info.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product)) return product;
            var description = info.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch { }

        return Path.GetFileNameWithoutExtension(exe);
    }

    static bool IsGenericOrSystemName(string name)
    {
        if (name.Length < 2) return true;
        var n = name.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^v?\d+(\.\d+){0,5}$")) return true;
        var bad = new[]
        {
            "Microsoft", "Windows", "Visual C++", "Visual Studio", "Redistributable", "DirectX",
            "Runtime", "NVIDIA", "AMD", "Intel", "Realtek", "Overwolf Benchmarking", "Crash Handler",
            "Unreal Engine Prerequisites"
        };
        return bad.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    static string FriendlyOptionName(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe).Replace('_', ' ').Replace('-', ' ').Trim();
        return name.Length > 0 ? name : "Application";
    }

    static void SetStatus(MainForm form, string text)
    {
        try
        {
            var field = typeof(MainForm).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(form) is Label label)
            {
                if (label.InvokeRequired)
                    label.BeginInvoke(new Action(() => label.Text = text));
                else
                    label.Text = text;
            }
        }
        catch { }
    }
}
