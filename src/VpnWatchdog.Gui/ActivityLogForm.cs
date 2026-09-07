using System.Globalization;
using System.Text;
using VpnWatchdog.Core;

namespace VpnWatchdog.Gui;

/// <summary>
/// The "VPN Connection Logs" dialog: a compact, read-only window onto the shared
/// <see cref="IVpnActivityLog"/> trail - what the watchdog observed and what it did,
/// newest first.
/// <para>
/// Read-only towards the VPN by construction: the only thing this form is handed is
/// the activity log, so nothing in here can reach FortiClient, the reconnect policy
/// or a manual control. The one destructive action (Clear Logs) sits behind a
/// confirmation that defaults to No, and only ever deletes history.
/// </para>
/// <para>
/// Rows are owner-drawn so each event reads as ONE line of story - a coloured status
/// glyph, the message, its detail - rather than four grey grid cells. Colours come
/// from <see cref="VpnActivityKindExtensions.Severity"/> so this window and the main
/// form can never disagree about what counts as a warning.
/// </para>
/// </summary>
public sealed class ActivityLogForm : Form
{
    /// <summary>
    /// How much history one open of the window asks for. Plenty to scroll through,
    /// small enough that the read and the repopulate are both instant.
    /// </summary>
    private const int MaxEntries = 500;

    // Local wall-clock, unambiguous and sortable. The list shows the short form; the
    // clipboard and the export add the UTC offset so a line pasted into a ticket by
    // someone in another time zone still means one instant.
    private const string DisplayTimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const string ExportTimestampFormat = "yyyy-MM-dd HH:mm:ss zzz";

    // Windows 11 palette. The status glyph uses ONLY the four severity colours - the
    // mapping from kind to severity lives in Core, not here.
    private static readonly Color Green = ColorTranslator.FromHtml("#107C10");
    private static readonly Color Red = ColorTranslator.FromHtml("#C42B1C");
    private static readonly Color Amber = ColorTranslator.FromHtml("#C19C00");
    private static readonly Color Blue = ColorTranslator.FromHtml("#0067C0");
    private static readonly Color Grey = ColorTranslator.FromHtml("#6E6E6E");

    private static readonly Color Surface = ColorTranslator.FromHtml("#F9F9F9");
    private static readonly Color Border = ColorTranslator.FromHtml("#E5E5E5");
    private static readonly Color TextPrimary = ColorTranslator.FromHtml("#1A1A1A");
    private static readonly Color SelectionBack = ColorTranslator.FromHtml("#EDEDED");
    private static readonly Color ButtonBorder = ColorTranslator.FromHtml("#D6D6D6");
    private static readonly Color ButtonHover = ColorTranslator.FromHtml("#F5F5F5");
    private static readonly Color ButtonPressed = ColorTranslator.FromHtml("#EBEBEB");

    private const TextFormatFlags LineFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private const TextFormatFlags GlyphFlags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly IVpnActivityLog _log;
    private readonly string _profileName;

    // Everything read from the log, newest first, and the subset the filter lets
    // through. Copy and Export work from _visible, so what leaves the window is
    // exactly what is on screen.
    private IReadOnlyList<VpnActivityEntry> _all = Array.Empty<VpnActivityEntry>();
    private List<VpnActivityEntry> _visible = new();

    // Bumped on every reload so a slow read that lands after a newer one (or after a
    // Clear) cannot repopulate the list with rows that no longer exist.
    private int _loadGeneration;
    private bool _loaded;
    private bool _listFocusedOnce;
    private bool _clearInFlight;

    private readonly System.ComponentModel.Container _components = new();
    private readonly Font _bodyFont;
    private readonly Font _primaryFont;
    private readonly Font _glyphFont;
    private readonly bool _iconFontAvailable;

    private readonly SolidBrush _rowBrush = new(Color.White);
    private readonly SolidBrush _selectionBrush = new(SelectionBack);
    private readonly SolidBrush _accentBrush = new(Blue);

    // ListView has no row-height property; an empty image list of the wanted height
    // is the standard way to set one. Sized in DEVICE pixels by ApplyRowMetrics,
    // because image lists are not touched by the form's auto-scaling.
    private readonly ImageList _rowHeightList;
    private int _primaryLineHeight;
    private int _secondaryLineHeight;

    private readonly Label _lblCaption;
    private readonly ComboBox _cmbFilter;
    private readonly Panel _listHost;
    private readonly BufferedListView _list;
    private readonly ColumnHeader _colEvent;
    private readonly Label _lblEmpty;
    private readonly RoundedButton _btnClear;
    private readonly RoundedButton _btnRefresh;
    private readonly RoundedButton _btnCopy;
    private readonly RoundedButton _btnExport;
    private readonly RoundedButton _btnClose;
    private readonly ToolTip _toolTip;

    public ActivityLogForm(IVpnActivityLog log, string profileName)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _profileName = profileName ?? string.Empty;

        _bodyFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _primaryFont = CreatePrimaryFont();
        _glyphFont = CreateGlyphFont(out _iconFontAvailable);
        _rowHeightList = new ImageList(_components) { ColorDepth = ColorDepth.Depth32Bit };
        _toolTip = new ToolTip(_components);

        SuspendLayout();

        // ---- Form ----------------------------------------------------------
        // Dpi scaling with 96 as the design baseline: every size below is in
        // logical pixels and WinForms multiplies them once at handle creation.
        // Fonts are in points and need no help.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = _bodyFont;
        Text = "VPN Connection Logs";
        BackColor = Surface;
        ClientSize = new Size(480, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = true;
        KeyPreview = true;

        // ---- Header: caption left, filter right ----------------------------
        _lblCaption = new Label
        {
            Text = "Recent VPN events (newest first)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            ForeColor = TextPrimary,
        };

        _cmbFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0),
        };
        _cmbFilter.Items.AddRange(FilterOptions);
        _cmbFilter.SelectedIndex = 0;
        _cmbFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _toolTip.SetToolTip(_cmbFilter, "Narrows the list. Copy and Export follow the filter.");

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(_lblCaption, 0, 0);
        header.Controls.Add(_cmbFilter, 1, 0);

        // ---- List ----------------------------------------------------------
        // One column that always spans the client width: the row is painted as a
        // whole in List_DrawItem, so column boundaries would only get in the way.
        _colEvent = new ColumnHeader { Text = string.Empty, Width = 400 };

        _list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = false,
            OwnerDraw = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = TextPrimary,
            UseCompatibleStateImageBehavior = false,
            ShowItemToolTips = false,
            SmallImageList = _rowHeightList,
            Visible = false, // neither list nor empty label until the first read lands
        };
        _list.Columns.Add(_colEvent);
        _list.DrawItem += List_DrawItem;
        // The whole row is painted in DrawItem; leaving DrawDefault false here is
        // what stops the ListView painting its own text over the top.
        _list.DrawSubItem += static (_, _) => { };
        _list.ClientSizeChanged += (_, _) => FitColumn();

        _lblEmpty = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No activity recorded yet",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Grey,
            BackColor = Color.White,
            Visible = false,
        };

        // A 1px panel border rather than BorderStyle.FixedSingle, which draws in the
        // dark WindowFrame colour and looks like a Windows 95 control on a light surface.
        _listHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            BackColor = Border,
            Margin = Padding.Empty,
        };
        _listHost.Controls.Add(_list);
        _listHost.Controls.Add(_lblEmpty);

        // ---- Footer: destructive action far left, everything else right -----
        _btnClear = CreateButton("Clear Logs...");
        _btnClear.Margin = new Padding(0, 0, 6, 0);
        _btnClear.Click += BtnClear_Click;
        _toolTip.SetToolTip(_btnClear, "Deletes every recorded event, after asking you to confirm.");

        _btnRefresh = CreateButton("Refresh");
        _btnRefresh.Margin = Padding.Empty;
        _btnRefresh.Click += (_, _) => BeginReload();
        _toolTip.SetToolTip(_btnRefresh, "Reloads the most recent events (F5).");

        _btnCopy = CreateButton("Copy");
        _btnCopy.Margin = new Padding(0, 0, 6, 0);
        _btnCopy.Click += (_, _) => CopyRows();
        _toolTip.SetToolTip(_btnCopy, "Copies the selected rows - or every row shown if none are selected - as text (Ctrl+C).");

        _btnExport = CreateButton("Export...");
        _btnExport.Margin = new Padding(0, 0, 6, 0);
        _btnExport.Click += BtnExport_Click;
        _toolTip.SetToolTip(_btnExport, "Saves the rows currently shown to a .txt or .csv file.");

        _btnClose = CreateButton("Close");
        _btnClose.Margin = Padding.Empty;
        _btnClose.Click += (_, _) => Close();

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6,
            RowCount = 1,
            Margin = new Padding(0, 10, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(_btnClear, 0, 0);
        footer.Controls.Add(_btnRefresh, 1, 0);
        footer.Controls.Add(_btnCopy, 3, 0);
        footer.Controls.Add(_btnExport, 4, 0);
        footer.Controls.Add(_btnClose, 5, 0);

        // ---- Root ----------------------------------------------------------
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12, 10, 12, 12),
            BackColor = Surface,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_listHost, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        // Escape closes. Deliberately no AcceptButton: Enter while browsing a log
        // should not make the window vanish.
        CancelButton = _btnClose;

        ResumeLayout(false);
        PerformLayout();
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // DeviceDpi is final once the handle exists, so this is the first moment
        // the device-pixel row metrics can be trusted.
        ApplyRowMetrics();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Set here, after auto-scaling has run, in device units: a fixed minimum in
        // logical pixels would let a 150% user shrink the window until the buttons
        // overlapped.
        MinimumSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(300));

        BeginReload();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // Not reachable under SystemAware, but if the app is ever moved to
        // per-monitor DPI the row height must follow the fonts or text clips.
        ApplyRowMetrics();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            BeginReload();
            return;
        }

        // Form-level (KeyPreview) rather than on the ListView alone: the filter is a
        // DropDownList and the buttons have nothing to copy, so Ctrl+C anywhere in
        // this window can only mean "copy the log rows".
        if (e.Control && e.KeyCode == Keys.C)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopyRows();
            return;
        }

        // WinForms' ListView has no built-in select-all.
        if (e.Control && e.KeyCode == Keys.A && _list.Focused)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SelectAllRows();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        // Children first (base), then the fonts they were still painting with.
        base.Dispose(disposing);

        if (disposing)
        {
            _components.Dispose();
            _rowBrush.Dispose();
            _selectionBrush.Dispose();
            _accentBrush.Dispose();
            _primaryFont.Dispose();
            _glyphFont.Dispose();
            _bodyFont.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Loading and filtering
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-reads the newest <see cref="MaxEntries"/> rows and repopulates the list.
    /// Fire-and-forget and never throws: a failure to read is shown as the empty
    /// state, never as a crash out of an async void handler.
    /// </summary>
    private async void BeginReload()
    {
        int generation = ++_loadGeneration;
        IVpnActivityLog log = _log;

        IReadOnlyList<VpnActivityEntry> entries;
        try
        {
            // Off the UI thread: GetRecent is normally milliseconds, but the SQLite
            // store may legitimately wait a few seconds for a lock held by the CLI
            // process, and the window must not freeze for that.
            entries = await Task.Run(() => log.GetRecent(MaxEntries));
        }
        catch
        {
            // Implementations promise not to throw; if one does anyway the window
            // degrades to its empty state instead of taking the GUI down.
            entries = Array.Empty<VpnActivityEntry>();
        }

        try
        {
            if (IsDisposed || generation != _loadGeneration) return;

            _all = entries ?? Array.Empty<VpnActivityEntry>();
            _loaded = true;
            ApplyFilter();
        }
        catch
        {
            // Anything escaping an async void continuation is an unobserved
            // exception on the UI thread - exactly the crash invariant 6 forbids.
        }
    }

    /// <summary>
    /// Applies the current filter to the cached rows. Pure UI work - no I/O - so
    /// switching filters is instant and never touches the store.
    /// </summary>
    private void ApplyFilter()
    {
        if (!_loaded) return;

        EventFilter filter = (_cmbFilter.SelectedItem as FilterOption)?.Filter ?? EventFilter.All;

        // _all is already newest-first; Where preserves that order.
        _visible = _all.Where(entry => Matches(entry, filter)).ToList();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            var items = new ListViewItem[_visible.Count];
            for (int i = 0; i < items.Length; i++)
            {
                // Text is set for accessibility tools only; the row itself is
                // painted from the Tag in List_DrawItem.
                items[i] = new ListViewItem(_visible[i].Message) { Tag = _visible[i] };
            }
            _list.Items.AddRange(items);
        }
        finally
        {
            _list.EndUpdate();
        }

        bool anyAtAll = _all.Count > 0;
        bool anyVisible = _visible.Count > 0;

        // Two different truths deserve two different sentences: a blank list because
        // nothing ever happened, and a blank list because the filter hid it all.
        _lblEmpty.Text = anyAtAll ? "No events match this filter" : "No activity recorded yet";
        _lblEmpty.Visible = !anyVisible;
        _list.Visible = anyVisible;

        _btnCopy.Enabled = anyVisible;
        _btnExport.Enabled = anyVisible;
        _btnClear.Enabled = anyAtAll && !_clearInFlight;

        FitColumn();

        // The list is hidden until the first read lands, so WinForms' initial focus
        // went to the filter. Hand it to the rows once, so arrow keys and Ctrl+A work
        // straight away - but never again, or a Refresh would steal focus.
        if (anyVisible && !_listFocusedOnce)
        {
            _listFocusedOnce = true;
            _list.Focus();
        }
    }

    private static bool Matches(VpnActivityEntry entry, EventFilter filter) => filter switch
    {
        // The tunnel itself: state changes plus the user's own connect/disconnect
        // actions. Monitoring, internet and FortiClient-process chatter stay out.
        EventFilter.VpnOnly => entry.Kind is
            VpnActivityKind.VpnConnected or
            VpnActivityKind.VpnDisconnected or
            VpnActivityKind.VpnDisconnectedUnexpectedly or
            VpnActivityKind.ManualConnectRequested or
            VpnActivityKind.ManualConnectSucceeded or
            VpnActivityKind.ManualConnectFailed or
            VpnActivityKind.ManualDisconnectRequested or
            VpnActivityKind.ManualDisconnectSucceeded or
            VpnActivityKind.ManualDisconnectFailed,

        EventFilter.Reconnects => entry.Kind is
            VpnActivityKind.AutoReconnectArmed or
            VpnActivityKind.AutoReconnectDisarmed or
            VpnActivityKind.AutoReconnectTriggered or
            VpnActivityKind.AutoReconnectSucceeded or
            VpnActivityKind.AutoReconnectFailed or
            VpnActivityKind.AutoReconnectGaveUp,

        EventFilter.WarningsAndErrors => entry.Kind.Severity() is
            VpnActivitySeverity.Warning or
            VpnActivitySeverity.Error,

        _ => true,
    };

    // ------------------------------------------------------------------
    // Owner drawing
    // ------------------------------------------------------------------

    /// <summary>
    /// Sizes the rows from the REAL font heights at the CURRENT DPI, so text can
    /// never clip at 125%/150% or under a font substitution. 40 logical px is the
    /// floor, not the rule.
    /// </summary>
    private void ApplyRowMetrics()
    {
        var unbounded = new Size(int.MaxValue, int.MaxValue);
        _primaryLineHeight = TextRenderer.MeasureText("Xg", _primaryFont, unbounded, LineFlags).Height;
        _secondaryLineHeight = TextRenderer.MeasureText("Xg", _bodyFont, unbounded, LineFlags).Height;

        int textBlock = _primaryLineHeight + LogicalToDeviceUnits(2) + _secondaryLineHeight;
        int rowHeight = Math.Max(LogicalToDeviceUnits(40), textBlock + LogicalToDeviceUnits(10));

        // ImageList caps image height at 256 - far above any sane DPI, but a hard
        // limit that throws if crossed.
        _rowHeightList.ImageSize = new Size(1, Math.Clamp(rowHeight, 1, 256));

        // Re-assigning is what makes a ListView that already has items re-measure them.
        _list.SmallImageList = null;
        _list.SmallImageList = _rowHeightList;
        _list.Invalidate();
    }

    /// <summary>Keeps the single column exactly as wide as the list, so there is never a horizontal scrollbar.</summary>
    private void FitColumn()
    {
        int width = _list.ClientSize.Width;
        if (width > 0 && _colEvent.Width != width)
        {
            _colEvent.Width = width;
        }

        // A resize does not repaint rows the ListView thinks are unchanged, but the
        // right-aligned timestamps have moved.
        _list.Invalidate();
    }

    private void List_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item.Tag is not VpnActivityEntry entry) return;

        Graphics g = e.Graphics;
        Rectangle row = e.Bounds;

        // Selection is a quiet tint plus an accent bar rather than the system
        // highlight, so the two-tone text stays readable in the same colours.
        bool selected = e.Item.Selected;
        g.FillRectangle(selected ? _selectionBrush : _rowBrush, row);
        if (selected)
        {
            int inset = LogicalToDeviceUnits(8);
            g.FillRectangle(_accentBrush, new Rectangle(
                row.Left, row.Top + inset, LogicalToDeviceUnits(3), Math.Max(1, row.Height - inset * 2)));
        }

        int pad = LogicalToDeviceUnits(8);

        // Status glyph: shape from the kind, colour from Core's severity mapping.
        var glyphRect = new Rectangle(row.Left + pad, row.Top, LogicalToDeviceUnits(20), row.Height);
        TextRenderer.DrawText(g, GlyphFor(entry.Kind), _glyphFont, glyphRect,
            SeverityColor(entry.Kind.Severity()), GlyphFlags);

        int textLeft = glyphRect.Right + LogicalToDeviceUnits(8);
        int textRight = row.Right - pad;
        int gap = LogicalToDeviceUnits(2);
        int blockTop = row.Top + Math.Max(0, (row.Height - (_primaryLineHeight + gap + _secondaryLineHeight)) / 2);

        // Line 1, right: timestamp in local time, secondary colour.
        string time = entry.Timestamp.ToLocalTime().ToString(DisplayTimestampFormat, CultureInfo.InvariantCulture);
        int timeWidth = TextRenderer.MeasureText(g, time, _bodyFont, new Size(int.MaxValue, int.MaxValue), LineFlags).Width;
        var timeRect = new Rectangle(textRight - timeWidth, blockTop, timeWidth, _primaryLineHeight);
        TextRenderer.DrawText(g, time, _bodyFont, timeRect, Grey, LineFlags);

        // Line 1, left: the message, semibold, clipped with an ellipsis before it
        // can run into the timestamp.
        int messageWidth = Math.Max(0, timeRect.Left - LogicalToDeviceUnits(12) - textLeft);
        var messageRect = new Rectangle(textLeft, blockTop, messageWidth, _primaryLineHeight);
        TextRenderer.DrawText(g, entry.Message, _primaryFont, messageRect, TextPrimary, LineFlags | TextFormatFlags.EndEllipsis);

        // Line 2: detail (or the profile name when there is none), secondary colour.
        string secondary = SecondaryTextFor(entry);
        if (secondary.Length > 0)
        {
            var detailRect = new Rectangle(textLeft, blockTop + _primaryLineHeight + gap,
                Math.Max(0, textRight - textLeft), _secondaryLineHeight);
            TextRenderer.DrawText(g, secondary, _bodyFont, detailRect, Grey, LineFlags | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// The grey second line. Detail when the entry has one; otherwise the profile it
    /// concerns, so "VPN connected" still says which tunnel. An entry for a DIFFERENT
    /// profile than this window's (the CLI writes to the same trail) is tagged with
    /// its profile even when it has a detail, because that is the one case where the
    /// reader would otherwise be misled.
    /// </summary>
    private string SecondaryTextFor(VpnActivityEntry entry)
    {
        string detail = entry.Detail?.Trim() ?? string.Empty;
        string profile = entry.ProfileName?.Trim() ?? string.Empty;

        if (detail.Length == 0) return profile;

        bool otherProfile = profile.Length > 0
            && !string.Equals(profile, _profileName, StringComparison.OrdinalIgnoreCase);
        return otherProfile ? $"{detail}  ·  {profile}" : detail;
    }

    /// <summary>
    /// Glyph shape. A circle for everything except a warning (triangle) and a
    /// reconnect being triggered (arrows). Icon-font code points are identical in
    /// Segoe Fluent Icons and Segoe MDL2 Assets; the plain characters are the
    /// fallback for a machine with neither.
    /// </summary>
    private string GlyphFor(VpnActivityKind kind)
    {
        if (kind == VpnActivityKind.AutoReconnectTriggered)
        {
            return _iconFontAvailable ? "\uE72C" : "↻"; // Refresh / ↻
        }

        return kind.Severity() switch
        {
            VpnActivitySeverity.Warning => _iconFontAvailable ? "\uE814" : "▲", // IncidentTriangle / ▲
            _ => _iconFontAvailable ? "\uEA3B" : "●",                            // CircleFill / ●
        };
    }

    /// <summary>The ONLY colour decision in this file: severity in, semantic colour out.</summary>
    private static Color SeverityColor(VpnActivitySeverity severity) => severity switch
    {
        VpnActivitySeverity.Success => Green,
        VpnActivitySeverity.Warning => Amber,
        VpnActivitySeverity.Error => Red,
        _ => Blue,
    };

    // ------------------------------------------------------------------
    // Actions: Clear, Copy, Export
    // ------------------------------------------------------------------

    private async void BtnClear_Click(object? sender, EventArgs e)
    {
        if (_clearInFlight) return;

        // Destructive and irreversible, so: confirm first, defaulting to Cancel.
        bool confirmed = ConfirmDialog.Show(
            this,
            title: "Clear Logs",
            heading: "Delete all recorded VPN events?",
            message: "This cannot be undone.",
            confirmText: "Clear Logs",
            cancelText: "Cancel");

        if (!confirmed) return;

        _clearInFlight = true;
        _btnClear.Enabled = false;

        bool cleared;
        try
        {
            IVpnActivityLog log = _log;
            // Same reasoning as the read: the store may wait on the other process.
            cleared = await Task.Run(() => log.Clear());
        }
        catch
        {
            cleared = false;
        }

        try
        {
            if (IsDisposed) return;

            _clearInFlight = false;

            if (!cleared)
            {
                MessageBox.Show(this, "Could not clear the log.", "Clear Logs",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Reload either way: on success it shows the empty state, on failure it
            // shows whatever is actually still there rather than what we assumed.
            BeginReload();
        }
        catch
        {
            // async void continuation - see BeginReload.
        }
    }

    /// <summary>Selected rows, or every visible row when nothing is selected, as text lines - for pasting into a ticket.</summary>
    private void CopyRows()
    {
        List<VpnActivityEntry> rows = RowsForCopy();
        if (rows.Count == 0) return;

        try
        {
            Clipboard.SetText(BuildText(rows));
        }
        catch
        {
            // Another application holding the clipboard open is the classic cause.
            // Nothing sensible to do but leave the clipboard as it was.
        }
    }

    private List<VpnActivityEntry> RowsForCopy()
    {
        if (_list.SelectedItems.Count == 0)
        {
            return new List<VpnActivityEntry>(_visible);
        }

        // SelectedItems comes back in index order, which is newest first.
        var rows = new List<VpnActivityEntry>(_list.SelectedItems.Count);
        foreach (ListViewItem item in _list.SelectedItems)
        {
            if (item.Tag is VpnActivityEntry entry) rows.Add(entry);
        }
        return rows;
    }

    private void SelectAllRows()
    {
        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _list.Items) item.Selected = true;
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private async void BtnExport_Click(object? sender, EventArgs e)
    {
        if (_visible.Count == 0) return;

        string path;
        bool csv;
        using (var dialog = new SaveFileDialog
        {
            Title = "Export VPN events",
            Filter = "Text file (*.txt)|*.txt|CSV file (*.csv)|*.csv",
            FilterIndex = 1,
            DefaultExt = "txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"vpn-events-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            path = dialog.FileName;

            // The extension the user actually typed wins; the filter only decides
            // when they typed none the dialog recognised.
            string ext = Path.GetExtension(path);
            csv = ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || (dialog.FilterIndex == 2 && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase));
        }

        // Snapshot now: a Refresh while the write is in flight must not change what
        // the user asked to save.
        VpnActivityEntry[] rows = _visible.ToArray();
        string content = csv ? BuildCsv(rows) : BuildText(rows);

        Exception? failure = null;
        try
        {
            // 500 lines is nothing, but the destination may be a network share.
            await Task.Run(() => File.WriteAllText(path, content, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            if (IsDisposed || failure is null) return;

            MessageBox.Show(this,
                $"Could not write the file.\r\n\r\n{failure.Message}",
                "Export failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
            // async void continuation - see BeginReload.
        }
    }

    // ------------------------------------------------------------------
    // Text formats. One line per event, newest first, the same in the clipboard
    // and the .txt export so a pasted line and a saved line are interchangeable.
    // ------------------------------------------------------------------

    private static string FormatTextLine(VpnActivityEntry entry)
    {
        string time = entry.Timestamp.ToLocalTime().ToString(ExportTimestampFormat, CultureInfo.InvariantCulture);
        return $"{time}  {entry.Kind}  {Flatten(entry.Message)}  {Flatten(entry.Detail)}".TrimEnd();
    }

    private static string BuildText(IEnumerable<VpnActivityEntry> rows)
    {
        var sb = new StringBuilder();
        foreach (VpnActivityEntry row in rows)
        {
            sb.AppendLine(FormatTextLine(row));
        }
        return sb.ToString();
    }

    private static string BuildCsv(IEnumerable<VpnActivityEntry> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Kind,Message,Detail");
        foreach (VpnActivityEntry row in rows)
        {
            string time = row.Timestamp.ToLocalTime().ToString(ExportTimestampFormat, CultureInfo.InvariantCulture);
            sb.Append(CsvField(time)).Append(',')
              .Append(CsvField(row.Kind.ToString())).Append(',')
              .Append(CsvField(row.Message)).Append(',')
              .Append(CsvField(row.Detail))
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Always quoted, quotes doubled - the one CSV form every consumer agrees on.</summary>
    private static string CsvField(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    /// <summary>Keeps "one event per line" true even if a message ever carries a line break.</summary>
    private static string Flatten(string? value) =>
        (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");

    // ------------------------------------------------------------------
    // Construction helpers
    // ------------------------------------------------------------------

    private static RoundedButton CreateButton(string text)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 2, 10, 2),
            MinimumSize = new Size(0, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextPrimary,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = ButtonBorder;
        button.FlatAppearance.MouseOverBackColor = ButtonHover;
        button.FlatAppearance.MouseDownBackColor = ButtonPressed;
        return button;
    }

    /// <summary>
    /// Semibold is the Windows 11 weight for a row's primary text. GDI+ silently
    /// substitutes a generic sans-serif for an unknown family, so the result is
    /// checked and Bold - which Segoe UI always has - used instead.
    /// </summary>
    private static Font CreatePrimaryFont()
    {
        const string family = "Segoe UI Semibold";
        var semibold = new Font(family, 9F, FontStyle.Regular, GraphicsUnit.Point);
        if (string.Equals(semibold.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
        {
            return semibold;
        }

        semibold.Dispose();
        return new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
    }

    /// <summary>
    /// Segoe Fluent Icons (Windows 11), else Segoe MDL2 Assets (Windows 10), else
    /// plain Segoe UI with text characters. Same substitution check as above.
    /// </summary>
    private static Font CreateGlyphFont(out bool iconFont)
    {
        foreach (string family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var candidate = new Font(family, 9F, FontStyle.Regular, GraphicsUnit.Point);
            if (string.Equals(candidate.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                iconFont = true;
                return candidate;
            }
            candidate.Dispose();
        }

        iconFont = false;
        return new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    }

    // ------------------------------------------------------------------
    // Nested types
    // ------------------------------------------------------------------

    private enum EventFilter { All, VpnOnly, Reconnects, WarningsAndErrors }

    /// <summary>Combo item. ToString is what a ComboBox displays.</summary>
    private sealed record FilterOption(string Label, EventFilter Filter)
    {
        public override string ToString() => Label;
    }

    private static readonly object[] FilterOptions =
    {
        new FilterOption("All events", EventFilter.All),
        new FilterOption("VPN only", EventFilter.VpnOnly),
        new FilterOption("Reconnects", EventFilter.Reconnects),
        new FilterOption("Warnings & errors", EventFilter.WarningsAndErrors),
    };

    /// <summary>
    /// An owner-drawn ListView flickers badly on resize and scroll without the
    /// LVS_EX_DOUBLEBUFFER style, which is what the protected DoubleBuffered
    /// property maps to on this control.
    /// </summary>
    private sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
        }
    }
}
