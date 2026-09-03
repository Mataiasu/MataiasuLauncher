using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class UiPolish
{
    static bool applied;
    static readonly Color Bg = Color.FromArgb(14, 12, 20);
    static readonly Color SidebarBg = Color.FromArgb(20, 17, 29);
    static readonly Color Panel2 = Color.FromArgb(31, 27, 45);
    static readonly Color Accent = Color.FromArgb(163, 92, 255);
    static readonly Color TextColor = Color.FromArgb(245, 242, 250);
    static readonly Color Muted = Color.FromArgb(166, 158, 181);

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += ApplyWhenReady;

    static void ApplyWhenReady(object? sender, EventArgs e)
    {
        if (applied) return;
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;

        var libraries = GetField<FlowLayoutPanel>(form, "libraryButtons");
        var status = GetField<Label>(form, "status");
        var sidebar = GetField<Panel>(form, "sidebar");
        var cards = GetField<FlowLayoutPanel>(form, "cards");
        var detail = GetField<Panel>(form, "detail");
        if (libraries == null || status == null || sidebar == null || cards == null || detail == null) return;

        applied = true;
        Application.Idle -= ApplyWhenReady;
        FixSidebar(libraries, sidebar);
        FixBottomLayout(form, status, detail, sidebar, cards);
        NormalizeLibraryButtons(libraries);
        var timer = new Timer { Interval = 150 };
        timer.Tick += (_, _) => NormalizeLibraryButtons(libraries);
        timer.Start();
    }

    static void FixSidebar(FlowLayoutPanel libraries, Panel sidebar)
    {
        sidebar.BackColor = SidebarBg;
        sidebar.Width = 242;
        sidebar.Padding = new Padding(16, 18, 12, 118);
        libraries.Dock = DockStyle.Fill;
        libraries.BackColor = SidebarBg;
        libraries.FlowDirection = FlowDirection.TopDown;
        libraries.WrapContents = false;
        libraries.AutoScroll = true;
        libraries.Padding = new Padding(0, 4, 4, 4);
        libraries.Margin = new Padding(0);

        foreach (var button in libraries.Controls.OfType<Button>())
        {
            button.Width = Math.Max(180, sidebar.ClientSize.Width - 12);
            button.Height = 36;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(10, 0, 8, 0);
            button.BackColor = SidebarBg;
            button.ForeColor = TextColor;
            button.Font = new Font("Segoe UI", 9.5f);
        }
    }

    static void FixBottomLayout(MainForm form, Label status, Panel detail, Panel sidebar, FlowLayoutPanel cards)
    {
        detail.Height = 108;
        detail.Dock = DockStyle.Bottom;
        detail.BringToFront();

        status.Parent?.Controls.Remove(status);
        form.Controls.Add(status);
        status.Dock = DockStyle.None;
        status.Height = 24;
        status.Width = Math.Max(300, form.ClientSize.Width - 270);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        status.Location = new Point(258, Math.Max(0, form.ClientSize.Height - detail.Height - status.Height));
        status.BackColor = Bg;
        status.ForeColor = Muted;
        status.Padding = new Padding(0, 0, 0, 0);
        status.Font = new Font("Segoe UI", 9f);
        status.BringToFront();

        form.Resize += (_, _) =>
        {
            status.Width = Math.Max(300, form.ClientSize.Width - 270);
            status.Location = new Point(258, Math.Max(0, form.ClientSize.Height - detail.Height - status.Height));
            NormalizeLibraryButtons(GetField<FlowLayoutPanel>(form, "libraryButtons")!);
        };

        cards.Padding = new Padding(20, 18, 20, detail.Height + 30);
        cards.Margin = new Padding(0);
    }

    static void NormalizeLibraryButtons(FlowLayoutPanel libraries)
    {
        var names = new List<string> { "Tous les jeux", "★ Favoris", "Non classés" };
        names.AddRange(LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name));
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var button in libraries.Controls.OfType<Button>())
        {
            var baseName = button.Tag as string;
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = names
                    .OrderByDescending(x => x.Length)
                    .FirstOrDefault(x => button.Text.Equals(x, StringComparison.OrdinalIgnoreCase) || button.Text.StartsWith(x + " ", StringComparison.OrdinalIgnoreCase));
                baseName ??= StripTrailingCount(button.Text);
                button.Tag = baseName;
            }

            var count = ResolveCount(baseName);
            var selected = button.BackColor == Accent || string.Equals(baseName, GetSelectedLibrary(), StringComparison.OrdinalIgnoreCase);
            button.Text = $"{baseName}    {count}";
            button.BackColor = selected ? Color.FromArgb(51, 38, 72) : SidebarBg;
            button.ForeColor = TextColor;
        }
    }

    static string GetSelectedLibrary()
    {
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        return form == null ? "Tous les jeux" : GetField<string>(form, "selectedLibrary") ?? "Tous les jeux";
    }

    static string StripTrailingCount(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(.*?)(?:\s+\d+)?$");
        return match.Success ? match.Groups[1].Value.Trim() : text.Trim();
    }

    static int ResolveCount(string name)
    {
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        var games = form == null ? null : GetFieldObject(form, "games") as System.Collections.IEnumerable;
        var all = games?.Cast<object>().ToList() ?? new List<object>();
        if (name.Equals("Tous les jeux", StringComparison.OrdinalIgnoreCase)) return all.Count;
        if (name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)) return all.Count(g => LibraryStore.IsFavorite(GetName(g)));
        if (name.Equals("Non classés", StringComparison.OrdinalIgnoreCase)) return all.Count(g => !LibraryStore.Load().Any(l => !l.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase) && l.Games.Any(x => x.Equals(GetName(g), StringComparison.OrdinalIgnoreCase))));
        return all.Count(g => LibraryStore.Contains(name, GetName(g)));
    }

    static T? GetField<T>(object form, string name) where T : class => GetFieldObject(form, name) as T;
    static object? GetFieldObject(object instance, string name)
    {
        try { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance); }
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
    static string GetName(object game) => GetProperty<string>(game, "Name") ?? string.Empty;
}