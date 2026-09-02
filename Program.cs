using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

internal sealed record Game(string Name, string Source, string? Executable, string? LaunchUri, string? InstallPath);

internal static class LauncherUpdater
{
    const string Repo = "Mataiasu/MataiasuLauncher";
    const string AssetName = "MataiasuLauncher.exe";
    const string CommitApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/commits/main";
    const string ReleaseApi = "https://api.github.com/repos/Mataiasu/MataiasuLauncher/releases/tags/latest";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MataiasuLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<bool> CheckAndApplyAsync()
    {
        if (BuildInfo.Commit.Equals("dev", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var latestCommit = await GetLatestCommitAsync();
            if (string.IsNullOrWhiteSpace(latestCommit) ||
                latestCommit.Equals(BuildInfo.Commit, StringComparison.OrdinalIgnoreCase))
                return false;

            var release = await GetLatestReleaseAsync();
            if (release is null || !release.Value.TryGetProperty("assets", out var assets))
                return false;

            string? assetUrl = null;
            string? releaseCommit = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    assetUrl = url;
                else if (string.Equals(name, "MataiasuLauncher.commit.txt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    releaseCommit = (await Http.GetStringAsync(url)).Trim();
            }

            if (string.IsNullOrWhiteSpace(assetUrl) ||
                !latestCommit.Equals(releaseCommit, StringComparison.OrdinalIgnoreCase))
                return false;

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return false;

            var tempRoot = Path.Combine(Path.GetTempPath(), "MataiasuLauncher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var downloadedExe = Path.Combine(tempRoot, AssetName);
            using (var response = await Http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(downloadedExe);
                await input.CopyToAsync(output);
            }

            var script = Path.Combine(tempRoot, "update.cmd");
            var scriptContent = $"@echo off\r\n" +
                ":retry\r\n" +
                $"copy /Y \"{downloadedExe}\" \"{currentExe}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto retry)\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                $"del \"{downloadedExe}\" >nul 2>&1\r\n" +
                $"del \"%~f0\" >nul 2>&1\r\n";
            await File.WriteAllTextAsync(script, scriptContent);

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
        catch
        {
            return false;
        }
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
        if (!response.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}

internal static class GameScanner
{
    public static List<Game> Scan()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        ScanUninstallKeys(games, RegistryHive.LocalMachine);
        ScanUninstallKeys(games, RegistryHive.CurrentUser);
        ScanSteam(games);
        ScanEpic(games);
        return games.Values.OrderBy(g => g.Name).ToList();
    }

    static void Add(Dictionary<string, Game> games, Game game)
    {
        if (string.IsNullOrWhiteSpace(game.Name)) return;
        if (game.Name.Contains("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
            game.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) return;
        games.TryAdd(game.Name, game);
    }

    static void ScanUninstallKeys(Dictionary<string, Game> games, RegistryHive hive)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            ScanUninstallPath(baseKey, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", games);
            using var wow = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
            ScanUninstallPath(wow, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", games);
        }
        catch { }
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
                var exe = ResolveExecutable(install, icon);
                if (exe != null || !string.IsNullOrWhiteSpace(install))
                    Add(games, new Game(name.Trim(), "Installed", exe, null, install));
            }
            catch { }
        }
    }

    static string? ResolveExecutable(string? installPath, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var p = displayIcon.Split(',')[0].Trim('\"', ' ');
            if (File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p;
        }
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return null;
        try
        {
            return Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(x => !Path.GetFileName(x).Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                     !Path.GetFileName(x).Contains("setup", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    static void ScanSteam(Dictionary<string, Game> games)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddSteamRoot(RegistryHive.CurrentUser, roots);
        TryAddSteamRoot(RegistryHive.LocalMachine, roots);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        foreach (var root in roots.Where(Directory.Exists))
        {
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
                        var path = string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(lib, "steamapps", "common", installDir);
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                            Add(games, new Game(name, "Steam", null, $"steam://rungameid/{appId}", path));
                    }
                    catch { }
                }
            }
        }
    }

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
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var line in File.ReadLines(vdf))
        {
            var idx = line.IndexOf("\"path\"");
            if (idx < 0) continue;
            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var candidate = parts[^1].Replace("\\\\", "\\");
                if (Directory.Exists(candidate)) yield return candidate;
            }
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
        if (q2 < 0) return null;
        return rest[(q1 + 1)..q2];
    }

    static void ScanEpic(Dictionary<string, Game> games)
    {
        var manifestsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestsRoot))
            manifestsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Data", "Manifests");
        if (!Directory.Exists(manifestsRoot)) return;

        foreach (var file in Directory.EnumerateFiles(manifestsRoot, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var install = root.TryGetProperty("InstallLocation", out var p) ? p.GetString() : null;
                var exeName = root.TryGetProperty("LaunchExecutable", out var e) ? e.GetString() : null;
                var exe = !string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(exeName) ? Path.Combine(install, exeName) : null;
                var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                    Add(games, new Game(name, "Epic Games", exe, string.IsNullOrWhiteSpace(appName) ? null : $"com.epicgames.launcher://apps/{appName}?action=launch", install));
            }
            catch { }
        }
    }
}

internal sealed class MainForm : Form
{
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button play = new() { Text = "▶ JOUER", Width = 150, Height = 42 };
    readonly Button scan = new() { Text = "↻ ANALYSER", Width = 150, Height = 42 };
    readonly TextBox search = new() { Width = 280, PlaceholderText = "Rechercher un jeu..." };
    List<Game> allGames = new();

    public MainForm()
    {
        Text = "Mataiasu Launcher";
        Width = 1100;
        Height = 700;
        MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 26);
        ForeColor = Color.White;

        var top = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(18) };
        var title = new Label { Text = "MATAIASU LAUNCHER", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Location = new Point(18, 12) };
        var sub = new Label { Text = "Jeux installés sur ce PC", ForeColor = Color.LightGray, AutoSize = true, Location = new Point(20, 48) };
        search.Location = new Point(650, 25);
        scan.Location = new Point(950, 22);
        scan.Click += (_, _) => DoScan();
        search.TextChanged += (_, _) => ApplyFilter();
        top.Controls.Add(title); top.Controls.Add(sub); top.Controls.Add(search); top.Controls.Add(scan);

        list.Columns.Add("Jeu", 430);
        list.Columns.Add("Source", 150);
        list.Columns.Add("Emplacement", 420);
        list.DoubleClick += (_, _) => LaunchSelected();

        play.Dock = DockStyle.Bottom;
        play.Click += (_, _) => LaunchSelected();

        Controls.Add(list); Controls.Add(play); Controls.Add(status); Controls.Add(top);
        Shown += (_, _) => DoScan();
    }

    void DoScan()
    {
        scan.Enabled = false;
        status.Text = "Analyse des jeux installés...";
        Application.DoEvents();
        allGames = GameScanner.Scan();
        ApplyFilter();
        status.Text = $"{allGames.Count} jeu(x) détecté(s). Double-clique ou utilise JOUER.";
        scan.Enabled = true;
    }

    void ApplyFilter()
    {
        var term = search.Text.Trim();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var game in allGames.Where(g => string.IsNullOrWhiteSpace(term) || g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ListViewItem(game.Name);
            item.SubItems.Add(game.Source);
            item.SubItems.Add(game.InstallPath ?? "");
            item.Tag = game;
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    void LaunchSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var game = (Game)list.SelectedItems[0].Tag;
        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchUri))
            {
                Process.Start(new ProcessStartInfo { FileName = game.LaunchUri, UseShellExecute = true });
                return;
            }
            if (!string.IsNullOrWhiteSpace(game.Executable) && File.Exists(game.Executable))
            {
                Process.Start(new ProcessStartInfo { FileName = game.Executable, WorkingDirectory = Path.GetDirectoryName(game.Executable), UseShellExecute = true });
                return;
            }
            if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                Process.Start(new ProcessStartInfo { FileName = game.InstallPath, UseShellExecute = true });
                MessageBox.Show("Le launcher a trouvé le dossier du jeu, mais pas encore son exécutable.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MessageBox.Show("Aucune méthode de lancement valide n'a été trouvée pour ce jeu.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible de lancer le jeu.\n\n{ex.Message}", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class Program
{
    [STAThread]
    static async Task Main()
    {
        ApplicationConfiguration.Initialize();

        var updated = await LauncherUpdater.CheckAndApplyAsync();
        if (updated)
            return;

        Application.Run(new MainForm());
    }
}
