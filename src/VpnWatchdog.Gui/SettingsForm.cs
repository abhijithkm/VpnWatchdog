namespace VpnWatchdog.Gui;

/// <summary>
/// Modal editor for <see cref="GuiSettings"/>. Works on a private copy and hands it
/// back through <see cref="Result"/> only when the user presses OK; Cancel, Escape and
/// the close box leave the caller's instance untouched. Deliberately does NOT call
/// <see cref="GuiSettings.Save"/>: whether the edit is persisted, and how it is applied
/// to a monitoring session already in progress, is the caller's decision.
/// <para>
/// Nothing here touches the VPN - the dialog edits numbers and switches, that is all.
/// It has no credential field and must never grow one: FortiClient owns the saved
/// profile's credentials and this app never sees them. Auto-reconnect is armed by the
/// checkbox alone; nothing in this dialog infers it from any other setting.
/// </para>
/// </summary>
public sealed class SettingsForm : Form
{
    // Windows 11 palette: one accent, one hairline, one muted grey, one error red.
    private static readonly Color Accent = Color.FromArgb(0x00, 0x67, 0xC0);
    private static readonly Color AccentHover = Color.FromArgb(0x19, 0x75, 0xC5);
    private static readonly Color AccentPressed = Color.FromArgb(0x31, 0x83, 0xCA);
    private static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
    private static readonly Color Hairline = Color.FromArgb(0xE5, 0xE5, 0xE5);
    private static readonly Color MutedText = Color.FromArgb(0x6E, 0x6E, 0x6E);
    private static readonly Color ErrorText = Color.FromArgb(0xC4, 0x2B, 0x1C);

    private const int ColumnCount = 4;

    private readonly System.ComponentModel.IContainer components = new System.ComponentModel.Container();
    private readonly ToolTip toolTip;
    private readonly Font bodyFont = new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font sectionFont = new("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);

    private readonly TextBox txtProfileName = new();
    private readonly NumericUpDown nudPollInterval = Spinner(GuiSettingLimits.MinPollIntervalMs, GuiSettingLimits.MaxPollIntervalMs, increment: 500);
    private readonly CheckBox chkStartOnLaunch = Toggle("Start monitoring on launch");
    private readonly CheckBox chkMinimizeToTray = Toggle("Minimize to tray on close");

    private readonly CheckBox chkAutoReconnect = Toggle("Auto-reconnect");
    private readonly NumericUpDown nudGracePeriod = Spinner(GuiSettingLimits.MinGracePeriodSeconds, GuiSettingLimits.MaxGracePeriodSeconds, increment: 5);
    private readonly NumericUpDown nudMaxAttempts = Spinner(GuiSettingLimits.MinReconnectAttempts, GuiSettingLimits.MaxReconnectAttempts, increment: 1);
    private readonly NumericUpDown nudInitialBackoff = Spinner(GuiSettingLimits.MinBackoffSeconds, GuiSettingLimits.MaxInitialBackoffSeconds, increment: 5);
    private readonly NumericUpDown nudMaxBackoff = Spinner(GuiSettingLimits.MinBackoffSeconds, GuiSettingLimits.MaxMaxBackoffSeconds, increment: 30);
    private readonly NumericUpDown nudVerifyTimeout = Spinner(GuiSettingLimits.MinVerifyTimeoutSeconds, GuiSettingLimits.MaxVerifyTimeoutSeconds, increment: 5);

    private readonly Label lblValidation = new();
    private readonly LinkLabel lnkAbout = new();
    private readonly Button btnOk = new();
    private readonly Button btnCancel = new();

    private GuiSettings _result;

    /// <summary>The edited settings. Meaningful only when <see cref="Form.DialogResult"/> is OK.</summary>
    public GuiSettings Result => _result;

    public SettingsForm(GuiSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // A copy, so a Cancel half-way through leaves the caller's instance exactly
        // as it was - the dialog never writes into what it was given.
        _result = current.Clone();

        toolTip = new ToolTip(components);

        InitializeComponent();
        LoadFrom(_result);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components.Dispose();
        }

        base.Dispose(disposing);

        // After base: the controls that were using these fonts are gone by now.
        if (disposing)
        {
            bodyFont.Dispose();
            sectionFont.Dispose();
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "Settings";
        Font = bodyFont;
        BackColor = Surface;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        // Everything below is AutoSize inside one TableLayoutPanel, so the window is
        // exactly as large as its text needs at the current DPI and font. Nothing is
        // positioned by pixel and nothing can clip at 125% / 150%.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 10, 12, 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = ColumnCount,
            Margin = Padding.Empty,
        };
        for (int i = 0; i < ColumnCount; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        int row;

        // ---- Connection ------------------------------------------------------

        AddSectionHeader(layout, "Connection", first: true);

        txtProfileName.Width = 200;
        txtProfileName.MaxLength = 256;
        txtProfileName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        txtProfileName.Margin = new Padding(0, 2, 0, 2);
        toolTip.SetToolTip(txtProfileName, "The tunnel name exactly as FortiClient shows it.");
        // Any edit withdraws the "enter a profile name" message; it returns on OK if still empty.
        txtProfileName.TextChanged += (_, _) => lblValidation.Visible = false;

        row = NewRow(layout);
        Place(layout, FieldLabel("Profile name"), 0, row);
        Place(layout, txtProfileName, 1, row, span: 3);

        toolTip.SetToolTip(nudPollInterval, "How often the adapter, internet and FortiClient are checked.");
        row = NewRow(layout);
        Place(layout, FieldLabel("Poll interval (ms)"), 0, row);
        Place(layout, nudPollInterval, 1, row);

        toolTip.SetToolTip(chkStartOnLaunch, "Begin monitoring as soon as the window opens.");
        row = NewRow(layout);
        Place(layout, chkStartOnLaunch, 0, row, span: ColumnCount);

        toolTip.SetToolTip(chkMinimizeToTray, "Closing the window hides it in the tray instead of exiting.");
        row = NewRow(layout);
        Place(layout, chkMinimizeToTray, 0, row, span: ColumnCount);

        // ---- Auto-reconnect --------------------------------------------------

        AddSectionHeader(layout, "Auto-reconnect", first: false);

        toolTip.SetToolTip(chkAutoReconnect,
            "When the tunnel stays down, ask FortiClient to reconnect it.\nIt is never asked to disconnect.");
        row = NewRow(layout);
        Place(layout, chkAutoReconnect, 0, row, span: ColumnCount);

        var lblAutoReconnectHint = new Label
        {
            Text = "Waits the grace period so FortiClient can recover on its own first. Never disconnects.",
            AutoSize = true,
            ForeColor = MutedText,
            Anchor = AnchorStyles.Left,
            // Indented to sit under the checkbox caption rather than its box, and
            // capped in width so it wraps to two lines instead of stretching the whole
            // dialog to the length of one sentence.
            Margin = new Padding(18, 0, 0, 6),
            MaximumSize = new Size(300, 0),
        };
        row = NewRow(layout);
        Place(layout, lblAutoReconnectHint, 0, row, span: ColumnCount);

        toolTip.SetToolTip(nudGracePeriod,
            "How long FortiClient is left to recover by itself before the watchdog steps in.\nIt usually heals in 6-70 s on its own.");
        toolTip.SetToolTip(nudMaxAttempts, "Reconnect attempts per outage before giving up.");
        row = NewRow(layout);
        Place(layout, FieldLabel("Grace period (s)"), 0, row);
        Place(layout, nudGracePeriod, 1, row);
        Place(layout, FieldLabel("Max attempts", leftGap: 12), 2, row);
        Place(layout, nudMaxAttempts, 3, row);

        toolTip.SetToolTip(nudInitialBackoff, "Wait after the first failed attempt. Doubles after each further failure.");
        toolTip.SetToolTip(nudMaxBackoff, "Cap on that doubling wait.");
        // The cap can never sit below the starting value: raising the initial backoff
        // raises the floor of the max spinner, which pulls its value up if needed.
        nudInitialBackoff.ValueChanged += (_, _) => nudMaxBackoff.Minimum = nudInitialBackoff.Value;
        row = NewRow(layout);
        Place(layout, FieldLabel("Initial backoff (s)"), 0, row);
        Place(layout, nudInitialBackoff, 1, row);
        Place(layout, FieldLabel("Max backoff (s)", leftGap: 12), 2, row);
        Place(layout, nudMaxBackoff, 3, row);

        toolTip.SetToolTip(nudVerifyTimeout,
            "How long to wait for FortiClient to report the tunnel up\nbefore an attempt counts as failed.");
        row = NewRow(layout);
        Place(layout, FieldLabel("Verify timeout (s)"), 0, row);
        Place(layout, nudVerifyTimeout, 1, row);

        // ---- Validation + buttons --------------------------------------------

        // Inline rather than a MessageBox: a second modal on top of a small dialog is
        // heavier than the mistake it reports. Hidden rows take no space, so the
        // window only grows by one line when there is something to say.
        lblValidation.AutoSize = true;
        lblValidation.ForeColor = ErrorText;
        lblValidation.Visible = false;
        lblValidation.Anchor = AnchorStyles.Left;
        lblValidation.Margin = new Padding(0, 6, 0, 0);
        lblValidation.MaximumSize = new Size(300, 0);
        row = NewRow(layout);
        Place(layout, lblValidation, 0, row, span: ColumnCount);

        StyleButton(btnOk, "OK", accent: true);
        // No DialogResult on the button itself: WinForms would close the dialog before
        // the click handler had a chance to validate.
        btnOk.Click += BtnOk_Click;

        StyleButton(btnCancel, "Cancel", accent: false);
        btnCancel.DialogResult = DialogResult.Cancel;

        var pnlButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Right,
        };
        pnlButtons.Controls.Add(btnOk);
        pnlButtons.Controls.Add(btnCancel);

        lnkAbout.Text = "About VPN Watchdog";
        lnkAbout.AutoSize = true;
        lnkAbout.LinkColor = Accent;
        lnkAbout.ActiveLinkColor = AccentPressed;
        lnkAbout.LinkBehavior = LinkBehavior.HoverUnderline;
        lnkAbout.Dock = DockStyle.Left;
        lnkAbout.Margin = new Padding(0, 6, 0, 0);
        lnkAbout.TextAlign = ContentAlignment.MiddleLeft;
        lnkAbout.LinkClicked += (_, _) => ShowAbout();

        // A plain Dock-based row rather than another table cell: the outer layout's
        // columns are AutoSize, so anchoring two separate controls Left/Right inside
        // one AutoSize cell would collapse both to their own width instead of
        // spreading across the row's real (already-decided-by-other-rows) width. A
        // Fill host with Left/Right docked children uses that width directly.
        var pnlActions = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
            Height = pnlButtons.PreferredSize.Height,
        };
        pnlActions.Controls.Add(pnlButtons);
        pnlActions.Controls.Add(lnkAbout);

        row = NewRow(layout);
        Place(layout, pnlActions, 0, row, span: ColumnCount);

        layout.RowCount = layout.RowStyles.Count;
        Controls.Add(layout);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        ResumeLayout(true);
    }

    private void LoadFrom(GuiSettings settings)
    {
        txtProfileName.Text = settings.ProfileName;
        SetValue(nudPollInterval, settings.PollIntervalMs);
        chkStartOnLaunch.Checked = settings.StartMonitoringOnLaunch;
        chkMinimizeToTray.Checked = settings.MinimizeToTrayOnClose;

        chkAutoReconnect.Checked = settings.AutoReconnectEnabled;
        SetValue(nudGracePeriod, settings.ReconnectGracePeriodSeconds);
        SetValue(nudMaxAttempts, settings.ReconnectMaxAttempts);
        // Initial before max: the max spinner's floor follows the initial value.
        SetValue(nudInitialBackoff, settings.ReconnectInitialBackoffSeconds);
        SetValue(nudMaxBackoff, settings.ReconnectMaxBackoffSeconds);
        SetValue(nudVerifyTimeout, settings.ReconnectVerifyTimeoutSeconds);
    }

    private void ShowAbout()
    {
        using var about = new AboutForm();
        about.ShowDialog(this);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        string profileName = txtProfileName.Text.Trim();
        if (profileName.Length == 0)
        {
            lblValidation.Text = "Enter the FortiClient profile name to watch.";
            lblValidation.Visible = true;
            txtProfileName.Focus();
            return;
        }

        // Reading Value commits any text still being typed in a spinner, clamped to
        // the spinner's range - so every number below is already within limits.
        int initialBackoff = (int)nudInitialBackoff.Value;

        _result.ProfileName = profileName;
        _result.PollIntervalMs = (int)nudPollInterval.Value;
        _result.StartMonitoringOnLaunch = chkStartOnLaunch.Checked;
        _result.MinimizeToTrayOnClose = chkMinimizeToTray.Checked;

        // The checkbox is the one and only source of this value.
        _result.AutoReconnectEnabled = chkAutoReconnect.Checked;
        _result.ReconnectGracePeriodSeconds = (int)nudGracePeriod.Value;
        _result.ReconnectMaxAttempts = (int)nudMaxAttempts.Value;
        _result.ReconnectInitialBackoffSeconds = initialBackoff;
        // Belt and braces with the spinner coupling above: a value typed into the max
        // spinner and not yet committed when initial was raised could still be lower.
        _result.ReconnectMaxBackoffSeconds = Math.Max((int)nudMaxBackoff.Value, initialBackoff);
        _result.ReconnectVerifyTimeoutSeconds = (int)nudVerifyTimeout.Value;

        DialogResult = DialogResult.OK;
        Close();
    }

    // ------------------------------------------------------------------
    // Layout helpers
    // ------------------------------------------------------------------

    /// <summary>Starts a new AutoSize row and returns its index - heights come from the font, never from constants.</summary>
    private static int NewRow(TableLayoutPanel layout)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return layout.RowStyles.Count - 1;
    }

    private static void Place(TableLayoutPanel layout, Control control, int column, int row, int span = 1)
    {
        layout.Controls.Add(control, column, row);
        if (span > 1)
        {
            layout.SetColumnSpan(control, span);
        }
    }

    /// <summary>A bold caption over a 1px hairline: the section boundary without a GroupBox's frame and padding.</summary>
    private void AddSectionHeader(TableLayoutPanel layout, string title, bool first)
    {
        int row = NewRow(layout);
        Place(layout, new Label
        {
            Text = title,
            Font = sectionFont,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, first ? 0 : 10, 0, 2),
        }, 0, row, span: ColumnCount);

        row = NewRow(layout);
        Place(layout, new Panel
        {
            Size = new Size(1, 1),
            BackColor = Hairline,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 6),
        }, 0, row, span: ColumnCount);
    }

    /// <summary>Caption for a field. Left-anchored only, so the panel centres it on the control beside it.</summary>
    private static Label FieldLabel(string text, int leftGap = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(leftGap, 0, 6, 0),
    };

    private static NumericUpDown Spinner(int minimum, int maximum, int increment) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Increment = increment,
        Width = 64,
        TextAlign = HorizontalAlignment.Right,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 2, 0, 2),
    };

    private static CheckBox Toggle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 2, 0, 2),
    };

    /// <summary>
    /// Assigns a spinner value, pulled into the spinner's range first. An out-of-range
    /// Value throws, and a settings dialog must never crash over a stray number in a
    /// hand-built <see cref="GuiSettings"/>.
    /// </summary>
    private static void SetValue(NumericUpDown spinner, int value) =>
        spinner.Value = Math.Clamp(value, spinner.Minimum, spinner.Maximum);

    /// <summary>Flat Windows 11 buttons: a filled accent OK, an outlined Cancel. No gradients, no images.</summary>
    private static void StyleButton(Button button, string text, bool accent)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(84, 27);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = new Padding(6, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;

        if (accent)
        {
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentPressed;
        }
        else
        {
            button.BackColor = Color.White;
            button.ForeColor = SystemColors.ControlText;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Hairline;
            button.FlatAppearance.MouseOverBackColor = Surface;
            button.FlatAppearance.MouseDownBackColor = Hairline;
        }
    }
}
