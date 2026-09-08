using System.Drawing;
using System.Windows.Forms;

namespace DisplaySwitch;

/// <summary>Minimal single-line prompt -- WinForms has no built-in input box.</summary>
internal static class InputDialog
{
    public static string? Ask(string title, string prompt, string initialValue = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 130),
        };

        var label = new Label
        {
            Text = prompt,
            AutoSize = false,
            Bounds = new Rectangle(12, 12, 336, 34),
        };
        var input = new TextBox
        {
            Text = initialValue,
            Bounds = new Rectangle(12, 52, 336, 23),
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(192, 90, 75, 26),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(273, 90, 75, 26),
        };

        form.Controls.AddRange(new Control[] { label, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.TopMost = true;

        try
        {
            if (Environment.ProcessPath is { } exe) form.Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Title-bar icon only; not worth failing the dialog over.
        }

        var result = form.ShowDialog();
        var value = input.Text.Trim();
        return result == DialogResult.OK && value.Length > 0 ? value : null;
    }
}
