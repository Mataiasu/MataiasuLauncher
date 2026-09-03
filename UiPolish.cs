using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class UiPolish
{
    static bool applied;
    static readonly Color Bg = Color.FromArgb(14, 12, 20);
    static readonly Color Panel2 = Color.FromArgb(31, 27, 45);
    static readonly Color SidebarBg = Color.FromArgb(20, 17, 29);
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

        var libraries = GetField<ListBox>(form, "libraries");
        var status = GetField<Label>(form, "status");
        var sidebar = GetField<Panel>(form, "sidebar");
        var cards = GetField<FlowLayoutPanel>(form, "cards");
        if (libraries == null || status == null || sidebar == null || cards == null) return;

        applied = true;
        Application.Idle -= ApplyWhenReady;
        FixSidebar(sidebar, libraries);
        FixStatusVisibility(status, form);
        cards.Padding = new Padding(20, 18, 20, 18);
        cards.Margin = new Padding(0);
        cards.WrapContents = true;
        cards.BackColor = Bg;
    }

    static void FixSidebar(Panel sidebar, ListBox libraries)
    {
        sidebar.Width = 242;
        sidebar.BackColor = SidebarBg;
        sidebar.Padding = new Padding(16, 18, 12, 12);

        var manage = sidebar.Controls.OfType<Button>().FirstOrDefault(b => b.Text.Contains("GÉRER", StringComparison.OrdinalIgnoreCase));
        var create = sidebar.Controls.OfType<Button>().FirstOrDefault(b => b.Text.Contains("CRÉER", StringComparison.OrdinalIgnoreCase));
        var sideStatus = sidebar.Controls.OfType<Label>().FirstOrDefault(l => l.Text.Contains("Organise", StringComparison.OrdinalIgnoreCase));
        var heading = sidebar.Controls.OfType<Label>().FirstOrDefault(l => l.Text.Equals("MA BIBLIOTHÈQUE", StringComparison.OrdinalIgnoreCase));

        if (heading != null)
        {
            heading.Dock = DockStyle.Top;
            heading.Height = 34;
            heading.ForeColor = Muted;
            heading.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        }

        sideStatus?.Dispose();

        if (manage != null) manage.Visible = false;
        if (create != null) create.Visible = false;

        var oldBottom = sidebar.Controls.OfType<Panel>().FirstOrDefault(p => Equals(p.Tag, "UiPolishBottom"));
        oldBottom?.Dispose();

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 90,
            BackColor = SidebarBg,
            Tag = "UiPolishBottom",
            Padding = new Padding(0, 8, 0, 0)
        };

        var manageNew = new Button
        {
            Text = "⚙  Gérer les bibliothèques",
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Panel2,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        manageNew.FlatAppearance.BorderSize = 0;
        if (manage != null) manageNew.Click += (_, _) => manage.PerformClick();

        var createNew = new Button
        {
            Text = "+  Créer une bibliothèque",
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        createNew.FlatAppearance.BorderSize = 0;
        if (create != null) createNew.Click += (_, _) => create.PerformClick();

        bottom.Controls.Add(createNew);
        bottom.Controls.Add(manageNew);
        sidebar.Controls.Add(bottom);
        bottom.BringToFront();

        libraries.Dock = DockStyle.Fill;
        libraries.BackColor = SidebarBg;
        libraries.ForeColor = TextColor;
        libraries.BorderStyle = BorderStyle.None;
        libraries.HorizontalScrollbar = false;
        libraries.ScrollAlwaysVisible = false;
        libraries.IntegralHeight = false;
        libraries.DrawMode = DrawMode.OwnerDrawFixed;
        libraries.ItemHeight = 38;
        libraries.DrawItem -= DrawLibrary;
        libraries.DrawItem += DrawLibrary;
    }

    static void DrawLibrary(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;
        var name = list.Items[e.Index]?.ToString() ?? string.Empty;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? Color.FromArgb(51, 38, 72) : SidebarBg;
        using var bg = new SolidBrush(background);
        e.Graphics.FillRectangle(bg, e.Bounds);

        var count = ResolveLibraryCount(name);
        using var font = new Font("Segoe UI", 10f, selected ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, name, font,
            new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 58), e.Bounds.Height),
            TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, count.ToString(), font,
            new Rectangle(e.Bounds.Right - 42, e.Bounds.Y, 30, e.Bounds.Height),
            Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    static int ResolveLibraryCount(string name)
    {
        try
        {
            var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            var games = form == null ? null : GetFieldObject(form, "games") as System.Collections.IEnumerable;
            var all = games?.Cast<object>().ToList() ?? new List<object>();

            if (name.Equals("Tous les jeux", StringComparison.OrdinalIgnoreCase)) return all.Count;
            if (name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase))
                return all.Count(g => LibraryStore.IsFavorite(GetName(g)));
            if (name.Equals("Non classés", StringComparison.OrdinalIgnoreCase))
                return all.Count(g => !GameBelongsToCustomLibrary(GetName(g)));

            return all.Count(g => LibraryStore.Contains(name, GetName(g)));
        }
        catch { return 0; }
    }

    static bool GameBelongsToCustomLibrary(string gameName) =>
        LibraryStore.Load().Any(l =>
            !l.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase) &&
            l.Games.Any(g => g.Equals(gameName, StringComparison.OrdinalIgnoreCase)));

    static void FixStatusVisibility(Label status, MainForm form)
    {
        var header = status.Parent;
        if (header == null) return;

        status.Dock = DockStyle.None;
        status.Location = new Point(276, 82);
        status.Size = new Size(Math.Max(260, header.ClientSize.Width - 300), 22);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        status.BackColor = Color.Transparent;
        status.ForeColor = Muted;
        status.Padding = new Padding(0);
        status.Font = new Font("Segoe UI", 9f);
        status.BringToFront();
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
