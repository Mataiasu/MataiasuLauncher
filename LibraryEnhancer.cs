using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class LibraryEnhancer
{
    static bool attached;
    static Form? form;
    static FlowLayoutPanel? cards;
    static ListBox? libraryList;
    static Label? countLabel;
    static System.Windows.Forms.Timer? refreshTimer;
    static readonly HashSet<Control> wiredCards = new();
    static string selectedLibrary = "Tous les jeux";

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += OnIdle;

    static void OnIdle(object? sender, EventArgs e)
    {
        if (attached) return;
        form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;
        cards = form.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
        if (cards == null) return;
        attached = true;
        try { LibraryStore.Create("★ Favoris"); } catch { }
        BuildSidebar(form);
        cards.ControlAdded += (_, _) => WireCards();
        WireCards();
        refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        refreshTimer.Tick += (_, _) =>
        {
            WireCards();
            ApplyLibraryFilter();
        };
        refreshTimer.Start();
    }

    static void BuildSidebar(Form target)
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 240,
            Padding = new Padding(16, 18, 12, 14),
            BackColor = Color.FromArgb(20, 17, 29)
        };
        var heading = new Label
        {
            Text = "MA BIBLIOTHÈQUE",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(166, 158, 181)
        };
        countLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(130, 121, 146)
        };
        var create = new Button
        {
            Text = "+  CRÉER UNE BIBLIOTHÈQUE",
            Dock = DockStyle.Bottom,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(163, 92, 255),
            ForeColor = Color.White
        };
        create.FlatAppearance.BorderSize = 0;
        create.Click += (_, _) => CreateLibraryDialog();
        var manage = new Button
        {
            Text = "⚙  GÉRER LES BIBLIOTHÈQUES",
            Dock = DockStyle.Bottom,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(31, 27, 45),
            ForeColor = Color.FromArgb(235, 230, 245)
        };
        manage.FlatAppearance.BorderSize = 0;
        manage.Click += (_, _) => ManageLibrariesDialog();
        libraryList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(20, 17, 29),
            ForeColor = Color.FromArgb(235, 230, 245),
            IntegralHeight = false,
            Font = new Font("Segoe UI", 10f)
        };
        libraryList.SelectedIndexChanged += (_, _) =>
        {
            if (libraryList.SelectedItem is string name)
            {
                selectedLibrary = name;
                ApplyLibraryFilter();
            }
        };
        sidebar.Controls.Add(libraryList);
        sidebar.Controls.Add(manage);
        sidebar.Controls.Add(create);
        sidebar.Controls.Add(countLabel);
        sidebar.Controls.Add(heading);
        target.Controls.Add(sidebar);
        sidebar.BringToFront();
        RefreshLibraries();
    }

    static void RefreshLibraries()
    {
        if (libraryList == null) return;
        var custom = LibraryStore.Load()
            .Select(x => x.Name)
            .Where(x => !x.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = new List<string> { "Tous les jeux", "★ Favoris", "Non classés" };
        names.AddRange(custom);
        var previous = selectedLibrary;
        libraryList.BeginUpdate();
        libraryList.Items.Clear();
        libraryList.Items.AddRange(names.Cast<object>().ToArray());
        libraryList.EndUpdate();
        var index = names.FindIndex(x => x.Equals(previous, StringComparison.OrdinalIgnoreCase));
        libraryList.SelectedIndex = index >= 0 ? index : 0;
    }

    static void ApplyLibraryFilter()
    {
        if (cards == null) return;
        var visible = 0;
        foreach (Control control in cards.Controls)
        {
            if (control.Tag is not Game game)
            {
                control.Visible = true;
                continue;
            }
            var show = selectedLibrary.Equals("Tous les jeux", StringComparison.OrdinalIgnoreCase)
                || (selectedLibrary.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase) && LibraryStore.IsFavorite(game.Name))
                || (selectedLibrary.Equals("Non classés", StringComparison.OrdinalIgnoreCase) && !IsInAnyLibrary(game.Name))
                || LibraryStore.Contains(selectedLibrary, game.Name);
            control.Visible = show;
            if (show) visible++;
        }
        if (countLabel != null) countLabel.Text = $"{visible} jeu(x) visible(s)";
    }

    static bool IsInAnyLibrary(string game) => LibraryStore.Load().Any(l => l.Games.Any(g => g.Equals(game, StringComparison.OrdinalIgnoreCase)));

    static void WireCards()
    {
        if (cards == null) return;
        foreach (Control card in cards.Controls)
        {
            if (card.Tag is not Game game || !wiredCards.Add(card)) continue;
            var menu = BuildContextMenu(game);
            card.ContextMenuStrip = menu;
            foreach (Control child in card.Controls) child.ContextMenuStrip = menu;
        }
    }

    static ContextMenuStrip BuildContextMenu(Game game)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(31, 27, 45),
            ForeColor = Color.FromArgb(245, 242, 250)
        };
        var add = new ToolStripMenuItem("Ajouter / retirer d'une bibliothèque");
        foreach (var library in LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ToolStripMenuItem(library.Name)
            {
                Checked = LibraryStore.Contains(library.Name, game.Name)
            };
            item.Click += (_, _) =>
            {
                if (LibraryStore.Contains(library.Name, game.Name)) LibraryStore.RemoveGame(library.Name, game.Name);
                else LibraryStore.AddGame(library.Name, game.Name);
                RefreshLibraries();
                ApplyLibraryFilter();
            };
            add.DropDownItems.Add(item);
        }
        if (add.DropDownItems.Count == 0)
        {
            var create = new ToolStripMenuItem("Créer une bibliothèque…");
            create.Click += (_, _) => CreateLibraryDialog();
            add.DropDownItems.Add(create);
        }
        menu.Items.Add(add);

        var favorite = new ToolStripMenuItem(LibraryStore.IsFavorite(game.Name) ? "Retirer des favoris" : "Ajouter aux favoris");
        favorite.Click += (_, _) =>
        {
            if (LibraryStore.IsFavorite(game.Name)) LibraryStore.RemoveFavorite(game.Name);
            else LibraryStore.AddFavorite(game.Name);
            RefreshLibraries();
            ApplyLibraryFilter();
            WireCards();
        };
        menu.Items.Add(favorite);

        var folder = new ToolStripMenuItem("Ouvrir le dossier du jeu");
        folder.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
                Process.Start(new ProcessStartInfo { FileName = game.InstallPath, UseShellExecute = true });
        };
        menu.Items.Add(folder);
        return menu;
    }

    static void CreateLibraryDialog()
    {
        using var dialog = new Form
        {
            Text = "Nouvelle bibliothèque",
            Width = 430,
            Height = 190,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(24, 21, 34),
            ForeColor = Color.White
        };
        var label = new Label { Text = "Nom de la bibliothèque", Left = 22, Top = 18, AutoSize = true };
        var input = new TextBox { Left = 22, Top = 48, Width = 365, BackColor = Color.FromArgb(31, 27, 45), ForeColor = Color.White };
        var ok = new Button { Text = "Créer", Left = 220, Top = 92, Width = 80, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(163, 92, 255), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Annuler", Left = 307, Top = 92, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = 0;
        cancel.FlatAppearance.BorderSize = 0;
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(form) != DialogResult.OK) return;
        var name = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)) return;
        if (!LibraryStore.Create(name))
            MessageBox.Show("Cette bibliothèque existe déjà.", "Mataiasu Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshLibraries();
    }

    static void ManageLibrariesDialog()
    {
        var libs = LibraryStore.Load().Where(x => !x.Name.Equals("★ Favoris", StringComparison.OrdinalIgnoreCase)).ToList();
        using var dialog = new Form
        {
            Text = "Gérer les bibliothèques",
            Width = 480,
            Height = 430,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(24, 21, 34),
            ForeColor = Color.White
        };
        var list = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 300,
            BackColor = Color.FromArgb(31, 27, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        list.Items.AddRange(libs.Select(x => $"{x.Name}   ·   {x.Games.Count} jeu(x)").Cast<object>().ToArray());
        var delete = new Button
        {
            Text = "Supprimer la bibliothèque sélectionnée",
            Dock = DockStyle.Bottom,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(70, 48, 84),
            ForeColor = Color.White
        };
        delete.FlatAppearance.BorderSize = 0;
        delete.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var library = libs[list.SelectedIndex];
            if (MessageBox.Show($"Supprimer « {library.Name} » ? Les jeux ne seront pas désinstallés.", "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            LibraryStore.Delete(library.Name);
            dialog.Close();
            RefreshLibraries();
            ApplyLibraryFilter();
        };
        dialog.Controls.Add(delete);
        dialog.Controls.Add(list);
        dialog.ShowDialog(form);
    }
}
