using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal enum ManualUpdateResult
{
    NotAvailable,
    AlreadyUpToDate,
    UpdatedAndRestarting,
    Failed
}

internal static class AutoUpdate
{
    const string CommitUrl = "https://github.com/Mataiasu/MataiasuLauncher/releases/download/latest/MataiasuLauncher.commit.txt";
    const string ExeUrl = "https://github.com/Mataiasu/MataiasuLauncher/releases/download/latest/MataiasuLauncher.exe";
    static readonly HttpClient Http = CreateClient();
    static int autoCheckStarted;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MataiasuLauncher", "1.0"));
        return client;
    }

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += OnIdle;

    static async void OnIdle(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref autoCheckStarted, 1) != 0) return;
        Application.Idle -= OnIdle;

        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;
        await CheckAndApplyAsync(form, interactive: false);
    }

    public static async Task<ManualUpdateResult> CheckAndApplyAsync(Form? form, bool interactive)
    {
        try
        {
            if (form != null) SetStatus(form, "Recherche d'une mise à jour...");

            var releaseCommit = await GetLatestReleaseCommitAsync();
            if (string.IsNullOrWhiteSpace(releaseCommit))
            {
                if (interactive) ShowMessage(form, "Impossible de contacter le serveur de mise à jour.", MessageBoxIcon.Warning);
                return ManualUpdateResult.NotAvailable;
            }

            var currentCommit = BuildInfo.Commit.Trim();
            var isDev = currentCommit.Equals("dev", StringComparison.OrdinalIgnoreCase);
            if (!isDev && releaseCommit.Equals(currentCommit, StringComparison.OrdinalIgnoreCase))
            {
                if (interactive)
                {
                    SetStatus(form, "Launcher à jour.");
                    ShowMessage(form, "Mataiasu Launcher est déjà à jour.", MessageBoxIcon.Information);
                }
                return ManualUpdateResult.AlreadyUpToDate;
            }

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                if (interactive) ShowMessage(form, "Impossible de localiser l'exécutable actuel.", MessageBoxIcon.Warning);
                return ManualUpdateResult.Failed;
            }

            var installDirectory = Path.GetDirectoryName(currentExe);
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                if (interactive) ShowMessage(form, "Impossible de localiser le dossier du launcher.", MessageBoxIcon.Warning);
                return ManualUpdateResult.Failed;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "MataiasuLauncher", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var downloadedExe = Path.Combine(tempRoot, "MataiasuLauncher.new.exe");
            var helper = Path.Combine(tempRoot, "update.ps1");
            var pid = Environment.ProcessId;

            if (form != null) SetStatus(form, "Téléchargement de la mise à jour...");
            await DownloadFileAsync(ExeUrl, downloadedExe);

            await File.WriteAllTextAsync(helper, BuildPowerShellScript());

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" -Pid {pid} -Source \"{downloadedExe}\" -Target \"{currentExe}\""
            });

            if (form != null)
            {
                SetStatus(form, "Mise à jour téléchargée. Redémarrage...");
                await Task.Delay(250);
                form.Close();
            }

            return ManualUpdateResult.UpdatedAndRestarting;
        }
        catch (Exception ex)
        {
            if (interactive)
                ShowMessage(form, "La mise à jour a échoué :\n\n" + ex.Message, MessageBoxIcon.Error);
            if (form != null) SetStatus(form, "Mise à jour indisponible.");
            return ManualUpdateResult.Failed;
        }
    }

    static async Task<string?> GetLatestReleaseCommitAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CommitUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return null;
        var value = (await response.Content.ReadAsStringAsync()).Trim();
        return value.Length >= 7 ? value : null;
    }

    static async Task DownloadFileAsync(string url, string destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output);
    }

    static string BuildPowerShellScript() => @"
param(
    [int]$Pid,
    [string]$Source,
    [string]$Target
)

for ($i = 0; $i -lt 60; $i++) {
    $process = Get-Process -Id $Pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { break }
    Start-Sleep -Milliseconds 500
}

for ($i = 0; $i -lt 20; $i++) {
    try {
        Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop
        Start-Process -FilePath $Target
        Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
        exit 0
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

try { Start-Process -FilePath $Target } catch { }
";

    static void SetStatus(Form form, string text)
    {
        foreach (var label in FindControls<Label>(form))
        {
            if (label.Text.Contains("jeux", StringComparison.OrdinalIgnoreCase) ||
                label.Text.Equals("Prêt", StringComparison.OrdinalIgnoreCase) ||
                label.Text.StartsWith("Analyse", StringComparison.OrdinalIgnoreCase) ||
                label.Text.StartsWith("Recherche", StringComparison.OrdinalIgnoreCase) ||
                label.Text.StartsWith("Téléchargement", StringComparison.OrdinalIgnoreCase))
            {
                label.Text = text;
                return;
            }
        }
    }

    static void ShowMessage(Form? form, string text, MessageBoxIcon icon)
    {
        try { MessageBox.Show(form, text, "Mataiasu Launcher", MessageBoxButtons.OK, icon); }
        catch { MessageBox.Show(text, "Mataiasu Launcher", MessageBoxButtons.OK, icon); }
    }

    static IEnumerable<T> FindControls<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}

internal static class AutoUpdateUi
{
    static bool attached;
    static Button? updateButton;

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += AttachWhenReady;

    static void AttachWhenReady(object? sender, EventArgs e)
    {
        if (attached) return;
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;
        var scan = FindControls<Button>(form).FirstOrDefault(b => b.Text.Contains("SCAN", StringComparison.OrdinalIgnoreCase));
        if (scan == null || scan.Parent == null) return;

        attached = true;
        Application.Idle -= AttachWhenReady;

        updateButton = new Button
        {
            Text = "↻  MISE À JOUR",
            Width = 150,
            Height = scan.Height,
            Left = scan.Right + 10,
            Top = scan.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(31, 27, 45),
            ForeColor = Color.FromArgb(245, 242, 250),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        updateButton.FlatAppearance.BorderColor = Color.FromArgb(90, 76, 115);
        updateButton.FlatAppearance.BorderSize = 1;
        updateButton.Click += async (_, _) => await ManualUpdateAsync(form);
        scan.Parent.Controls.Add(updateButton);
        updateButton.BringToFront();

        form.Resize += (_, _) => Position(scan, updateButton);
        Position(scan, updateButton);
    }

    static async Task ManualUpdateAsync(Form form)
    {
        if (updateButton == null) return;
        updateButton.Enabled = false;
        updateButton.Text = "… VÉRIFICATION";
        var result = await AutoUpdate.CheckAndApplyAsync(form, interactive: true);
        if (result != ManualUpdateResult.UpdatedAndRestarting)
        {
            updateButton.Text = "↻  MISE À JOUR";
            updateButton.Enabled = true;
        }
    }

    static void Position(Control scan, Control button)
    {
        button.Left = scan.Right + 10;
        button.Top = scan.Top;
    }

    static IEnumerable<T> FindControls<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}
