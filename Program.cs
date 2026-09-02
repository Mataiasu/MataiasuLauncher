using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows.Forms;

internal sealed record LaunchOption(string Name, string Kind, string? Target, string? WorkingDirectory);
internal sealed record Game(string Name, string Source, string? InstallPath, List<LaunchOption> Options, string? IconPath = null);

internal static class LauncherUpdater
{
    const string AssetName = "MataiasuLauncher.exe";
    const string CommitApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/commits/main";
    const string ReleaseApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/releases/tags/latest";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MataiasuLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<bool> CheckAndApplyAsync()
    {
        if (BuildInfo.Commit.Equals("dev", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var latestCommit = await GetLatestCommitAsync();
            if (string.IsNullOrWhiteSpace(latestCommit) || latestCommit.Equals(BuildInfo.Commit, StringComparison.OrdinalIgnoreCase)) return false;
            using var response = await Http.GetAsync(ReleaseApi);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return false;

            string? assetUrl = null;
            string? releaseCommit = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) assetUrl = url;
                if (string.Equals(name, "MataiasuLauncher.commit.txt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    releaseCommit = (await Http.GetStringAsync(url)).Trim();
            }
            if (string.IsNullOrWhiteSpace(assetUrl) || !latestCommit.Equals(releaseCommit, StringComparison.OrdinalIgnoreCase)) return false;

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe)) return false;
            var tempRoot = Path.Combine(Path.GetTempPath(), "MataiasuLauncher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var downloadedExe = Path.Combine(tempRoot, AssetName);
            using (var download = await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();
                await using var input = await download.Content.ReadAsStreamAsync();
                await using var output = File.Create(downloadedExe);
                await input.CopyToAsync(output);
            }

            var script = Path.Combine(tempRoot, "update.cmd");
            await File.WriteAllTextAsync(script,
                "@echo off\r\n" +
                ":retry\r\n" +
                $"copy /Y \"{downloadedExe}\" \"{currentExe}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto retry)\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                $"del \"{downloadedExe}\" >nul 2>&1\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
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
}

internal static class GameScanner
{
    static readonly string[] IgnoredExeNames =
    {
        "unins000.exe", "uninstall.exe", "uninstaller.exe", "setup.exe", "install.exe", "update.exe",
        "crashreportclient.exe", "unitycrashhandler32.exe", "unitycrashhandler64.exe", "ue-prereqsetup_x64.exe",
        "dotnet.exe", "msbuild.exe", "devenv.exe", "explorer.exe"
    };

    static readonly string[] IgnoredPathParts =
    {
        "\\Windows\\", "\\WinSxS\\", "\\System32\\", "\\SysWOW64\\", "\\Microsoft.NET\\",
        "\\WindowsApps\\", "\\NuGetFallbackFolder\\", "\\node_modules\\", "\\Visual Studio\\"
    };

    public static List<Game> Scan(bool deep = true)
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        ScanUninstallKeys(games, RegistryHive.LocalMachine);
        ScanUninstallKeys(games, RegistryHive.CurrentUser);
        ScanSteam(games);
        ScanEpic(games);
        if (deep) ScanCommonGameFolders(games);
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Add(Dictionary<string, Game> games, Game game)
    {
        if (string.IsNullOrWhiteSpace(game.Name) || game.Options.Count == 0) return;
        if (game.Name.Contains("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains("Microsoft .NET", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) return;

        var key = game.Name.Trim();
        if (!games.TryGetValue(key, out var existing))
        {
            games[key] = game;
            return;
        }
        var options = existing.Options.Concat(game.Options)
            .GroupBy(o => (o.Kind, o.Target), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        games[key] = existing with
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
                ScanUninstallPath(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", games);
            }
            catch { }
        }
    }

    static void ScanUninstallPath(RegistryKey root, string path, Dictionary<string, Game> games)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                var name = sub?.GetValue("DisplayName") as string;
                var install = sub?.GetValue("InstallLocation") as string;
                var icon = sub?.GetValue("DisplayIcon") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                install = NormalizeExistingDirectory(install);
                var iconExe = NormalizeExePath(icon);
                var options = new List<LaunchOption>();
                if (iconExe != null) options.Add(new LaunchOption("Application", "exe", iconExe, Path.GetDirectoryName(iconExe)));
                if (install != null)
                {
                    foreach (var exe in FindBestExecutables(install, 4)) AddExeOption(options, exe, install);
                    AddSpecialLaunchers(options, install);
                }
                Add(games, new Game(name.Trim(), "Installé", install, options, iconExe));
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
        TryAddSteamRoot(RegistryHive.CurrentUser, roots);
        TryAddSteamRoot(RegistryHive.LocalMachine, roots);

        foreach (var root in roots.Where(Directory.Exists))
        foreach (var lib in ReadSteamLibraries(root))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var name = ParseAcfValue(text, "name");
                    var appId = ParseAcfValue(text, "appid");
                    var installDir = ParseAcfValue(text, "installdir");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                    var path = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(lib, "steamapps", "common", installDir);
                    var options = new List<LaunchOption> { new("Steam", "uri", $"steam://rungameid/{appId}", path) };
                    if (path != null)
                    {
                        foreach (var exe in FindBestExecutables(path, 5)) AddExeOption(options, exe, path);
                        AddSpecialLaunchers(options, path);
                    }
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
        var root = roots.FirstOrDefault(Directory.Exists);
        if (root == null) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var name = GetString(r, "DisplayName");
                var install = GetString(r, "InstallLocation");
                var exeName = GetString(r, "LaunchExecutable");
                var appName = GetString(r, "AppName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                install = NormalizeExistingDirectory(install);
                var options = new List<LaunchOption>();
                if (!string.IsNullOrWhiteSpace(appName)) options.Add(new LaunchOption("Epic Games", "uri", $"com.epicgames.launcher://apps/{appName}?action=launch", install));
                if (install != null && !string.IsNullOrWhiteSpace(exeName))
                {
                    var exe = Path.Combine(install, exeName);
                    if (File.Exists(exe)) options.Add(new LaunchOption("Direct", "exe", exe, install));
                }
                if (install != null)
                {
                    foreach (var exe in FindBestExecutables(install, 5)) AddExeOption(options, exe, install);
                    AddSpecialLaunchers(options, install);
                }
                Add(games, new Game(name.Trim(), "Epic Games", install, options, FindFirstIcon(install)));
            }
            catch { }
        }
    }

    static void ScanCommonGameFolders(Dictionary<string, Game> games)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf)) { roots.Add(pf); roots.Add(Path.Combine(pf, "Steam")); }
        if (!string.IsNullOrWhiteSpace(pfx86)) { roots.Add(pfx86); roots.Add(Path.Combine(pfx86, "Steam")); }
        foreach (var special in new[] { "Games", "Jeux", "My Games" })
        {
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), special));
            roots.Add(Path.Combine("C:\\", special));
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Epic Games"));
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Take(5000))
                {
                    if (IsIgnoredPath(dir)) continue;
                    var exes = SafeEnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(IsCandidateExe).Take(8).ToList();
                    if (exes.Count == 0) continue;
                    var best = exes.OrderByDescending(ExecutableScore).Take(5).ToList();
                    var name = CleanGameName(Path.GetFileName(dir));
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var options = new List<LaunchOption>();
                    foreach (var exe in best) AddExeOption(options, exe, dir);
                    AddSpecialLaunchers(options, dir);
                    if (options.Count > 0) Add(games, new Game(name, "EXE détecté", dir, options, best.FirstOrDefault()));
                }
            }
            catch { }
        }
    }

    static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, pattern, option); } catch { return Array.Empty<string>(); }
    }

    static bool IsCandidateExe(string path)
    {
        var file = Path.GetFileName(path);
        if (IgnoredExeNames.Any(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase))) return false;
        if (IgnoredPathParts.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        try { return new FileInfo(path).Length >= 350_000; } catch { return false; }
    }

    static int ExecutableScore(string path)
    {
        var score = 0;
        var file = Path.GetFileNameWithoutExtension(path);
        if (file.Contains("game", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (file.Contains("win64", StringComparison.OrdinalIgnoreCase)) score += 10;
        if (file.Contains("shipping", StringComparison.OrdinalIgnoreCase)) score += 10;
        try { score += (int)Math.Min(50, new FileInfo(path).Length / 50_000_000); } catch { }
        return score;
    }

    static IEnumerable<string> FindBestExecutables(string? install, int max)
    {
        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) yield break;
        var files = SafeEnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(IsCandidateExe)
            .OrderByDescending(ExecutableScore)
            .ThenByDescending(FileLengthSafe)
            .Take(max);
        foreach (var file in files) yield return file;
    }

    static void AddExeOption(List<LaunchOption> options, string exe, string? workingDirectory)
    {
        if (!File.Exists(exe) || options.Any(o => string.Equals(o.Target, exe, StringComparison.OrdinalIgnoreCase))) return;
        var label = FriendlyExeName(Path.GetFileNameWithoutExtension(exe));
        if (label.Equals("Stardew Valley", StringComparison.OrdinalIgnoreCase)) label = "Normal";
        options.Add(new LaunchOption(label, "exe", exe, workingDirectory));
    }

    static void AddSpecialLaunchers(List<LaunchOption> options, string? install)
    {
        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) return;
        var smapi = SafeFindFile(install, "StardewModdingAPI.exe");
        if (smapi != null) options.Add(new LaunchOption("SMAPI / Mods", "exe", smapi, install));
    }

    static string? SafeFindFile(string root, string fileName)
    {
        try { return Directory.EnumerateFiles(root, fileName, SearchOption.TopDirectoryOnly).FirstOrDefault(); } catch { return null; }
    }

    static string? FindFirstIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return SafeEnumerateFiles(path, "*.exe", SearchOption.TopDirectoryOnly).Where(File.Exists).OrderByDescending(ExecutableScore).FirstOrDefault();
    }

    static string? NormalizeExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        return Directory.Exists(p) ? p : null;
    }

    static string? NormalizeExePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var p = value.Split(',')[0].Trim().Trim('"');
        return File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null;
    }

    static long FileLengthSafe(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    static bool IsIgnoredPath(string path) => IgnoredPathParts.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase));

    static string CleanGameName(string value)
    {
        var name = value.Replace("_", " ").Trim();
        return name.Length > 1 ? name : string.Empty;
    }

    static string FriendlyExeName(string value) => value.Replace('_', ' ').Replace('-', ' ').Trim();

    static string? GetString(JsonElement root, string property) => root.TryGetProperty(property, out var p) ? p.GetString() : null;

    static void TryAddSteamRoot(RegistryHive hive, HashSet<string> roots)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(@"SOFTWARE\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
        catch { }
    }

    static IEnumerable<string> ReadSteamLibraries(string steamRoot)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (yielded.Add(steamRoot)) yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var line in File.ReadLines(vdf))
        {
            var marker = "\"path\"";
            var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var candidate = parts[^1].Replace("\\\\", "\\");
            if (Directory.Exists(candidate) && yielded.Add(candidate)) yield return candidate;
        }
    }

    static string? ParseAcfValue(string text, string key)
    {
        var marker = $"\"{key}\"";
        var i = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = text[(i + marker.Length)..];
        var q1 = rest.IndexOf('"');
        if (q1 < 0) return null;
        var q2 = rest.IndexOf('"', q1 + 1);
        return q2 < 0 ? null : rest[(q1 + 1)..q2];
    }
}

internal sealed class MainForm : Form
{
    readonly FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), WrapContents = true };
    readonly TextBox search = new() { Width = 310, Height = 34, PlaceholderText = "Rechercher un jeu...", BorderStyle = BorderStyle.FixedSingle };
    readonly Button scan = new() { Text = "⟳  SCAN COMPLET", Width = 150, Height = 38, FlatStyle = FlatStyle.Flat };
    readonly Button play = new() { Text = "▶  JOUER", Width = 190, Height = 48, FlatStyle = FlatStyle.Flat };
    readonly ComboBox mode = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label status = new() { AutoSize = true, Text = "Prêt" };
    readonly Label selectedTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold) };
    readonly Label selectedInfo = new() { AutoSize = true, MaximumSize = new Size(760, 55) };
    readonly Panel selectedIcon = new() { Width = 54, Height = 54 };
    readonly Panel detail = new() { Dock = DockStyle.Bottom, Height = 118, Padding = new Padding(18) };
    List<Game> games = new();
    Game? selected;
    bool scanning;

    static readonly Color Bg = Color.FromArgb(14, 12, 20);
    static readonly Color PanelBg = Color.FromArgb(24, 21, 34);
    static readonly Color Panel2 = Color.FromArgb(31, 27, 45);
    static readonly Color Accent = Color.FromArgb(163, 92, 255);
    static readonly Color Text = Color.FromArgb(245, 242, 250);
    static readonly Color Muted = Color.FromArgb(166, 158, 181);

    public MainForm()
    {
        Text = "Mataiasu Launcher";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 680);
        ClientSize = new Size(1280, 820);
        BackColor = Bg;
        ForeColor = Text;
        Font = new Font("Segoe UI", 10f);

        var header = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(26, 18, 26, 14), BackColor = PanelBg };
        var logo = new Label { Text = "MATAIASU", AutoSize = true, Font = new Font("Segoe UI Semibold", 25, FontStyle.Bold), ForeColor = Accent, Location = new Point(26, 12) };
        var sub = new Label { Text = "GAME LIBRARY", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Muted, Location = new Point(29, 51) };
        search.Location = new Point(420, 31);
        search.BackColor = Panel2; search.ForeColor = Text; search.ForeColor = Text;
        scan.Location = new Point(765, 29); scan.BackColor = Accent; scan.ForeColor = Color.White; scan.FlatAppearance.BorderSize = 0;
        search.TextChanged += (_, _) => RenderCards();
        scan.Click += async (_, _) => await ScanAsync(true);
        header.Controls.AddRange(new Control[] { logo, sub, search, scan });

        detail.BackColor = PanelBg;
        selectedIcon.BackColor = Panel2;
        selectedIcon.Location = new Point(18, 24);
        selectedTitle.Location = new Point(92, 17);
        selectedInfo.Location = new Point(92, 47); selectedInfo.ForeColor = Muted;
        mode.Location = new Point(760, 36); mode.BackColor = Panel2; mode.ForeColor = Text;
        play.Location = new Point(970, 28); play.BackColor = Accent; play.ForeColor = Color.White; play.FlatAppearance.BorderSize = 0;
        play.Click += (_, _) => LaunchSelected();
        mode.SelectedIndexChanged += (_, _) => UpdateSelectedInfo();
        detail.Controls.AddRange(new Control[] { selectedIcon, selectedTitle, selectedInfo, mode, play });

        status.Dock = DockStyle.Top; status.Height = 24; status.Padding = new Padding(24, 4, 0, 0); status.ForeColor = Muted; status.BackColor = Bg;
        Controls.Add(cards); Controls.Add(detail); Controls.Add(status); Controls.Add(header);
        Shown += async (_, _) => await ScanAsync(true);
    }

    async Task ScanAsync(bool deep)
    {
        if (scanning) return;
        scanning = true; scan.Enabled = false; scan.Text = "SCAN...";
        status.Text = deep ? "Analyse des bibliothèques et des exécutables..." : "Analyse rapide...";
        await Task.Yield();
        try
        {
            games = await Task.Run(() => GameScanner.Scan(deep));
            RenderCards();
            status.Text = $"{games.Count} jeux / applications détectés • {games.Sum(g => g.Options.Count)} modes de lancement disponibles";
        }
        catch (Exception ex)
        {
            status.Text = "Erreur de scan : " + ex.Message;
        }
        finally { scanning = false; scan.Enabled = true; scan.Text = "⟳  SCAN COMPLET"; }
    }

    void RenderCards()
    {
        var term = search.Text.Trim();
        cards.SuspendLayout(); cards.Controls.Clear();
        var filtered = games.Where(g => string.IsNullOrWhiteSpace(term) || g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var game in filtered) cards.Controls.Add(CreateGameCard(game));
        cards.ResumeLayout();
        if (selected != null && !games.Contains(selected)) SelectGame(filtered.FirstOrDefault());
    }

    Control CreateGameCard(Game game)
    {
        var card = new Panel { Width = 285, Height = 165, Margin = new Padding(10), BackColor = PanelBg, Cursor = Cursors.Hand, Tag = game };
        var accent = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = game.Source.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? Accent : Color.FromArgb(90, 83, 110) };
        var icon = new Panel { Location = new Point(18, 18), Size = new Size(54, 54), BackColor = Panel2 };
        DrawIcon(icon, game.IconPath);
        var title = new Label { Text = game.Name, Location = new Point(86, 18), Size = new Size(178, 48), Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold), ForeColor = Text, AutoEllipsis = true };
        var src = new Label { Text = game.Source.ToUpperInvariant(), Location = new Point(86, 64), Size = new Size(178, 22), Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Muted };
        var modes = new Label { Text = $"{game.Options.Count} mode{(game.Options.Count > 1 ? "s" : "")} de lancement", Location = new Point(18, 94), Size = new Size(245, 24), ForeColor = Muted };
        var launch = new Button { Text = game.Options.Count > 1 ? "CHOISIR ▶" : "LANCER ▶", Location = new Point(18, 124), Size = new Size(245, 30), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Text };
        launch.FlatAppearance.BorderColor = Color.FromArgb(70, 62, 90);
        card.Controls.AddRange(new Control[] { accent, icon, title, src, modes, launch });
        void select(object? s, EventArgs e) => SelectGame(game);
        card.Click += select; icon.Click += select; title.Click += select; src.Click += select; modes.Click += select; launch.Click += (_, _) => { SelectGame(game); LaunchSelected(); };
        return card;
    }

    void SelectGame(Game? game)
    {
        selected = game;
        if (game == null)
        {
            selectedTitle.Text = "Sélectionne un jeu"; selectedInfo.Text = ""; mode.Items.Clear(); return;
        }
        selectedTitle.Text = game.Name;
        mode.BeginUpdate(); mode.Items.Clear(); foreach (var option in game.Options) mode.Items.Add(option.Name); mode.EndUpdate();
        mode.SelectedIndex = 0;
        DrawIcon(selectedIcon, game.IconPath);
        UpdateSelectedInfo();
    }

    void UpdateSelectedInfo()
    {
        if (selected == null || mode.SelectedIndex < 0) { selectedInfo.Text = ""; return; }
        var option = selected.Options[mode.SelectedIndex];
        selectedInfo.Text = $"{selected.Source}  •  {option.Name}\n{selected.InstallPath ?? option.Target ?? "Emplacement inconnu"}";
    }

    void LaunchSelected()
    {
        if (selected == null || mode.SelectedIndex < 0) return;
        var option = selected.Options[mode.SelectedIndex];
        try
        {
            if (option.Kind == "uri")
            {
                Process.Start(new ProcessStartInfo { FileName = option.Target, UseShellExecute = true });
                return;
            }
            if (!string.IsNullOrWhiteSpace(option.Target) && File.Exists(option.Target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = option.Target,
                    WorkingDirectory = string.IsNullOrWhiteSpace(option.WorkingDirectory) ? Path.GetDirectoryName(option.Target) : option.WorkingDirectory,
                    UseShellExecute = true
                });
                status.Text = $"Lancé : {selected.Name} • {option.Name}";
                return;
            }
            MessageBox.Show("Le fichier de lancement n'existe plus. Lance un nouveau scan.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Impossible de lancer le jeu", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void DrawIcon(Panel panel, string? exePath)
    {
        panel.BackgroundImage?.Dispose(); panel.BackgroundImage = null;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) { panel.Invalidate(); return; }
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null) panel.BackgroundImage = icon.ToBitmap();
            panel.BackgroundImageLayout = ImageLayout.Stretch;
        }
        catch { }
    }
}

internal static class Program
{
    [STAThread]
    static async Task Main()
    {
        ApplicationConfiguration.Initialize();
        var updated = await LauncherUpdater.CheckAndApplyAsync();
        if (updated) return;
        Application.Run(new MainForm());
    }
}
