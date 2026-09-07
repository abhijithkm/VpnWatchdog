namespace VpnWatchdog.Gui;

/// <summary>
/// A Yes/No confirmation styled to match the rest of this app (rounded
/// buttons, the shared Windows 11 palette) instead of the generic system
/// <see cref="MessageBox"/> - the one dialog in the app that still looked
/// like default Windows chrome. Action-named buttons ("Disconnect" / "Cancel")
/// rather than bare "Yes"/"No": a screen reader or a glance at the button
/// itself should say what pressing it DOES, not force the user back up to
/// the question text to find out what "Yes" commits to.
/// </summary>
internal sealed class ConfirmDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
    private static readonly Color Hairline = Color.FromArgb(0xE5, 0xE5, 0xE5);
    private static readonly Color TextColor = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color Amber = Color.FromArgb(0x9A, 0x77, 0x00);

    private readonly System.ComponentModel.IContainer components = new System.ComponentModel.Container();
    private readonly Font bodyFont = new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font titleFont = new("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly RoundedButton btnConfirm = new();
    private readonly RoundedButton btnCancel = new();
    private readonly Font iconFont = new("Segoe UI", 22F, FontStyle.Regular, GraphicsUnit.Point);

    private ConfirmDialog(string title, string heading, string message, string confirmText, string cancelText, bool destructive)
    {
        InitializeComponent(title, heading, message, confirmText, cancelText, destructive);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components.Dispose();
            bodyFont.Dispose();
            titleFont.Dispose();
            iconFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Shows the dialog and returns true only if the confirm button was
    /// pressed. Defaults to the CANCEL side for both Enter and Escape (the
    /// same "default to No" safety <see cref="MessageBoxDefaultButton.Button2"/>
    /// gave the dialog this replaces) - a destructive confirmation must never
    /// fire from an absent-minded Enter press.
    /// </summary>
    public static bool Show(
        IWin32Window? owner,
        string title,
        string heading,
        string message,
        string confirmText,
        string cancelText,
        bool destructive = true)
    {
        using var dialog = new ConfirmDialog(title, heading, message, confirmText, cancelText, destructive);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private void InitializeComponent(string title, string heading, string message, string confirmText, string cancelText, bool destructive)
    {
        SuspendLayout();

        Text = title;
        Font = bodyFont;
        BackColor = Surface;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Absolute, not Percent: see AboutForm's identical comment on this exact
        // pattern - a Percent column here would size against this container's
        // own AutoSize width, a circular dependency that leaves a wrapping
        // Label's MaximumSize measured against a stale, too-narrow width.
        const int MessageWidth = 300;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, MessageWidth));
        root.RowCount = 2;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var iconLabel = new Label
        {
            Text = "⚠", // warning triangle - a plain Unicode glyph renders on every
                              // Windows version, unlike a Segoe Fluent/MDL2 codepoint.
            Font = iconFont,
            ForeColor = destructive ? Amber : MainForm.Palette.Blue,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 16, 0),
        };

        var headingLabel = new Label
        {
            Text = heading,
            Font = titleFont,
            ForeColor = TextColor,
            AutoSize = true,
            MaximumSize = new Size(MessageWidth, 0),
            Margin = new Padding(0, 0, 0, 8),
        };

        var messageLabel = new Label
        {
            Text = message,
            Font = bodyFont,
            ForeColor = TextColor,
            AutoSize = true,
            MaximumSize = new Size(MessageWidth, 0),
            Margin = Padding.Empty,
        };

        var textStack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        textStack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        textStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        textStack.Controls.Add(headingLabel, 0, 0);
        textStack.Controls.Add(messageLabel, 0, 1);

        root.Controls.Add(iconLabel, 0, 0);
        root.SetRowSpan(iconLabel, 1);
        root.Controls.Add(textStack, 1, 0);

        Color confirmAccent = destructive ? Color.FromArgb(0xC4, 0x2B, 0x1C) : MainForm.Palette.Blue;
        StyleButton(btnConfirm, confirmText, confirmAccent);
        StyleButton(btnCancel, cancelText, accentColor: null);
        btnCancel.DialogResult = DialogResult.Cancel;
        btnConfirm.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 16, 0, 0),
        };
        // RightToLeft flow with Cancel added first puts Cancel on the far
        // right and Confirm just left of it - the standard Windows button
        // order (primary-adjacent-to-edge reads right to left as OK/Yes,
        // then Cancel outermost - matching SettingsForm's own OK/Cancel order).
        buttonRow.Controls.Add(btnCancel);
        buttonRow.Controls.Add(btnConfirm);

        root.Controls.Add(buttonRow, 1, 1);

        Controls.Add(root);
        AcceptButton = btnCancel;
        CancelButton = btnCancel;

        ResumeLayout(true);
    }

    /// <summary>
    /// Flat Windows 11 button, matching SettingsForm's OK/Cancel styling
    /// exactly. <paramref name="accentColor"/> null means the neutral outlined
    /// style (Cancel); a color means a filled button in that color (Confirm).
    /// </summary>
    private void StyleButton(RoundedButton button, string text, Color? accentColor)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(84, 27);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = new Padding(6, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.TabStop = true;

        if (accentColor is { } accent)
        {
            button.BackColor = accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(accent, 0.08f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(accent, 0.16f);
        }
        else
        {
            button.BackColor = Color.White;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Hairline;
            button.FlatAppearance.MouseOverBackColor = Surface;
            button.FlatAppearance.MouseDownBackColor = Hairline;
        }
    }
}
