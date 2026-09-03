using System.Text.Json;

internal sealed class LauncherLibrary
{
    public string Name { get; set; } = "";
    public List<string> Games { get; set; } = new();
}

internal static class LibraryStore
{
    static readonly object Sync = new();
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MataiasuLauncher");
    static readonly string FilePath = Path.Combine(Root, "libraries.json");
    static List<LauncherLibrary>? cache;

    public static List<LauncherLibrary> Load()
    {
        lock (Sync)
        {
            if (cache != null) return Clone(cache);
            try
            {
                Directory.CreateDirectory(Root);
                if (File.Exists(FilePath))
                {
                    cache = JsonSerializer.Deserialize<List<LauncherLibrary>>(File.ReadAllText(FilePath)) ?? new List<LauncherLibrary>();
                }
            }
            catch { cache = new List<LauncherLibrary>(); }

            cache ??= new List<LauncherLibrary>();
            cache = cache
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new LauncherLibrary
                {
                    Name = x.Name.Trim(),
                    Games = x.Games.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                })
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            return Clone(cache);
        }
    }

    public static bool Exists(string name) => Load().Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool Create(string name)
    {
        name = Normalize(name);
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (Sync)
        {
            var libs = LoadUnlocked();
            if (libs.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
            libs.Add(new LauncherLibrary { Name = name });
            SaveUnlocked(libs);
            return true;
        }
    }

    public static bool Delete(string name)
    {
        lock (Sync)
        {
            var libs = LoadUnlocked();
            var removed = libs.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveUnlocked(libs);
            return true;
        }
    }

    public static bool Contains(string library, string game)
    {
        var lib = Load().FirstOrDefault(x => x.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
        return lib?.Games.Any(g => g.Equals(game, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static bool AddGame(string library, string game)
    {
        library = Normalize(library);
        game = Normalize(game);
        if (string.IsNullOrWhiteSpace(library) || string.IsNullOrWhiteSpace(game)) return false;
        lock (Sync)
        {
            var libs = LoadUnlocked();
            var lib = libs.FirstOrDefault(x => x.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
            if (lib == null) return false;
            if (lib.Games.Any(g => g.Equals(game, StringComparison.OrdinalIgnoreCase))) return false;
            lib.Games.Add(game);
            SaveUnlocked(libs);
            return true;
        }
    }

    public static bool RemoveGame(string library, string game)
    {
        lock (Sync)
        {
            var libs = LoadUnlocked();
            var lib = libs.FirstOrDefault(x => x.Name.Equals(library, StringComparison.OrdinalIgnoreCase));
            if (lib == null) return false;
            var removed = lib.Games.RemoveAll(g => g.Equals(game, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveUnlocked(libs);
            return true;
        }
    }

    public static bool IsFavorite(string game) => Contains("★ Favoris", game);
    public static bool AddFavorite(string game) => AddGame("★ Favoris", game);
    public static bool RemoveFavorite(string game) => RemoveGame("★ Favoris", game);

    static List<LauncherLibrary> LoadUnlocked()
    {
        if (cache != null) return cache;
        try
        {
            Directory.CreateDirectory(Root);
            cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<LauncherLibrary>>(File.ReadAllText(FilePath)) ?? new List<LauncherLibrary>()
                : new List<LauncherLibrary>();
        }
        catch { cache = new List<LauncherLibrary>(); }
        return cache;
    }

    static void SaveUnlocked(List<LauncherLibrary> libraries)
    {
        cache = libraries;
        Directory.CreateDirectory(Root);
        var json = JsonSerializer.Serialize(libraries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    static string Normalize(string value) => value.Trim();

    static List<LauncherLibrary> Clone(List<LauncherLibrary> source) => source.Select(x => new LauncherLibrary { Name = x.Name, Games = x.Games.ToList() }).ToList();
}
