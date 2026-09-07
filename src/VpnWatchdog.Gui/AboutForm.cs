using System.Drawing.Drawing2D;

namespace VpnWatchdog.Gui;

/// <summary>
/// Compact "About" dialog: brand, version, and a short, honest statement of what
/// this app does and does not do. Read-only - no settings live here.
/// <para>
/// The banner prefers an embedded WORDMARK image (logo + "VPN Watchdog" text
/// together) if one has been embedded as <c>AppWordmark.png</c> - see
/// <see cref="LoadEmbeddedWordmark"/>. Until that asset exists, it falls back to
/// the plain icon-only app icon plus a text app name, composed at paint time, so
/// this dialog never ships broken or blank while the real wordmark is pending.
/// Dropping the file in later is a one-line .csproj change, not a code change.
/// </para>
/// </summary>
public sealed class AboutForm : Form
{
    // Same Windows 11 palette as SettingsForm - kept as a local literal copy
    // rather than a shared type, matching how every dialog in this app is styled.
    private static readonly Color Accent = Color.FromArgb(0x00, 0x67, 0xC0);
    private static readonly Color AccentHover = Color.FromArgb(0x19, 0x75, 0xC5);
    private static readonly Color AccentPressed = Color.FromArgb(0x31, 0x83, 0xCA);
    private static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
    private static readonly Color Hairline = Color.FromArgb(0xE5, 0xE5, 0xE5);
    private static readonly Color MutedText = Color.FromArgb(0x6E, 0x6E, 0x6E);
    private static readonly Color TextColor = Color.FromArgb(0x1A, 0x1A, 0x1A);

    private readonly System.ComponentModel.IContainer components = new System.ComponentModel.Container();
    private readonly Font bodyFont = new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font nameFont = new("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font smallFont = new("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);

    private readonly Image? _wordmark;
    private readonly Icon? _icon;
    private readonly Panel banner;
    private readonly Button btnClose = new();

    public AboutForm()
    {
        _wordmark = LoadEmbeddedWordmark();
        _icon = LoadEmbeddedIconForBanner();

        banner = new Panel
        {
            Height = 96,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 10),
        };
        banner.Paint += Banner_Paint;

        InitializeComponent();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components.Dispose();
            _wordmark?.Dispose();
            _icon?.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            bodyFont.Dispose();
            nameFont.Dispose();
            smallFont.Dispose();
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "About VPN Watchdog";
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
            ColumnCount = 1,
            Margin = Padding.Empty,
        };
        // Absolute, not Percent: a Percent column's width is derived from the
        // container's own AutoSize width, which is itself derived from its
        // children's preferred sizes - a circular dependency that WinForms can
        // resolve too early for a wrapping Label, leaving MaximumSize-based
        // wrapping measured against a stale (too-narrow-then-clipped) width on
        // first layout. An absolute width breaks the cycle deterministically.
        const int ContentWidth = 320;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));

        var version = new Label
        {
            Text = VersionText(),
            Font = smallFont,
            ForeColor = MutedText,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 10),
        };

        // Only shown for the icon+text fallback banner: the real wordmark already
        // bakes "Monitor · Recover · Stay Connected" into the artwork itself, so
        // drawing it again here would repeat the same line twice.
        var tagline = new Label
        {
            Text = "Monitor · Recover · Stay Connected",
            Font = bodyFont,
            ForeColor = MutedText,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 14),
            Visible = _wordmark is null,
        };

        var sep1 = new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Hairline, Margin = new Padding(0, 0, 0, 12) };

        var summary = new Label
        {
            Text =
                "Watches your FortiClient VPN tunnel and, only when you explicitly turn " +
                "it on, asks FortiClient to reconnect after an outage. It never disconnects " +
                "the tunnel on its own, and it never sees or stores your VPN credentials - " +
                "FortiClient's own saved sign-in handles that.",
            Font = bodyFont,
            ForeColor = TextColor,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 14),
        };

        var sep2 = new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Hairline, Margin = new Padding(0, 0, 0, 12) };

        var profile = new Label
        {
            Text = "Built on .NET 8 · FortiClient COM automation",
            Font = smallFont,
            ForeColor = MutedText,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 4),
        };

        var credit = new Label
        {
            Text = "Developed by Abhijith for the Nix family",
            Font = smallFont,
            ForeColor = MutedText,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 16),
        };

        // No visible Close button: FixedDialog already gives this window a
        // titlebar X, and a second "Close" control right below it is redundant.
        // btnClose itself stays - invisible and outside the layout flow - purely
        // so AcceptButton/CancelButton have a real IButtonControl to route
        // Enter/Escape to; that's the only reason it still exists.
        btnClose.Click += (_, _) => Close();
        btnClose.Visible = false;
        btnClose.TabStop = false;
        btnClose.Size = Size.Empty;
        Controls.Add(btnClose);

        root.Controls.Add(banner);
        root.Controls.Add(Center(version));
        // Not just Visible=false: skipping the Add entirely guarantees zero
        // layout footprint (no reserved margin/row) when the wordmark already
        // shows this line, rather than trusting an invisible child to collapse.
        if (_wordmark is null)
        {
            root.Controls.Add(Center(tagline));
        }
        root.Controls.Add(sep1);
        root.Controls.Add(Center(summary));
        root.Controls.Add(sep2);
        root.Controls.Add(Center(profile));
        root.Controls.Add(Center(credit));

        Controls.Add(root);
        AcceptButton = btnClose;
        CancelButton = btnClose;

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>
    /// Wraps a control in a full-width host so <see cref="AnchorStyles.None"/>
    /// centres it - a TableLayoutPanel cell alone won't centre an AutoSize child
    /// without a sized host to centre it within.
    /// </summary>
    private static Panel Center(Control control)
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        control.Dock = DockStyle.Top;
        host.Controls.Add(control);
        return host;
    }

    /// <summary>
    /// Draws the wordmark image if one is embedded, letterboxed to fit the banner
    /// without distortion; otherwise composes the plain icon plus the app name as
    /// a readable fallback, so the dialog is never blank.
    /// </summary>
    private void Banner_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        Rectangle bounds = banner.ClientRectangle;

        if (_wordmark is not null)
        {
            float scale = Math.Min((float)bounds.Width / _wordmark.Width, (float)bounds.Height / _wordmark.Height);
            int w = (int)(_wordmark.Width * scale);
            int h = (int)(_wordmark.Height * scale);
            int x = bounds.Left + (bounds.Width - w) / 2;
            int y = bounds.Top + (bounds.Height - h) / 2;
            e.Graphics.DrawImage(_wordmark, x, y, w, h);
            return;
        }

        const int iconSize = 48;
        string name = "VPN Watchdog";
        SizeF nameSize = e.Graphics.MeasureString(name, nameFont);
        int gap = 12;
        int totalWidth = iconSize + gap + (int)nameSize.Width;
        int startX = bounds.Left + (bounds.Width - totalWidth) / 2;
        int centerY = bounds.Top + bounds.Height / 2;

        if (_icon is not null)
        {
            e.Graphics.DrawIcon(_icon, new Rectangle(startX, centerY - iconSize / 2, iconSize, iconSize));
        }

        using var brush = new SolidBrush(TextColor);
        e.Graphics.DrawString(
            name, nameFont, brush,
            startX + iconSize + gap, centerY - nameSize.Height / 2);
    }

    /// <summary>
    /// The eventual text+name wordmark, once embedded as <c>Assets\wordmark.png</c>
    /// with LogicalName <c>AppWordmark.png</c> in the .csproj. Returns null (never
    /// throws) until that resource exists, which is the ONLY thing that needs to
    /// change to switch this dialog over - no code here needs to change.
    /// </summary>
    private static Image? LoadEmbeddedWordmark()
    {
        try
        {
            using Stream? stream = typeof(AboutForm).Assembly.GetManifestResourceStream("AppWordmark.png");
            return stream is null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Icon? LoadEmbeddedIconForBanner()
    {
        try
        {
            using Stream? stream = typeof(AboutForm).Assembly.GetManifestResourceStream("AppIcon.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string VersionText()
    {
        Version? version = typeof(AboutForm).Assembly.GetName().Version;
        return version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    /// <summary>Flat Windows 11 accent button - matches SettingsForm's OK styling.</summary>
    private static void StyleButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(84, 27);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Margin = Padding.Empty;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
    }
}
