using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows.Forms;

internal sealed record LaunchOption(string Name, string Kind, string? Target, string? WorkingDirectory);
internal sealed record Game(string Name, string Source, string? InstallPath, List<LaunchOption> Options, string? IconPath = null);

internal enum UpdateResult { UpToDate, Updated, NoPublishedBuild, NotWritable, Failed, DevBuild }

internal static class LauncherUpdater
{
    const string CommitApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/commits/main";
    const string ReleaseApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/releases/tags/latest";
    const string ExeName = "MataiasuLauncher.exe";
    const string CommitName = "MataiasuLauncher.commit.txt";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MataiasuLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<UpdateResult> CheckAndApplyAsync(bool interactive = false)
    {
        if (BuildInfo.Commit.Equals("dev", StringComparison.OrdinalIgnoreCase)) return UpdateResult.DevBuild;
        try
        {
            var currentCommit = BuildInfo.Commit.Trim();
            var latestCommit = await GetLatestCommitAsync();
            if (string.IsNullOrWhiteSpace(latestCommit)) return UpdateResult.Failed;
            if (latestCommit.Equals(currentCommit, StringComparison.OrdinalIgnoreCase)) return UpdateResult.UpToDate;

            var release = await GetLatestReleaseAsync();
            if (release is null || !release.Value.TryGetProperty("assets", out var assets)) return UpdateResult.NoPublishedBuild;

            string? exeUrl = null;
            string? releaseCommit = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                if (string.Equals(name, CommitName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    releaseCommit = (await Http.GetStringAsync(url)).Trim();
            }
            if (string.IsNullOrWhiteSpace(exeUrl) || !latestCommit.Equals(releaseCommit, StringComparison.OrdinalIgnoreCase)) return UpdateResult.NoPublishedBuild;

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe)) return UpdateResult.Failed;
            var currentDir = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrWhiteSpace(currentDir) || !CanWriteDirectory(currentDir)) return UpdateResult.NotWritable;

            var tempRoot = Path.Combine(Path.GetTempPath(), "MataiasuLauncher", "update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var downloaded = Path.Combine(tempRoot, ExeName);
            using (var response = await Http.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(downloaded);
                await input.CopyToAsync(output);
            }
            if (!File.Exists(downloaded) || new FileInfo(downloaded).Length < 1_000_000) return UpdateResult.Failed;

            var script = Path.Combine(tempRoot, "apply-update.ps1");
            var safeDownloaded = downloaded.Replace("'", "''");
            var safeCurrent = currentExe.Replace("'", "''");
            var safeTemp = tempRoot.Replace("'", "''");
            var scriptContent = $@"$src='{safeDownloaded}'
$dst='{safeCurrent}'
$tmp='{safeTemp}'
Start-Sleep -Milliseconds 900
$ok=$false
for($i=0;$i -lt 60;$i++) {{
  try {{
    Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
    if((Get-Item $dst).Length -eq (Get-Item $src).Length) {{ $ok=$true; break }}
  }} catch {{}}
  Start-Sleep -Seconds 1
}}
if($ok) {{ Start-Process -FilePath $dst }}
else {{ Start-Process -FilePath $dst }}
Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
";
            await File.WriteAllTextAsync(script, scriptContent);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return UpdateResult.Updated;
        }
        catch { return UpdateResult.Failed; }
    }

    static bool CanWriteDirectory(string directory)
    {
        try
        {
            var test = Path.Combine(directory, ".mataiasu_write_test_" + Guid.NewGuid().ToString("N"));
            using (File.Create(test)) { }
            File.Delete(test);
            return true;
        }
        catch { return false; }
    }

    static async Task<string?> GetLatestCommitAsync()
    {
        using var response = await Http.GetAsync(CommitApi);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
    }

    static async Task<JsonElement?> GetLatestReleaseAsync()
    {
        using var response = await Http.GetAsync(ReleaseApi);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}

internal static class GameScanner
{
    static readonly string[] IgnoredExeNames =
    {
        "unins000.exe", "uninstall.exe", "uninstaller.exe", "setup.exe", "install.exe", "update.exe", "updater.exe",
        "launcherupdate.exe", "crashreportclient.exe", "unitycrashhandler32.exe", "unitycrashhandler64.exe",
        "ue-prereqsetup_x64.exe", "dotnet.exe", "msbuild.exe", "devenv.exe", "explorer.exe", "python.exe", "node.exe",
        "java.exe", "javaw.exe"
    };

    static readonly string[] IgnoredPathParts =
    {
        "\\Windows\\", "\\WinSxS\\", "\\System32\\", "\\SysWOW64\\", "\\Microsoft.NET\\", "\\WindowsApps\\",
        "\\NuGetFallbackFolder\\", "\\node_modules\\", "\\Visual Studio\\", "\\Common Files\\", "\\Windows Kits\\", "\\dotnet\\"
    };

    public static List<Game> Scan(bool deep = true)
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        ScanUninstallKeys(games, RegistryHive.LocalMachine);
        ScanUninstallKeys(games, RegistryHive.CurrentUser);
        ScanSteam(games);
        ScanEpic(games);
        if (deep) ScanDetectedExeRoots(games);
        return games.Values.Where(g => g.Options.Count > 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Add(Dictionary<string, Game> games, Game game)
    {
        var name = CleanDisplayName(game.Name);
        if (string.IsNullOrWhiteSpace(name) || game.Options.Count == 0 || IsBadGameName(name)) return;
        game = game with { Name = name };
        if (!games.TryGetValue(name, out var existing)) { games[name] = game; return; }
        var options = existing.Options.Concat(game.Options).GroupBy(o => $"{o.Kind}|{o.Target}", StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        games[name] = existing with
        {
            Options = options,
            InstallPath = existing.InstallPath ?? game.InstallPath,
            IconPath = existing.IconPath ?? game.IconPath,
            Source = existing.Source.Contains(game.Source, StringComparison.OrdinalIgnoreCase) ? existing.Source : existing.Source + " + " + game.Source
        };
    }

    static void ScanUninstallKeys(Dictionary<string, Game> games, RegistryHive hive)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key == null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        var name = sub?.GetValue("DisplayName") as string;
                        var install = NormalizeDirectory(sub?.GetValue("InstallLocation") as string);
                        var iconExe = NormalizeExePath(sub?.GetValue("DisplayIcon") as string);
                        var options = new List<LaunchOption>();
                        if (iconExe != null) options.Add(new LaunchOption("Application", "exe", iconExe, Path.GetDirectoryName(iconExe)));
                        if (install != null)
                        {
                            foreach (var exe in FindBestExecutables(install, 8)) AddExeOption(options, exe, install);
                            AddSpecialLaunchers(options, install);
                        }
                        if (!string.IsNullOrWhiteSpace(name)) Add(games, new Game(name, "Installé", install, options, iconExe));
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    static void ScanSteam(Dictionary<string, Game> games)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };
        TryAddSteamRoot(RegistryHive.CurrentUser, roots); TryAddSteamRoot(RegistryHive.LocalMachine, roots);
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var library in ReadSteamLibraries(root))
        {
            var apps = Path.Combine(library, "steamapps"); if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest); var name = ParseAcfValue(text, "name"); var appId = ParseAcfValue(text, "appid"); var installDir = ParseAcfValue(text, "installdir");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                    var path = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(library, "steamapps", "common", installDir);
                    var options = new List<LaunchOption> { new("Steam", "uri", $"steam://rungameid/{appId}", path) };
                    if (path != null) { foreach (var exe in FindBestExecutables(path, 12)) AddExeOption(options, exe, path); AddSpecialLaunchers(options, path); }
                    Add(games, new Game(name, "Steam", path, options, FindFirstIcon(path)));
                }
                catch { }
            }
        }
    }

    static void ScanEpic(Dictionary<string, Game> games)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Data", "Manifests")
        };
        var root = roots.FirstOrDefault(Directory.Exists); if (root == null) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file)); var r = doc.RootElement;
                var name = GetString(r, "DisplayName"); var install = NormalizeDirectory(GetString(r, "InstallLocation")); var launchExe = GetString(r, "LaunchExecutable"); var appName = GetString(r, "AppName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var options = new List<LaunchOption>();
                if (!string.IsNullOrWhiteSpace(appName)) options.Add(new LaunchOption("Epic Games", "uri", $"com.epicgames.launcher://apps/{appName}?action=launch", install));
                if (install != null && !string.IsNullOrWhiteSpace(launchExe)) { var direct = Path.Combine(install, launchExe); if (File.Exists(direct)) options.Add(new LaunchOption("Direct", "exe", direct, install)); }
                if (install != null) { foreach (var exe in FindBestExecutables(install, 12)) AddExeOption(options, exe, install); AddSpecialLaunchers(options, install); }
                Add(games, new Game(name, "Epic Games", install, options, FindFirstIcon(install)));
            }
            catch { }
        }
    }

    static void ScanDetectedExeRoots(Dictionary<string, Game> games)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf)) roots.Add(pf); if (!string.IsNullOrWhiteSpace(pfx86)) roots.Add(pfx86);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games")); roots.Add(Path.Combine(drive.RootDirectory.FullName, "Jeux"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary")); roots.Add(Path.Combine(drive.RootDirectory.FullName, "Epic Games")); roots.Add(Path.Combine(drive.RootDirectory.FullName, "GOG Games"));
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); foreach (var name in new[] { "Games", "Jeux", "My Games" }) roots.Add(Path.Combine(user, name));

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Take(30000))
                {
                    if (!IsCandidateExe(exe) || FileLengthSafe(exe) < 700_000) continue;
                    var product = GetProductName(exe); var gameName = !string.IsNullOrWhiteSpace(product) ? product : GetInstallFolderName(root, exe);
                    gameName = CleanDisplayName(gameName); if (string.IsNullOrWhiteSpace(gameName) || !LooksLikeGameExecutable(exe, gameName)) continue;
                    var folder = Path.GetDirectoryName(exe); if (string.IsNullOrWhiteSpace(folder)) continue;
                    var options = new List<LaunchOption>(); AddExeOption(options, exe, folder);
                    if (options.Count > 0) Add(games, new Game(gameName, "EXE détecté", folder, options, exe));
                }
            }
            catch { }
        }
    }

    static bool LooksLikeGameExecutable(string exe, string gameName)
    {
        if (IsBadGameName(gameName)) return false;
        var file = Path.GetFileNameWithoutExtension(exe);
        if (file.Contains("benchmark", StringComparison.OrdinalIgnoreCase)) return false;
        if (gameName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || gameName.Contains("Overwolf", StringComparison.OrdinalIgnoreCase) || gameName.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static string? GetProductName(string exe)
    {
        try { var info = FileVersionInfo.GetVersionInfo(exe); return !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : info.FileDescription?.Trim(); }
        catch { return null; }
    }

    static string GetInstallFolderName(string root, string exe)
    {
        var dir = Path.GetDirectoryName(exe) ?? root; var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); var current = dir;
        for (var i = 0; i < 3 && !current.Equals(rootFull, StringComparison.OrdinalIgnoreCase); i++) current = Path.GetDirectoryName(current) ?? current;
        return Path.GetFileName(current);
    }

    static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern, SearchOption option) { try { return Directory.EnumerateFiles(dir, pattern, option); } catch { return Array.Empty<string>(); } }
    static IEnumerable<string> FindBestExecutables(string install, int max) => SafeEnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly).Where(IsCandidateExe).OrderByDescending(ExecutableScore).ThenByDescending(FileLengthSafe).Take(max);
    static int ExecutableScore(string path) { var score = 0; var file = Path.GetFileNameWithoutExtension(path); if (file.Contains("game", StringComparison.OrdinalIgnoreCase)) score += 40; if (file.Contains("win64", StringComparison.OrdinalIgnoreCase)) score += 15; if (file.Contains("shipping", StringComparison.OrdinalIgnoreCase)) score += 10; if (GetProductName(path) != null) score += 20; score += (int)Math.Min(30, FileLengthSafe(path) / 50_000_000); return score; }

    static void AddExeOption(List<LaunchOption> options, string exe, string? workingDirectory) { if (!File.Exists(exe) || options.Any(o => string.Equals(o.Target, exe, StringComparison.OrdinalIgnoreCase))) return; var label = FriendlyExeName(Path.GetFileNameWithoutExtension(exe)); if (label.Equals("Stardew Valley", StringComparison.OrdinalIgnoreCase)) label = "Normal"; options.Add(new LaunchOption(label, "exe", exe, workingDirectory)); }
    static void AddSpecialLaunchers(List<LaunchOption> options, string install) { var smapi = SafeEnumerateFiles(install, "StardewModdingAPI.exe", SearchOption.TopDirectoryOnly).FirstOrDefault(); if (smapi != null) AddExeOptionWithName(options, "SMAPI / Mods", smapi, install); }
    static void AddExeOptionWithName(List<LaunchOption> options, string name, string exe, string workingDirectory) { if (File.Exists(exe) && !options.Any(o => string.Equals(o.Target, exe, StringComparison.OrdinalIgnoreCase))) options.Add(new LaunchOption(name, "exe", exe, workingDirectory)); }
    static bool IsCandidateExe(string path) { var file = Path.GetFileName(path); return !IgnoredExeNames.Any(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase)) && !IgnoredPathParts.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)); }
    static bool IsBadGameName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return true; if (Regex.IsMatch(name.Trim(), @"^v?\d+(\.\d+){0,5}$", RegexOptions.IgnoreCase)) return true; var bad = new[] { "Microsoft Visual C++", "Microsoft .NET", "Redistributable", "DirectX", "OverwolfBenchmarking" }; return bad.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)); }
    static string CleanDisplayName(string? value) { if (string.IsNullOrWhiteSpace(value)) return string.Empty; return Regex.Replace(value.Trim().Replace('_', ' '), @"\s+", " "); }
    static string FriendlyExeName(string value) => CleanDisplayName(value.Replace('-', ' '));
    static string? NormalizeDirectory(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var p = value.Trim().Trim('"'); return Directory.Exists(p) ? p : null; }
    static string? NormalizeExePath(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var p = value.Split(',')[0].Trim().Trim('"'); return File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null; }
    static long FileLengthSafe(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    static string? FindFirstIcon(string? path) { if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null; return SafeEnumerateFiles(path, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault(IsCandidateExe); }
    static void TryAddSteamRoot(RegistryHive hive, HashSet<string> roots) { try { using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(@"SOFTWARE\Valve\Steam"); var path = key?.GetValue("InstallPath") as string; if (!string.IsNullOrWhiteSpace(path)) roots.Add(path); } catch { } }
    static IEnumerable<string> ReadSteamLibraries(string steamRoot) { var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (Directory.Exists(steamRoot) && result.Add(steamRoot)) yield return steamRoot; var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"); if (!File.Exists(vdf)) yield break; foreach (var line in File.ReadLines(vdf)) { if (line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) < 0) continue; var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries); if (parts.Length < 4) continue; var candidate = parts[^1].Replace("\\\\", "\\"); if (Directory.Exists(candidate) && result.Add(candidate)) yield return candidate; } }
    static string? ParseAcfValue(string text, string key) { var marker = $"\"{key}\""; var i = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (i < 0) return null; var rest = text[(i + marker.Length)..]; var q1 = rest.IndexOf('"'); if (q1 < 0) return null; var q2 = rest.IndexOf('"', q1 + 1); return q2 < 0 ? null : rest[(q1 + 1)..q2]; }
    static string? GetString(JsonElement root, string property) => root.TryGetProperty(property, out var p) ? p.GetString() : null;
}

internal sealed class MainForm : Form
{
    readonly FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), WrapContents = true };
    readonly TextBox search = new() { Width = 310, Height = 34, PlaceholderText = "Rechercher un jeu...", BorderStyle = BorderStyle.FixedSingle };
    readonly Button scan = new() { Text = "⟳  SCAN COMPLET", Width = 150, Height = 38, FlatStyle = FlatStyle.Flat };
    readonly Button update = new() { Text = "↻  MISE À JOUR", Width = 150, Height = 38, FlatStyle = FlatStyle.Flat };
    readonly Button play = new() { Text = "▶  JOUER", Width = 190, Height = 48, FlatStyle = FlatStyle.Flat };
    readonly ComboBox mode = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ListBox libraries = new() { BorderStyle = BorderStyle.None, IntegralHeight = false };
    readonly Label status = new() { AutoSize = true, Text = "Prêt" };
    readonly Label selectedTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold) };
    readonly Label selectedInfo = new() { AutoSize = true, MaximumSize = new Size(600, 55) };
    readonly Panel selectedIcon = new() { Width = 54, Height = 54 };
    readonly Panel detail = new() { Dock = DockStyle.Bottom, Height = 118, Padding = new Padding(18) };
    readonly Panel sidebar = new() { Dock = DockStyle.Left, Width = 235, Padding = new Padding(16, 18, 12, 14) };
    List<Game> games = new(); Game? selected; string selectedLibrary = "Tous les jeux"; bool scanning;

    static readonly Color Bg = Color.FromArgb(14, 12, 20), PanelBg = Color.FromArgb(24, 21, 34), Panel2 = Color.FromArgb(31, 27, 45), SidebarBg = Color.FromArgb(20, 17, 29), Accent = Color.FromArgb(163, 92, 255), TextColor = Color.FromArgb(245, 242, 250), Muted = Color.FromArgb(166, 158, 181);

    public MainForm()
    {
        Text = "Mataiasu Launcher"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(1050, 700); ClientSize = new Size(1360, 820); BackColor = Bg; ForeColor = TextColor; Font = new Font("Segoe UI", 10f);
        LibraryStore.Create("★ Favoris"); BuildSidebar();

        var header = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(26, 18, 20, 14), BackColor = PanelBg };
        var logo = new Label { Text = "MATAIASU", AutoSize = true, Font = new Font("Segoe UI Semibold", 25, FontStyle.Bold), ForeColor = Accent, Location = new Point(26, 12) };
        var sub = new Label { Text = "GAME LIBRARY", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Muted, Location = new Point(29, 51) };
        search.Location = new Point(430, 31); search.BackColor = Panel2; search.ForeColor = TextColor; scan.Location = new Point(765, 29); update.Location = new Point(930, 29); StyleButton(scan); StyleButton(update);
        search.TextChanged += (_, _) => RenderCards(); scan.Click += async (_, _) => await ScanAsync(); update.Click += async (_, _) => await ManualUpdateAsync(); header.Controls.AddRange(new Control[] { logo, sub, search, scan, update });

        detail.BackColor = PanelBg; selectedIcon.BackColor = Panel2; selectedIcon.Location = new Point(18, 24); selectedTitle.Location = new Point(92, 17); selectedInfo.Location = new Point(92, 47); selectedInfo.ForeColor = Muted; mode.Location = new Point(720, 36); mode.BackColor = Panel2; mode.ForeColor = TextColor; play.Location = new Point(960, 28); StyleButton(play); play.Size = new Size(190, 48); play.Click += (_, _) => LaunchSelected(); mode.SelectedIndexChanged += (_, _) => UpdateSelectedInfo(); detail.Controls.AddRange(new Control[] { selectedIcon, selectedTitle, selectedInfo, mode, play });

        status.Dock = DockStyle.Top; status.Height = 24; status.Padding = new Padding(24, 4, 0, 0); status.ForeColor = Muted; status.BackColor = Bg;
        Controls.Add(cards); Controls.Add(detail); Controls.Add(status); Controls.Add(header); Controls.Add(sidebar); sidebar.BringToFront(); Shown += async (_, _) => await ScanAsync();
    }

    void BuildSidebar()
    {
        sidebar.BackColor = SidebarBg;
        var heading = new Label { Text = "MA BIBLIOTHÈQUE", Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Muted };
        libraries.Dock = DockStyle.Fill; libraries.BackColor = SidebarBg; libraries.ForeColor = TextColor; libraries.Font = new Font("Segoe UI", 10f); libraries.SelectedIndexChanged += (_, _) => { if (libraries.SelectedItem is string name) { selectedLibrary = name; RenderCards(); } };
        var manage = new Button { Text = "⚙  GÉRER LES BIBLIOTHÈQUES", Dock = DockStyle.Bottom, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = TextColor }; manage.FlatAppearance.BorderSize = 0; manage.Click += (_, _) => ManageLibraries();
        var create = new Button { Text = "+  CRÉER UNE BIBLIOTHÈQUE", Dock = DockStyle.Bottom, Height = 42, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White }; create.FlatAppearance.BorderSize = 0; create.Click += (_, _) => CreateLibrary();
        var sideStatus = new Label { Text = "Organise tes jeux comme tu veux.", Dock = DockStyle.Bottom, Height = 32, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
        sidebar.Controls.Add(libraries); sidebar.Controls.Add(manage); sidebar.Controls.Add(create); sidebar.Controls.Add(sideStatus); sidebar.Controls.Add(heading); RefreshLibraries();
    }

    void RefreshLibraries()
    {
        var names = new List<string> { "Tous les jeux", "★ Favoris", "Non classés" }; names.AddRange(LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).OrderBy(x => x));
        var old = selectedLibrary; libraries.BeginUpdate(); libraries.Items.Clear(); libraries.Items.AddRange(names.Cast<object>().ToArray()); libraries.EndUpdate(); selectedLibrary = names.FirstOrDefault(x => x.Equals(old, StringComparison.OrdinalIgnoreCase)) ?? names[0]; libraries.SelectedItem = selectedLibrary;
    }

    void RenderCards()
    {
        var term = search.Text.Trim(); var filtered = games.Where(g => (string.IsNullOrWhiteSpace(term) || g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) && IsInSelectedLibrary(g.Name)).ToList();
        cards.SuspendLayout(); foreach (Control old in cards.Controls.Cast<Control>().ToList()) old.Dispose(); cards.Controls.Clear(); foreach (var game in filtered) cards.Controls.Add(CreateGameCard(game)); cards.ResumeLayout();
        if (selected == null || !filtered.Any(g => g.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))) SelectGame(filtered.FirstOrDefault()); status.Text = $"{filtered.Count} jeux visibles • {games.Sum(g => g.Options.Count)} modes de lancement";
    }

    bool IsInSelectedLibrary(string game) { if (selectedLibrary.Equals("Tous les jeux", StringComparison.OrdinalIgnoreCase)) return true; if (selectedLibrary.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)) return LibraryStore.IsFavorite(game); if (selectedLibrary.Equals("Non classés", StringComparison.OrdinalIgnoreCase)) return !LibraryStore.Load().Any(l => l.Games.Any(g => g.Equals(game, StringComparison.OrdinalIgnoreCase))); return LibraryStore.Contains(selectedLibrary, game); }

    Control CreateGameCard(Game game)
    {
        var card = new Panel { Width = 275, Height = 165, Margin = new Padding(10), BackColor = PanelBg, Cursor = Cursors.Hand, Tag = game };
        var accent = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = game.Source.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? Accent : Color.FromArgb(90, 83, 110) };
        var icon = new Panel { Location = new Point(18, 18), Size = new Size(54, 54), BackColor = Panel2 }; DrawIcon(icon, game.IconPath);
        var title = new Label { Text = game.Name, Location = new Point(86, 18), Size = new Size(165, 45), Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold), ForeColor = TextColor, AutoEllipsis = true };
        var src = new Label { Text = game.Source.ToUpperInvariant(), Location = new Point(86, 64), Size = new Size(165, 20), Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Muted };
        var modes = new Label { Text = $"{game.Options.Count} mode{(game.Options.Count > 1 ? "s" : "")} de lancement", Location = new Point(18, 94), Size = new Size(240, 24), ForeColor = Muted };
        var launch = new Button { Text = game.Options.Count > 1 ? "CHOISIR ▶" : "LANCER ▶", Location = new Point(18, 124), Size = new Size(235, 30), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = TextColor }; launch.FlatAppearance.BorderColor = Color.FromArgb(70, 62, 90);
        card.Controls.AddRange(new Control[] { accent, icon, title, src, modes, launch }); EventHandler select = (_, _) => SelectGame(game); foreach (var control in new Control[] { card, icon, title, src, modes }) control.Click += select; launch.Click += (_, _) => { SelectGame(game); LaunchSelected(); };
        card.ContextMenuStrip = BuildContextMenu(game); foreach (Control control in card.Controls) control.ContextMenuStrip = card.ContextMenuStrip; return card;
    }

    ContextMenuStrip BuildContextMenu(Game game)
    {
        var menu = new ContextMenuStrip { BackColor = Panel2, ForeColor = TextColor }; var add = new ToolStripMenuItem("Ajouter / retirer d'une bibliothèque");
        foreach (var lib in LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase))) { var item = new ToolStripMenuItem(lib.Name) { Checked = LibraryStore.Contains(lib.Name, game.Name) }; item.Click += (_, _) => { if (LibraryStore.Contains(lib.Name, game.Name)) LibraryStore.RemoveGame(lib.Name, game.Name); else LibraryStore.AddGame(lib.Name, game.Name); RefreshLibraries(); RenderCards(); }; add.DropDownItems.Add(item); }
        var newLib = new ToolStripMenuItem("Créer une bibliothèque…"); newLib.Click += (_, _) => CreateLibrary(); add.DropDownItems.Add(newLib); menu.Items.Add(add);
        var fav = new ToolStripMenuItem(LibraryStore.IsFavorite(game.Name) ? "Retirer des favoris" : "Ajouter aux favoris"); fav.Click += (_, _) => { if (LibraryStore.IsFavorite(game.Name)) LibraryStore.RemoveFavorite(game.Name); else LibraryStore.AddFavorite(game.Name); RefreshLibraries(); RenderCards(); }; menu.Items.Add(fav);
        var folder = new ToolStripMenuItem("Ouvrir le dossier du jeu"); folder.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath)) Process.Start(new ProcessStartInfo { FileName = game.InstallPath, UseShellExecute = true }); }; menu.Items.Add(folder); return menu;
    }

    void SelectGame(Game? game)
    {
        selected = game; if (game == null) { selectedTitle.Text = "Sélectionne un jeu"; selectedInfo.Text = ""; mode.Items.Clear(); return; }
        selectedTitle.Text = game.Name; mode.BeginUpdate(); mode.Items.Clear(); foreach (var option in game.Options) mode.Items.Add(option.Name); mode.EndUpdate(); mode.SelectedIndex = 0; DrawIcon(selectedIcon, game.IconPath); UpdateSelectedInfo();
    }

    void UpdateSelectedInfo() { if (selected == null || mode.SelectedIndex < 0) { selectedInfo.Text = ""; return; } var option = selected.Options[mode.SelectedIndex]; selectedInfo.Text = $"{selected.Source}  •  {option.Name}\n{selected.InstallPath ?? option.Target ?? "Emplacement inconnu"}"; }

    void LaunchSelected()
    {
        if (selected == null || mode.SelectedIndex < 0) return; var option = selected.Options[mode.SelectedIndex];
        try
        {
            if (option.Kind == "uri") { Process.Start(new ProcessStartInfo { FileName = option.Target, UseShellExecute = true }); return; }
            if (!string.IsNullOrWhiteSpace(option.Target) && File.Exists(option.Target)) { Process.Start(new ProcessStartInfo { FileName = option.Target, WorkingDirectory = option.WorkingDirectory ?? Path.GetDirectoryName(option.Target), UseShellExecute = true }); status.Text = $"Lancé : {selected.Name} • {option.Name}"; return; }
            MessageBox.Show("Le fichier de lancement n'existe plus. Lance un nouveau scan.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Impossible de lancer le jeu", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task ScanAsync()
    {
        if (scanning) return; scanning = true; scan.Enabled = false; scan.Text = "SCAN..."; status.Text = "Analyse des jeux et des bibliothèques..."; await Task.Yield();
        try { games = await Task.Run(() => GameScanner.Scan(true)); RefreshLibraries(); RenderCards(); } catch (Exception ex) { status.Text = "Erreur de scan : " + ex.Message; } finally { scanning = false; scan.Enabled = true; scan.Text = "⟳  SCAN COMPLET"; }
    }

    async Task ManualUpdateAsync()
    {
        update.Enabled = false; update.Text = "VÉRIFICATION..."; status.Text = "Recherche d'une nouvelle version..."; var result = await LauncherUpdater.CheckAndApplyAsync(true);
        if (result == UpdateResult.Updated) { status.Text = "Mise à jour téléchargée. Redémarrage..."; BeginInvoke(new Action(Close)); return; }
        status.Text = result switch { UpdateResult.UpToDate => "Le launcher est déjà à jour.", UpdateResult.NoPublishedBuild => "Une mise à jour est disponible, mais son build n'est pas encore publié.", UpdateResult.NotWritable => "Le dossier du launcher n'est pas accessible en écriture. Déplace l'EXE dans un dossier utilisateur.", UpdateResult.DevBuild => "Cette version locale n'est pas une version publiée.", _ => "Impossible de vérifier ou d'installer la mise à jour." };
        update.Enabled = true; update.Text = "↻  MISE À JOUR";
    }

    void CreateLibrary()
    {
        using var dialog = new Form { Text = "Nouvelle bibliothèque", Width = 430, Height = 190, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = PanelBg, ForeColor = TextColor };
        var label = new Label { Text = "Nom de la bibliothèque", Left = 22, Top = 18, AutoSize = true }; var input = new TextBox { Left = 22, Top = 48, Width = 365, BackColor = Panel2, ForeColor = TextColor };
        var ok = new Button { Text = "Créer", Left = 220, Top = 92, Width = 80, DialogResult = DialogResult.OK, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; var cancel = new Button { Text = "Annuler", Left = 307, Top = 92, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat }; ok.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderSize = 0;
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) { var name = input.Text.Trim(); if (!string.IsNullOrWhiteSpace(name) && !name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)) { if (!LibraryStore.Create(name)) MessageBox.Show("Cette bibliothèque existe déjà.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information); RefreshLibraries(); RenderCards(); } }
    }

    void ManageLibraries()
    {
        var libs = LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)).ToList();
        using var dialog = new Form { Text = "Gérer les bibliothèques", Width = 480, Height = 430, StartPosition = FormStartPosition.CenterParent, BackColor = PanelBg, ForeColor = TextColor };
        var list = new ListBox { Dock = DockStyle.Top, Height = 300, BackColor = Panel2, ForeColor = TextColor, BorderStyle = BorderStyle.None }; list.Items.AddRange(libs.Select(x => $"{x.Name}   ·   {x.Games.Count} jeu(x)").Cast<object>().ToArray());
        var delete = new Button { Text = "Supprimer la bibliothèque sélectionnée", Dock = DockStyle.Bottom, Height = 42, BackColor = Color.FromArgb(70, 48, 84), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; delete.FlatAppearance.BorderSize = 0;
        delete.Click += (_, _) => { if (list.SelectedIndex < 0) return; var lib = libs[list.SelectedIndex]; if (MessageBox.Show($"Supprimer « {lib.Name} » ? Les jeux ne seront pas désinstallés.", "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return; LibraryStore.Delete(lib.Name); dialog.Close(); RefreshLibraries(); RenderCards(); };
        dialog.Controls.Add(delete); dialog.Controls.Add(list); dialog.ShowDialog(this);
    }

    static void StyleButton(Button button) { button.BackColor = Accent; button.ForeColor = Color.White; button.FlatAppearance.BorderSize = 0; }
    static void DrawIcon(Panel panel, string? exePath) { panel.BackgroundImage?.Dispose(); panel.BackgroundImage = null; if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) { panel.Invalidate(); return; } try { using var icon = Icon.ExtractAssociatedIcon(exePath); if (icon != null) panel.BackgroundImage = icon.ToBitmap(); panel.BackgroundImageLayout = ImageLayout.Stretch; } catch { } }
}

internal static class Program
{
    [STAThread]
    static async Task Main()
    {
        ApplicationConfiguration.Initialize();
        var result = await LauncherUpdater.CheckAndApplyAsync();
        if (result == UpdateResult.Updated) return;
        Application.Run(new MainForm());
    }
}
