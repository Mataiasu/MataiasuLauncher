using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class LogoPolish
{
    static bool applied;

    [ModuleInitializer]
    internal static void Initialize() => Application.Idle += ApplyWhenReady;

    static void ApplyWhenReady(object? sender, EventArgs e)
    {
        if (applied) return;
        var form = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (form == null) return;

        applied = true;
        Application.Idle -= ApplyWhenReady;
        form.Text = "Launch'aiasu";

        var labels = FindLabels(form).ToList();
        var logo = labels.FirstOrDefault(x => x.Text.Equals("MATAIASU", StringComparison.OrdinalIgnoreCase));
        if (logo == null || logo.Parent is not Panel header) return;

        header.Height = 128;
        logo.Text = "LAUNCH'AIASU";
        logo.AutoSize = false;
        logo.Size = new Size(390, 40);
        logo.Location = new Point(26, 8);
        logo.Font = new Font("Segoe UI Semibold", 24f, FontStyle.Bold);
        logo.TextAlign = ContentAlignment.MiddleLeft;
        logo.Padding = new Padding(0);

        var subtitle = labels.FirstOrDefault(x => x.Text.Equals("GAME LIBRARY", StringComparison.OrdinalIgnoreCase));
        if (subtitle != null)
        {
            subtitle.AutoSize = false;
            subtitle.Size = new Size(390, 24);
            subtitle.Location = new Point(29, 49);
            subtitle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            subtitle.TextAlign = ContentAlignment.MiddleLeft;
            subtitle.Padding = new Padding(0, 1, 0, 0);
            subtitle.Margin = new Padding(0);
        }
    }

    static IEnumerable<Label> FindLabels(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label label) yield return label;
            foreach (var nested in FindLabels(child)) yield return nested;
        }
    }
}
