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

        var logo = FindLabels(form).FirstOrDefault(x => x.Text.Equals("MATAIASU", StringComparison.OrdinalIgnoreCase));
        if (logo == null || logo.Parent == null) return;

        applied = true;
        Application.Idle -= ApplyWhenReady;

        if (logo.Parent is Panel header)
        {
            header.Height = 124;
            logo.AutoSize = false;
            logo.Size = new Size(320, 36);
            logo.Location = new Point(26, 12);
            logo.Font = new Font("Segoe UI Semibold", 24f, FontStyle.Bold);
            logo.TextAlign = ContentAlignment.MiddleLeft;

            var subtitle = FindLabels(header).FirstOrDefault(x => x.Text.Equals("GAME LIBRARY", StringComparison.OrdinalIgnoreCase));
            if (subtitle != null)
            {
                subtitle.AutoSize = false;
                subtitle.Size = new Size(320, 22);
                subtitle.Location = new Point(29, 49);
                subtitle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                subtitle.TextAlign = ContentAlignment.MiddleLeft;
            }
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
