using System.Collections;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class ArtworkPolish
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Sync = new();
    static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Launch'aiasu", "Artwork");
    static bool hooked;

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += OnIdle;

    static void OnIdle(object? sender, EventArgs e)
    {
        var form = Application.OpenForms.OfType<Form>().FirstOrDefault(f => f.GetType().Name.Equals("MainForm", StringComparison.Ordinal));
        if (form == null) return;
        if (!hooked)
        {
            hooked = true;
            form.FormClosed += (_, _) => Application.Idle -= OnIdle;
        }

        var cardsField = form.GetType().GetField("cards", BindingFlags.Instance | BindingFlags.NonPublic);
        if (cardsField?.GetValue(form) is not FlowLayoutPanel cards || cards.Controls.Count == 0) return;

        foreach (Control card in cards.Controls)
        {
            if (card.Tag == null) continue;
            var gameName = GetProperty<string>(card.Tag, "Name");
            var optionsObject = GetPropertyObject(card.Tag, "Options");
            if (string.IsNullOrWhiteSpace(gameName) || optionsObject is not IEnumerable options) continue;

            var appId = ExtractSteamAppId(options);
            if (string.IsNullOrWhiteSpace(appId)) continue;

            var key = gameName + "|" + appId;
            lock (Sync) if (!Loaded.Add(key)) continue;
            _ = LoadSteamArtworkAsync(card, appId);
        }
    }

    static async Task LoadSteamArtworkAsync(Control card, string appId)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            var path = Path.Combine(CacheRoot, appId + ".jpg");
            if (!File.Exists(path))
            {
                var urls = new[]
                {
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                };
                foreach (var url in urls)
                {
                    try
                    {
                        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        if (!response.IsSuccessStatusCode) continue;
                        await using var input = await response.Content.ReadAsStreamAsync();
                        await using var output = File.Create(path);
                        await input.CopyToAsync(output);
                        if (new FileInfo(path).Length > 10_000) break;
                    }
                    catch { }
                }
            }
            if (!File.Exists(path) || new FileInfo(path).Length <= 10_000) return;

            using var original = Image.FromFile(path);
            var thumb = CreateSquareThumbnail(original, 192);
            if (card.IsDisposed) { thumb.Dispose(); return; }
            card.BeginInvoke(new Action(() => ApplyToCard(card, thumb)));
        }
        catch { }
    }

    static Bitmap CreateSquareThumbnail(Image source, int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var side = Math.Min(source.Width, source.Height);
        var cropX = (source.Width - side) / 2;
        var cropY = (source.Height - side) / 2;
        g.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle(cropX, cropY, side, side), GraphicsUnit.Pixel);
        return bitmap;
    }

    static void ApplyToCard(Control card, Bitmap bitmap)
    {
        try
        {
            var imagePanel = card.Controls.OfType<Panel>()
                .OrderBy(p => p.Location.X)
                .FirstOrDefault(p => p.Width <= 80 && p.Height <= 80 && p != card);
            if (imagePanel == null) { bitmap.Dispose(); return; }
            var old = imagePanel.BackgroundImage;
            imagePanel.BackgroundImage = bitmap;
            imagePanel.BackgroundImageLayout = ImageLayout.Stretch;
            old?.Dispose();
        }
        catch { bitmap.Dispose(); }
    }

    static string? ExtractSteamAppId(IEnumerable options)
    {
        foreach (var option in options)
        {
            var kind = GetProperty<string>(option!, "Kind");
            var target = GetProperty<string>(option!, "Target");
            if (!string.Equals(kind, "uri", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target)) continue;
            var match = Regex.Match(target, @"steam://(?:rungameid|runapp)/(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["id"].Value;
        }
        return null;
    }

    static object? GetPropertyObject(object source, string name)
    {
        try { return source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source); }
        catch { return null; }
    }

    static T? GetProperty<T>(object source, string name)
    {
        try
        {
            var value = GetPropertyObject(source, name);
            if (value is T typed) return typed;
            return value is null ? default : (T?)Convert.ChangeType(value, typeof(T));
        }
        catch { return default; }
    }
}
