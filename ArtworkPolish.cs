using System.Collections;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class ArtworkPolish
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Sync = new();
    static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Launch'aiasu",
        "Artwork");
    static bool hooked;

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += OnIdle;

    static void OnIdle(object? sender, EventArgs e)
    {
        var form = Application.OpenForms.OfType<Form>().FirstOrDefault(
            f => f.GetType().Name.Equals("MainForm", StringComparison.Ordinal));
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
            var iconPath = GetProperty<string>(card.Tag, "IconPath");
            var optionsValue = GetRawProperty(card.Tag, "Options");
            if (string.IsNullOrWhiteSpace(gameName) || optionsValue is not IEnumerable options) continue;

            var appId = ExtractSteamAppId(options);
            var key = string.IsNullOrWhiteSpace(appId)
                ? gameName + "|local|" + (iconPath ?? string.Empty)
                : gameName + "|steam|" + appId;

            lock (Sync)
            {
                if (!Loaded.Add(key)) continue;
            }

            if (!string.IsNullOrWhiteSpace(appId))
                _ = LoadSteamArtworkAsync(card, appId);
            else
                _ = LoadLocalArtworkAsync(card, iconPath, gameName);
        }
    }

    static async Task LoadSteamArtworkAsync(Control card, string appId)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            var cachePath = Path.Combine(CacheRoot, $"steam_{appId}_512.png");

            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length < 20_000)
            {
                var sourcePath = Path.Combine(CacheRoot, $"steam_{appId}_source.jpg");
                if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length < 10_000)
                {
                    var urls = new[]
                    {
                        $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
                        $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                        $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                    };

                    foreach (var url in urls)
                    {
                        try
                        {
                            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (!response.IsSuccessStatusCode) continue;
                            await using var input = await response.Content.ReadAsStreamAsync();
                            await using var output = File.Create(sourcePath);
                            await input.CopyToAsync(output);
                            if (new FileInfo(sourcePath).Length > 10_000) break;
                        }
                        catch { }
                    }
                }

                if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 10_000)
                {
                    using var source = Image.FromFile(sourcePath);
                    using var polished = CreateCover(source, 512, 512);
                    polished.Save(cachePath, ImageFormat.Png);
                }
            }

            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length <= 20_000) return;
            using var image = Image.FromFile(cachePath);
            ApplyToCard(card, new Bitmap(image));
        }
        catch { }
    }

    static async Task LoadLocalArtworkAsync(Control card, string? iconPath, string gameName)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);

            // When a local game is also sold on Steam, use a real cover instead of the EXE icon.
            var discovered = await TryLoadStoreArtworkAsync(gameName);
            if (discovered != null)
            {
                ApplyToCard(card, discovered);
                return;
            }

            await Task.Yield();
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return;

            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(iconPath))).Substring(0, 20);
            var cachePath = Path.Combine(CacheRoot, $"local_{key}_256.png");

            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length < 2_000)
            {
                using var icon = Icon.ExtractAssociatedIcon(iconPath);
                if (icon == null) return;
                using var source = icon.ToBitmap();
                using var polished = CreateIconArtwork(source, 256);
                polished.Save(cachePath, ImageFormat.Png);
            }

            if (!File.Exists(cachePath)) return;
            using var image = Image.FromFile(cachePath);
            ApplyToCard(card, new Bitmap(image));
        }
        catch { }
    }

    static async Task<Bitmap?> TryLoadStoreArtworkAsync(string gameName)
    {
        try
        {
            var normalized = CleanSearchName(gameName);
            if (normalized.Length < 3) return null;

            var safeKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized))).Substring(0, 20);
            var cachePath = Path.Combine(CacheRoot, $"store_{safeKey}_512.png");

            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 20_000)
            {
                using var cached = Image.FromFile(cachePath);
                return new Bitmap(cached);
            }

            var url = "https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(normalized) + "&l=english&cc=fr";
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? best = null;
            var bestScore = int.MinValue;
            foreach (var item in items.EnumerateArray().Take(10))
            {
                var itemName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(itemName)) continue;
                var score = SimilarityScore(normalized, itemName);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }

            if (best is null || bestScore < 60) return null;

            var selected = best.Value;
            var appId = selected.TryGetProperty("id", out var id) ? id.ToString() : null;
            if (string.IsNullOrWhiteSpace(appId)) return null;

            var sourcePath = Path.Combine(CacheRoot, $"store_{safeKey}_source.jpg");
            var sourceUrls = new[]
            {
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
            };

            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length < 10_000)
            {
                foreach (var sourceUrl in sourceUrls)
                {
                    try
                    {
                        using var imgResponse = await Http.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead);
                        if (!imgResponse.IsSuccessStatusCode) continue;
                        await using var input = await imgResponse.Content.ReadAsStreamAsync();
                        await using var output = File.Create(sourcePath);
                        await input.CopyToAsync(output);
                        if (new FileInfo(sourcePath).Length > 10_000) break;
                    }
                    catch { }
                }
            }

            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 10_000) return null;

            using var source = Image.FromFile(sourcePath);
            using var polished = CreateCover(source, 512, 512);
            polished.Save(cachePath, ImageFormat.Png);
            return new Bitmap(polished);
        }
        catch { return null; }
    }

    static int SimilarityScore(string expected, string actual)
    {
        var a = CleanSearchName(expected);
        var b = CleanSearchName(actual);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return 100;
        if (b.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase)) return 90;

        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bTokens = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        if (aTokens.Length == 0) return 0;
        var common = aTokens.Count(t => bTokens.Contains(t));
        return 50 + (int)Math.Round(40.0 * common / aTokens.Length);
    }

    static string CleanSearchName(string value)
    {
        var cleaned = Regex.Replace(value ?? string.Empty, @"\([^)]*\)|\[[^\]]*\]", " ");
        cleaned = cleaned.Replace("Launcher", " ", StringComparison.OrdinalIgnoreCase)
                         .Replace("Client", " ", StringComparison.OrdinalIgnoreCase)
                         .Replace("Game", " ", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    static Bitmap CreateCover(Image source, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(18, 15, 26));
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var scale = Math.Max((double)width / source.Width, (double)height / source.Height);
        var drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        var x = (width - drawWidth) / 2;
        var y = (height - drawHeight) / 2;
        g.DrawImage(source, new Rectangle(x, y, drawWidth, drawHeight));
        return bitmap;
    }

    static Bitmap CreateIconArtwork(Image source, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(24, 21, 34));
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var padding = Math.Max(12, size / 10);
        var target = new Rectangle(padding, padding, size - padding * 2, size - padding * 2);
        g.DrawImage(source, target);
        return bitmap;
    }

    static void ApplyToCard(Control card, Bitmap bitmap)
    {
        if (card.IsDisposed)
        {
            bitmap.Dispose();
            return;
        }

        void Apply()
        {
            try
            {
                var imagePanel = card.Controls.OfType<Panel>()
                    .OrderBy(p => p.Location.X)
                    .FirstOrDefault(p => p.Width <= 110 && p.Height <= 110 && p != card);
                if (imagePanel == null)
                {
                    bitmap.Dispose();
                    return;
                }

                var old = imagePanel.BackgroundImage;
                imagePanel.BackgroundImage = bitmap;
                imagePanel.BackgroundImageLayout = ImageLayout.Zoom;
                old?.Dispose();
            }
            catch
            {
                bitmap.Dispose();
            }
        }

        try
        {
            if (card.InvokeRequired) card.BeginInvoke(new Action(Apply));
            else Apply();
        }
        catch
        {
            bitmap.Dispose();
        }
    }

    static string? ExtractSteamAppId(IEnumerable options)
    {
        foreach (var option in options)
        {
            var kind = GetProperty<string>(option!, "Kind");
            var target = GetProperty<string>(option!, "Target");
            if (!string.Equals(kind, "uri", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target))
                continue;

            var match = Regex.Match(target, @"steam://(?:rungameid|runapp)/(?<id>\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["id"].Value;
        }

        return null;
    }

    static object? GetRawProperty(object source, string name)
    {
        try
        {
            return source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
        }
        catch { return null; }
    }

    static T? GetProperty<T>(object source, string name)
    {
        try
        {
            var value = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
            if (value is T typed) return typed;
            return value is null ? default : (T?)Convert.ChangeType(value, typeof(T));
        }
        catch { return default; }
    }
}
