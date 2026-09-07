#nullable disable

namespace VpnWatchdog.Gui;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private TableLayoutPanel rootLayout;

    // Header
    private TableLayoutPanel headerLayout;
    private Label lblAppGlyph;
    private Label lblAppTitle;
    private Label lblAppSubtitle;
    private Label lblMonitoring;
    private Button btnMonitorToggle;
    private Button btnSettings;

    // Hero status
    private RoundedTablePanel pnlHero;
    private Label lblHeroGlyph;
    private Label lblHeroTitle;
    private Label lblHeroProfile;
    private Label lblHeroSub;

    // Diagnostics + uptime / last event (one table so the value columns line up)
    private TableLayoutPanel statusLayout;
    private Label lblInternetCaption;
    private Label lblInternetValue;
    private Label lblInternetExtra;
    private Label lblAdapterCaption;
    private Label lblAdapterValue;
    private Label lblAdapterExtra;
    private Label lblFortiCaption;
    private Label lblFortiValue;
    private Label lblFortiExtra;
    private Label lblNetworkCaption;
    private Label lblNetworkValue;
    private Label lblNetworkExtra;
    private Panel sepStatus;
    private Label lblUptimeCaption;
    private Label lblUptimeValue;
    private Label lblLastEventCaption;
    private Label lblLastEventValue;
    private Label lblLastEventTime;

    // Auto-reconnect
    private Panel sepAuto;
    private TableLayoutPanel autoLayout;
    private CheckBox chkAutoReconnect;
    private Label lblAutoReconnectHint;

    // Actions
    private TableLayoutPanel actionLayout;
    private Panel pnlAction;
    private Button btnAction;
    private Button btnViewLogs;

    // Footer
    private Panel sepFooter;
    private TableLayoutPanel footerLayout;
    private Label lblMode;
    private Label lblModeInfo;
    private Label lblVersion;

    private ToolTip toolTip;
    private NotifyIcon trayIcon;
    private ContextMenuStrip trayMenu;
    private ToolStripMenuItem trayMenuShow;
    private ToolStripMenuItem trayMenuExit;
    private System.Windows.Forms.Timer pollTimer;
    // Deliberately separate from pollTimer: update-checking has nothing to do
    // with VPN monitoring and must keep working (and keep re-checking) whether
    // Start/Stop is on or off - see MainForm.cs's BeginUpdateCheck.
    private System.Windows.Forms.Timer updateCheckTimer;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        toolTip = new ToolTip(components);
        trayMenu = new ContextMenuStrip(components);
        trayMenuShow = new ToolStripMenuItem();
        trayMenuExit = new ToolStripMenuItem();
        trayIcon = new NotifyIcon(components);
        pollTimer = new System.Windows.Forms.Timer(components);
        updateCheckTimer = new System.Windows.Forms.Timer(components);

        rootLayout = new TableLayoutPanel();
        headerLayout = new TableLayoutPanel();
        lblAppGlyph = new Label();
        lblAppTitle = new Label();
        lblAppSubtitle = new Label();
        lblMonitoring = new Label();
        btnMonitorToggle = new Button();
        btnSettings = new Button();
        pnlHero = new RoundedTablePanel();
        lblHeroGlyph = new Label();
        lblHeroTitle = new Label();
        lblHeroProfile = new Label();
        lblHeroSub = new Label();
        statusLayout = new TableLayoutPanel();
        lblInternetCaption = new Label();
        lblInternetValue = new Label();
        lblInternetExtra = new Label();
        lblAdapterCaption = new Label();
        lblAdapterValue = new Label();
        lblAdapterExtra = new Label();
        lblFortiCaption = new Label();
        lblFortiValue = new Label();
        lblFortiExtra = new Label();
        lblNetworkCaption = new Label();
        lblNetworkValue = new Label();
        lblNetworkExtra = new Label();
        sepStatus = MakeSeparator();
        lblUptimeCaption = new Label();
        lblUptimeValue = new Label();
        lblLastEventCaption = new Label();
        lblLastEventValue = new Label();
        lblLastEventTime = new Label();
        sepAuto = MakeSeparator();
        autoLayout = new TableLayoutPanel();
        chkAutoReconnect = new CheckBox();
        lblAutoReconnectHint = new Label();
        actionLayout = new TableLayoutPanel();
        pnlAction = new Panel();
        btnAction = new Button();
        btnViewLogs = new Button();
        sepFooter = MakeSeparator();
        footerLayout = new TableLayoutPanel();
        lblMode = new Label();
        lblModeInfo = new Label();
        lblVersion = new Label();

        rootLayout.SuspendLayout();
        headerLayout.SuspendLayout();
        pnlHero.SuspendLayout();
        statusLayout.SuspendLayout();
        autoLayout.SuspendLayout();
        actionLayout.SuspendLayout();
        footerLayout.SuspendLayout();
        SuspendLayout();

        // Every size below is in points (fonts) or 96-DPI logical pixels (everything
        // else); AutoScaleMode.Dpi scales the pixels and GDI+ scales the points, so
        // the same numbers hold at 100/125/150%.
        Font bodyFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Font smallFont = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        Font titleFont = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        Font heroTitleFont = new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
        Font heroSubFont = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);

        // ------------------------------------------------------------------
        // Root: one column, AutoSize rows, a single percent row above the footer
        // that soaks up whatever height the content did not need. Nothing here is
        // positioned by hand, which is what keeps 125%/150% DPI from overlapping.
        // ------------------------------------------------------------------
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.BackColor = Palette.Surface;
        rootLayout.Padding = new Padding(12, 10, 12, 8);
        rootLayout.ColumnCount = 1;
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowCount = 9;
        for (int i = 0; i < 9; i++)
        {
            rootLayout.RowStyles.Add(i == 6
                ? new RowStyle(SizeType.Percent, 100F)
                : new RowStyle(SizeType.AutoSize));
        }

        // ------------------------------------------------------------------
        // Header: glyph | title / subtitle | monitoring toggle | settings
        // ------------------------------------------------------------------
        headerLayout.Dock = DockStyle.Fill;
        headerLayout.AutoSize = true;
        headerLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerLayout.Margin = new Padding(0);
        headerLayout.ColumnCount = 4;
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.RowCount = 2;
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblAppGlyph.Text = Glyphs.Shield;
        lblAppGlyph.Font = Glyphs.Font(15F);
        lblAppGlyph.ForeColor = Palette.Blue;
        lblAppGlyph.AutoSize = true;
        lblAppGlyph.Anchor = AnchorStyles.None;
        lblAppGlyph.Margin = new Padding(0, 0, 8, 0);

        lblAppTitle.Text = "VPN Watchdog";
        lblAppTitle.Font = titleFont;
        lblAppTitle.ForeColor = Palette.Text;
        lblAppTitle.AutoSize = true;
        lblAppTitle.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        lblAppTitle.Margin = new Padding(0);

        lblAppSubtitle.Text = "Monitor · Recover · Stay Connected";
        lblAppSubtitle.Font = smallFont;
        lblAppSubtitle.ForeColor = Palette.Muted;
        lblAppSubtitle.AutoSize = true;
        lblAppSubtitle.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        lblAppSubtitle.Margin = new Padding(0);

        // Read-only status - what IS happening. The action that CHANGES it lives
        // in btnMonitorToggle right next to it: a plain status label that silently
        // doubles as a button (as this used to be) gave users no visual sign it was
        // clickable at all, and "click the current state to flip it" reads as a
        // description, not a control - hence a real bordered button with a verb.
        lblMonitoring.Text = "○ Not monitoring";
        lblMonitoring.Font = bodyFont;
        lblMonitoring.ForeColor = Palette.Muted;
        lblMonitoring.AutoSize = true;
        lblMonitoring.Anchor = AnchorStyles.Right;
        lblMonitoring.Margin = new Padding(8, 0, 6, 0);

        btnMonitorToggle.Text = "Start";
        btnMonitorToggle.Font = smallFont;
        btnMonitorToggle.ForeColor = Palette.Blue;
        btnMonitorToggle.BackColor = Palette.Surface;
        btnMonitorToggle.FlatStyle = FlatStyle.Flat;
        btnMonitorToggle.FlatAppearance.BorderSize = 1;
        btnMonitorToggle.FlatAppearance.BorderColor = Palette.ButtonBorder;
        btnMonitorToggle.FlatAppearance.MouseOverBackColor = Palette.Hover;
        btnMonitorToggle.FlatAppearance.MouseDownBackColor = Palette.Pressed;
        btnMonitorToggle.AutoSize = true;
        btnMonitorToggle.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btnMonitorToggle.Padding = new Padding(10, 2, 10, 2);
        btnMonitorToggle.Margin = new Padding(12, 0, 0, 0);
        btnMonitorToggle.Anchor = AnchorStyles.None;
        btnMonitorToggle.Cursor = Cursors.Hand;
        btnMonitorToggle.TabStop = false;
        toolTip.SetToolTip(btnMonitorToggle, "Start or stop monitoring the VPN");
        btnMonitorToggle.Click += BtnMonitorToggle_Click;

        btnSettings.Text = Glyphs.Settings;
        btnSettings.Font = Glyphs.Font(10F);
        btnSettings.ForeColor = Palette.Muted;
        btnSettings.BackColor = Palette.Surface;
        btnSettings.FlatStyle = FlatStyle.Flat;
        btnSettings.FlatAppearance.BorderSize = 0;
        btnSettings.FlatAppearance.MouseOverBackColor = Palette.Hover;
        btnSettings.FlatAppearance.MouseDownBackColor = Palette.Pressed;
        btnSettings.Size = new Size(28, 28);
        btnSettings.Anchor = AnchorStyles.Right;
        btnSettings.Margin = new Padding(0);
        btnSettings.Cursor = Cursors.Hand;
        btnSettings.TabStop = false;
        toolTip.SetToolTip(btnSettings, "Settings");
        btnSettings.Click += BtnSettings_Click;

        headerLayout.Controls.Add(lblAppGlyph, 0, 0);
        headerLayout.SetRowSpan(lblAppGlyph, 2);
        headerLayout.Controls.Add(lblAppTitle, 1, 0);
        headerLayout.Controls.Add(lblAppSubtitle, 1, 1);
        headerLayout.Controls.Add(lblMonitoring, 2, 0);
        headerLayout.SetRowSpan(lblMonitoring, 2);
        headerLayout.Controls.Add(btnSettings, 3, 0);
        headerLayout.SetRowSpan(btnSettings, 2);

        // ------------------------------------------------------------------
        // Hero: the one thing that dominates. Tinted, rounded, state-coloured.
        // ------------------------------------------------------------------
        pnlHero.Dock = DockStyle.Fill;
        pnlHero.AutoSize = true;
        pnlHero.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        pnlHero.Margin = new Padding(0, 8, 0, 8);
        pnlHero.Padding = new Padding(12, 8, 12, 8);
        pnlHero.ColumnCount = 3;
        pnlHero.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pnlHero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pnlHero.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pnlHero.RowCount = 3;
        pnlHero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnlHero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnlHero.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblHeroGlyph.Text = Glyphs.Unknown;
        lblHeroGlyph.Font = Glyphs.Font(24F);
        lblHeroGlyph.ForeColor = Palette.Muted;
        lblHeroGlyph.AutoSize = true;
        lblHeroGlyph.Anchor = AnchorStyles.None;
        lblHeroGlyph.Margin = new Padding(0, 0, 12, 0);

        // The three text rows are fixed-height + AutoEllipsis rather than AutoSize:
        // a long profile name must be cut short, never wrapped into a taller hero.
        lblHeroTitle.Text = "Not monitoring";
        lblHeroTitle.Font = heroTitleFont;
        lblHeroTitle.ForeColor = Palette.Muted;
        lblHeroTitle.AutoSize = false;
        lblHeroTitle.AutoEllipsis = true;
        lblHeroTitle.Dock = DockStyle.Fill;
        lblHeroTitle.Height = 28;
        lblHeroTitle.TextAlign = ContentAlignment.MiddleLeft;
        lblHeroTitle.Margin = new Padding(0);

        lblHeroProfile.Text = "";
        lblHeroProfile.Font = bodyFont;
        lblHeroProfile.ForeColor = Palette.Text;
        lblHeroProfile.AutoSize = false;
        lblHeroProfile.AutoEllipsis = true;
        lblHeroProfile.Dock = DockStyle.Fill;
        lblHeroProfile.Height = 18;
        lblHeroProfile.TextAlign = ContentAlignment.MiddleLeft;
        lblHeroProfile.Margin = new Padding(0);

        lblHeroSub.Text = "";
        lblHeroSub.Font = heroSubFont;
        lblHeroSub.ForeColor = Palette.Muted;
        lblHeroSub.AutoSize = false;
        lblHeroSub.AutoEllipsis = true;
        lblHeroSub.Dock = DockStyle.Fill;
        lblHeroSub.Height = 16;
        lblHeroSub.TextAlign = ContentAlignment.MiddleLeft;
        lblHeroSub.Margin = new Padding(0);
        // Cursor/tooltip are toggled per-render in RenderHero (only meaningful
        // while a profile-mismatch nudge is showing); the click handler itself is
        // wired once, here, and no-ops otherwise.
        lblHeroSub.Click += LblHeroSub_Click;

        pnlHero.Controls.Add(lblHeroGlyph, 0, 0);
        pnlHero.SetRowSpan(lblHeroGlyph, 3);
        pnlHero.Controls.Add(lblHeroTitle, 1, 0);
        pnlHero.Controls.Add(lblHeroProfile, 1, 1);
        pnlHero.Controls.Add(lblHeroSub, 1, 2);
        pnlHero.Controls.Add(btnMonitorToggle, 2, 0);
        pnlHero.SetRowSpan(btnMonitorToggle, 3);

        // ------------------------------------------------------------------
        // Diagnostics + uptime / last event: caption | dot + word | right detail
        // ------------------------------------------------------------------
        statusLayout.Dock = DockStyle.Fill;
        statusLayout.AutoSize = true;
        statusLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        statusLayout.Margin = new Padding(0);
        statusLayout.ColumnCount = 3;
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLayout.RowCount = 8;
        for (int i = 0; i < 8; i++)
        {
            statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        Padding rowMargin = new Padding(0, 3, 0, 3);
        Padding captionMargin = new Padding(0, 3, 12, 3);
        Padding extraMargin = new Padding(8, 3, 0, 3);

        StyleCaption(lblInternetCaption, "Internet", bodyFont, captionMargin);
        StyleValue(lblInternetValue, bodyFont, rowMargin);
        StyleExtra(lblInternetExtra, smallFont, extraMargin);

        StyleCaption(lblAdapterCaption, "VPN Adapter", bodyFont, captionMargin);
        StyleValue(lblAdapterValue, bodyFont, rowMargin);
        StyleExtra(lblAdapterExtra, smallFont, extraMargin);

        StyleCaption(lblFortiCaption, "FortiClient", bodyFont, captionMargin);
        StyleValue(lblFortiValue, bodyFont, rowMargin);
        StyleExtra(lblFortiExtra, smallFont, extraMargin);

        StyleCaption(lblNetworkCaption, "Network", bodyFont, captionMargin);
        // Not StyleValue: that sets the "○ Unknown" dot-status convention used
        // by the boolean Up/Down/Running rows above. This row is a live number,
        // not a status - same plain-text treatment as lblUptimeValue below.
        lblNetworkValue.Text = NoThroughput;
        lblNetworkValue.Font = bodyFont;
        lblNetworkValue.ForeColor = Palette.Text;
        lblNetworkValue.AutoSize = true;
        lblNetworkValue.Anchor = AnchorStyles.Left;
        lblNetworkValue.Margin = rowMargin;
        StyleExtra(lblNetworkExtra, smallFont, extraMargin);

        StyleCaption(lblUptimeCaption, "VPN Uptime", bodyFont, captionMargin);
        lblUptimeValue.Text = "--:--:--";
        lblUptimeValue.Font = bodyFont;
        lblUptimeValue.ForeColor = Palette.Text;
        lblUptimeValue.AutoSize = true;
        lblUptimeValue.Anchor = AnchorStyles.Left;
        lblUptimeValue.Margin = rowMargin;

        StyleCaption(lblLastEventCaption, "Last Event", bodyFont, captionMargin);
        lblLastEventValue.Text = "";
        lblLastEventValue.Font = bodyFont;
        lblLastEventValue.ForeColor = Palette.Muted;
        lblLastEventValue.AutoSize = false;
        lblLastEventValue.AutoEllipsis = true;
        lblLastEventValue.Dock = DockStyle.Fill;
        lblLastEventValue.Height = 18;
        lblLastEventValue.TextAlign = ContentAlignment.MiddleLeft;
        lblLastEventValue.Margin = new Padding(0, 3, 0, 0);

        lblLastEventTime.Text = "";
        lblLastEventTime.Font = smallFont;
        lblLastEventTime.ForeColor = Palette.Muted;
        lblLastEventTime.AutoSize = true;
        lblLastEventTime.Anchor = AnchorStyles.Left;
        lblLastEventTime.Margin = new Padding(0, 0, 0, 3);

        statusLayout.Controls.Add(lblInternetCaption, 0, 0);
        statusLayout.Controls.Add(lblInternetValue, 1, 0);
        statusLayout.Controls.Add(lblInternetExtra, 2, 0);
        statusLayout.Controls.Add(lblAdapterCaption, 0, 1);
        statusLayout.Controls.Add(lblAdapterValue, 1, 1);
        statusLayout.Controls.Add(lblAdapterExtra, 2, 1);
        statusLayout.Controls.Add(lblFortiCaption, 0, 2);
        statusLayout.Controls.Add(lblFortiValue, 1, 2);
        statusLayout.Controls.Add(lblFortiExtra, 2, 2);
        statusLayout.Controls.Add(lblNetworkCaption, 0, 3);
        statusLayout.Controls.Add(lblNetworkValue, 1, 3);
        statusLayout.Controls.Add(lblNetworkExtra, 2, 3);
        statusLayout.Controls.Add(sepStatus, 0, 4);
        statusLayout.SetColumnSpan(sepStatus, 3);
        statusLayout.Controls.Add(lblUptimeCaption, 0, 5);
        statusLayout.Controls.Add(lblUptimeValue, 1, 5);
        statusLayout.Controls.Add(lblLastEventCaption, 0, 6);
        statusLayout.Controls.Add(lblLastEventValue, 1, 6);
        statusLayout.SetColumnSpan(lblLastEventValue, 2);
        statusLayout.Controls.Add(lblLastEventTime, 1, 7);
        statusLayout.SetColumnSpan(lblLastEventTime, 2);

        // ------------------------------------------------------------------
        // Auto-reconnect opt-in. Unchecked here; MainForm syncs it from saved
        // settings. The watchdog stays observe-only until the user asks otherwise.
        // ------------------------------------------------------------------
        autoLayout.Dock = DockStyle.Fill;
        autoLayout.AutoSize = true;
        autoLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        autoLayout.Margin = new Padding(0);
        autoLayout.ColumnCount = 1;
        autoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        autoLayout.RowCount = 2;
        autoLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        autoLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        chkAutoReconnect.Text = "Auto-reconnect";
        chkAutoReconnect.Font = bodyFont;
        chkAutoReconnect.ForeColor = Palette.Text;
        chkAutoReconnect.AutoSize = true;
        chkAutoReconnect.Checked = false;
        chkAutoReconnect.Cursor = Cursors.Hand;
        chkAutoReconnect.Margin = new Padding(0, 2, 0, 0);
        toolTip.SetToolTip(chkAutoReconnect,
            "When checked, the watchdog asks FortiClient to reconnect this profile\n" +
            "if it stays down past the grace period. It never disconnects the VPN.");
        chkAutoReconnect.CheckedChanged += ChkAutoReconnect_CheckedChanged;

        lblAutoReconnectHint.Text = "Reconnect automatically after an unexpected disconnect.";
        lblAutoReconnectHint.Font = smallFont;
        lblAutoReconnectHint.ForeColor = Palette.Muted;
        lblAutoReconnectHint.AutoSize = true;
        lblAutoReconnectHint.Margin = new Padding(20, 0, 0, 2);

        autoLayout.Controls.Add(chkAutoReconnect, 0, 0);
        autoLayout.Controls.Add(lblAutoReconnectHint, 0, 1);

        // ------------------------------------------------------------------
        // Actions: one contextual primary button (Connect / Disconnect /
        // Reconnecting...) and View Logs.
        // ------------------------------------------------------------------
        actionLayout.Dock = DockStyle.Fill;
        actionLayout.AutoSize = true;
        actionLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        actionLayout.Margin = new Padding(0, 6, 0, 0);
        actionLayout.ColumnCount = 2;
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionLayout.RowCount = 1;
        actionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // The button sits in a Panel for one reason: WinForms sends no mouse
        // messages to a DISABLED control, so a tooltip on the button itself would be
        // invisible exactly when it has something to explain ("a transition is in
        // progress"). The panel behind it does get them. Do not "simplify" it away.
        pnlAction.Dock = DockStyle.Fill;
        pnlAction.Height = 30;
        pnlAction.Margin = new Padding(0, 0, 8, 0);

        btnAction.Text = "Connect";
        btnAction.Font = bodyFont;
        btnAction.BackColor = Palette.Blue;
        btnAction.ForeColor = Color.White;
        btnAction.FlatStyle = FlatStyle.Flat;
        btnAction.FlatAppearance.BorderSize = 0;
        btnAction.Dock = DockStyle.Fill;
        btnAction.Cursor = Cursors.Hand;
        btnAction.Click += BtnAction_Click;
        pnlAction.Controls.Add(btnAction);

        btnViewLogs.Text = "View Logs";
        btnViewLogs.Font = bodyFont;
        btnViewLogs.BackColor = Color.White;
        btnViewLogs.ForeColor = Palette.Text;
        btnViewLogs.FlatStyle = FlatStyle.Flat;
        btnViewLogs.FlatAppearance.BorderSize = 1;
        btnViewLogs.FlatAppearance.BorderColor = Palette.ButtonBorder;
        btnViewLogs.FlatAppearance.MouseOverBackColor = Palette.Hover;
        btnViewLogs.FlatAppearance.MouseDownBackColor = Palette.Pressed;
        btnViewLogs.Size = new Size(96, 30);
        btnViewLogs.Anchor = AnchorStyles.Right;
        btnViewLogs.Margin = new Padding(0);
        btnViewLogs.Cursor = Cursors.Hand;
        toolTip.SetToolTip(btnViewLogs, "What the watchdog observed and did, newest first.");
        btnViewLogs.Click += BtnViewLogs_Click;

        actionLayout.Controls.Add(pnlAction, 0, 0);
        actionLayout.Controls.Add(btnViewLogs, 1, 0);

        // ------------------------------------------------------------------
        // Footer: mode + (i) | version
        // ------------------------------------------------------------------
        footerLayout.Dock = DockStyle.Fill;
        footerLayout.AutoSize = true;
        footerLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        footerLayout.Margin = new Padding(0);
        footerLayout.ColumnCount = 4;
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerLayout.RowCount = 1;
        footerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        lblMode.Text = "Mode: Observe Only";
        lblMode.Font = smallFont;
        lblMode.ForeColor = Palette.Muted;
        lblMode.AutoSize = true;
        lblMode.Anchor = AnchorStyles.Left;
        lblMode.Margin = new Padding(0, 0, 4, 0);
        toolTip.SetToolTip(lblMode, ModeTooltip);

        lblModeInfo.Text = Glyphs.Info;
        lblModeInfo.Font = Glyphs.Font(8F);
        lblModeInfo.ForeColor = Palette.Muted;
        lblModeInfo.AutoSize = true;
        lblModeInfo.Anchor = AnchorStyles.Left;
        lblModeInfo.Margin = new Padding(0);
        lblModeInfo.Cursor = Cursors.Help;
        toolTip.SetToolTip(lblModeInfo, ModeTooltip);

        lblVersion.Text = "v1.0.0";
        lblVersion.Font = smallFont;
        lblVersion.ForeColor = Palette.Muted;
        lblVersion.AutoSize = true;
        lblVersion.Anchor = AnchorStyles.Right;
        lblVersion.Margin = new Padding(0);
        // Cursor/tooltip toggle per-render in RenderVersionLabel (only meaningful
        // once an update is known to be available); the click handler is wired
        // once, here, and no-ops otherwise.
        lblVersion.Click += LblVersion_Click;

        footerLayout.Controls.Add(lblMode, 0, 0);
        footerLayout.Controls.Add(lblModeInfo, 1, 0);
        footerLayout.Controls.Add(lblVersion, 3, 0);

        // ------------------------------------------------------------------
        // Assemble
        // ------------------------------------------------------------------
        rootLayout.Controls.Add(headerLayout, 0, 0);
        rootLayout.Controls.Add(pnlHero, 0, 1);
        rootLayout.Controls.Add(statusLayout, 0, 2);
        rootLayout.Controls.Add(sepAuto, 0, 3);
        rootLayout.Controls.Add(autoLayout, 0, 4);
        rootLayout.Controls.Add(actionLayout, 0, 5);
        // row 6: percent spacer, deliberately empty
        rootLayout.Controls.Add(sepFooter, 0, 7);
        rootLayout.Controls.Add(footerLayout, 0, 8);

        // Tray
        trayMenuShow.Text = "Show";
        trayMenuShow.Click += (_, _) => RestoreFromTray();
        trayMenuExit.Text = "Exit";
        trayMenuExit.Click += (_, _) => ExitFromTray();
        trayMenu.Items.Add(trayMenuShow);
        trayMenu.Items.Add(trayMenuExit);

        trayIcon.Text = "VPN Watchdog";
        // Icon is set from the embedded app icon in MainForm's constructor, after
        // InitializeComponent runs - not here, so there is exactly one place that
        // decides it.
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = false;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        pollTimer.Interval = 2000;
        pollTimer.Tick += PollTimer_Tick;
        // Re-check cadence for a GUI that may stay open for days; the first,
        // immediate check happens separately, from the constructor.
        updateCheckTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        updateCheckTimer.Tick += UpdateCheckTimer_Tick;

        // Form
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 408);
        BackColor = Palette.Surface;
        Font = bodyFont;
        Controls.Add(rootLayout);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "VPN Watchdog";
        Load += MainForm_Load;
        Resize += MainForm_Resize;
        FormClosing += MainForm_FormClosing;
        FormClosed += MainForm_FormClosed;

        footerLayout.ResumeLayout(false);
        footerLayout.PerformLayout();
        actionLayout.ResumeLayout(false);
        actionLayout.PerformLayout();
        autoLayout.ResumeLayout(false);
        autoLayout.PerformLayout();
        statusLayout.ResumeLayout(false);
        statusLayout.PerformLayout();
        pnlHero.ResumeLayout(false);
        pnlHero.PerformLayout();
        headerLayout.ResumeLayout(false);
        headerLayout.PerformLayout();
        rootLayout.ResumeLayout(false);
        rootLayout.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    private const string ModeTooltip =
        "Observe Only: watches and logs, never touches the VPN.\n" +
        "Auto-reconnect: may ask FortiClient to connect after the grace period.\n" +
        "The watchdog never disconnects on its own.";

    private static void StyleCaption(Label label, string text, Font font, Padding margin)
    {
        label.Text = text;
        label.Font = font;
        label.ForeColor = Palette.Muted;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = margin;
    }

    private static void StyleValue(Label label, Font font, Padding margin)
    {
        label.Text = "○ Unknown";
        label.Font = font;
        label.ForeColor = Palette.Muted;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = margin;
    }

    private static void StyleExtra(Label label, Font font, Padding margin)
    {
        label.Text = "";
        label.Font = font;
        label.ForeColor = Palette.Muted;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Right;
        label.Margin = margin;
    }

    /// <summary>A 1px hairline. Dock=Fill stretches it across its cell; Height stays 1.</summary>
    private static Panel MakeSeparator() => new Panel
    {
        Dock = DockStyle.Fill,
        Height = 1,
        BackColor = Palette.Border,
        Margin = new Padding(0, 6, 0, 6),
    };
}
