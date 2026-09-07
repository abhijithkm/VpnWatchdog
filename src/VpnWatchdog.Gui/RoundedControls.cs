using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace VpnWatchdog.Gui;

/// <summary>
/// The rounded-rectangle path shared by every custom-painted control in this
/// app (the hero card, every button) - one geometry helper so they all round
/// by exactly the same amount rather than each control picking its own.
/// </summary>
internal static class RoundedGeometry
{
    /// <summary>
    /// A logical radius, scaled to the current DPI by the caller via
    /// <see cref="Control.LogicalToDeviceUnits(int)"/> before it reaches here -
    /// this class only builds the path, it does not know about DPI.
    /// </summary>
    public static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The color to paint OUTSIDE the rounded rectangle (the four corners a
    /// square client area still has that the rounded path does not cover) so
    /// they show the real surrounding surface instead of a hard square edge.
    /// Walks up from <paramref name="start"/> rather than trusting its
    /// immediate parent's BackColor directly: several containers in this app
    /// (TableLayoutPanel among them) default to <see cref="Color.Transparent"/>
    /// (alpha 0) when no one has explicitly styled them, and filling a GDI+
    /// surface with a zero-alpha brush paints solid BLACK, not "nothing" -
    /// confirmed live (rounded buttons parented under an unstyled
    /// TableLayoutPanel rendered with black corners until this walked past it).
    /// The walk stops at the first ancestor with a fully opaque BackColor, or
    /// falls back to <paramref name="fallback"/> if none is found.
    /// </summary>
    public static Color ResolveOpaqueAncestorBackColor(Control? start, Color fallback)
    {
        for (Control? c = start; c is not null; c = c.Parent)
        {
            if (c.BackColor.A == 255) return c.BackColor;
        }
        return fallback;
    }
}

/// <summary>
/// A <see cref="TableLayoutPanel"/> with smooth, anti-aliased rounded corners -
/// used for the hero/status card. <see cref="OnPaintBackground"/> paints the
/// PARENT's background first so the corners the rounded rectangle does not
/// cover show through as the surrounding surface, not a hard square edge.
/// </summary>
internal sealed class RoundedTablePanel : TableLayoutPanel
{
    private Color _edge = MainForm.Palette.GreyEdge;

    public RoundedTablePanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = MainForm.Palette.GreyTint;
    }

    public void SetTint(Color tint, Color edge)
    {
        if (BackColor == tint && _edge == edge) return;
        _edge = edge;
        BackColor = tint;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Color surfaceColor = RoundedGeometry.ResolveOpaqueAncestorBackColor(Parent, MainForm.Palette.Surface);
        using (var surface = new SolidBrush(surfaceColor))
        {
            e.Graphics.FillRectangle(surface, ClientRectangle);
        }

        Rectangle rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        int radius = Math.Max(1, LogicalToDeviceUnits(6));
        using GraphicsPath path = RoundedGeometry.RoundedRectangle(rect, radius);

        SmoothingMode previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(BackColor))
        {
            e.Graphics.FillPath(fill, path);
        }
        using (var pen = new Pen(_edge))
        {
            e.Graphics.DrawPath(pen, path);
        }
        e.Graphics.SmoothingMode = previous;
    }
}

/// <summary>
/// A flat button with smooth, anti-aliased rounded corners, matching <see
/// cref="RoundedTablePanel"/>'s treatment of the hero card. A plain <see
/// cref="FlatStyle.Flat"/> button's <c>Region</c> could be clipped to a rounded
/// rectangle instead, but Windows' region clipping is a hard per-pixel mask
/// with no anti-aliasing - the corners would look stair-stepped rather than
/// smooth, which is exactly the difference this control exists to avoid.
/// <para>
/// Hover/pressed feedback is read from the SAME <see
/// cref="ButtonBase.FlatAppearance"/> colors every other button in this app
/// already sets (<c>MouseOverBackColor</c>/<c>MouseDownBackColor</c>) - custom
/// painting replaces HOW they get drawn, not what a caller sets them to.
/// </para>
/// </summary>
internal sealed class RoundedButton : Button
{
    private bool _isHovered;
    private bool _isPressed;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _isHovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _isPressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _isPressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        base.OnEnabledChanged(e);
    }

    /// <summary>
    /// A no-op override, deliberately: everything this control draws - the
    /// corner background included - happens in <see cref="OnPaint"/> on an
    /// offscreen buffer this method builds and fully controls, in one pass.
    /// Splitting the corner fill into a separate OnPaintBackground call (the
    /// obvious approach, and RoundedTablePanel's own approach) was tried
    /// first and confirmed LIVE to fail: GDI+'s anti-aliasing at the rounded
    /// path's edge in OnPaint blended against black rather than against
    /// whatever OnPaintBackground had just filled, leaving a black fringe
    /// along the curve. Compositing everything in one offscreen Bitmap before
    /// it ever touches the real surface removes the two-pass handoff where
    /// that happened.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Rectangle rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0) return;

        Color fill = Enabled && _isPressed && FlatAppearance.MouseDownBackColor != Color.Empty
            ? FlatAppearance.MouseDownBackColor
            : Enabled && _isHovered && FlatAppearance.MouseOverBackColor != Color.Empty
                ? FlatAppearance.MouseOverBackColor
                : BackColor;

        // Corners the rounded rectangle does not cover must show the real
        // surrounding surface, not this control's own square BackColor - see
        // RoundedGeometry.ResolveOpaqueAncestorBackColor for why this walks up
        // rather than trusting Parent.BackColor directly.
        Color surfaceColor = RoundedGeometry.ResolveOpaqueAncestorBackColor(Parent, BackColor);

        int radius = Math.Max(1, LogicalToDeviceUnits(6));
        using GraphicsPath path = RoundedGeometry.RoundedRectangle(rect, radius);

        // Format24bppRgb, not the ARGB32 Bitmap.ctor(w,h) defaults to: this
        // buffer is always fully opaque-filled below before anything else is
        // drawn onto it, and TextRenderer.DrawText's GDI (not GDI+) text
        // rendering does not composite cleanly against an alpha channel -
        // confirmed live, it left a ghosted/shadowed double-render of the
        // button text until this pixel format removed the ambiguity.
        using var buffer = new Bitmap(ClientRectangle.Width, ClientRectangle.Height, PixelFormat.Format24bppRgb);
        using (Graphics bg = Graphics.FromImage(buffer))
        {
            // Opaque corner fill FIRST, on the SAME surface the anti-aliased
            // path below blends against - the ordering that OnPaintBackground
            // + OnPaint as two separate calls did not reliably guarantee.
            using (var surfaceBrush = new SolidBrush(surfaceColor))
            {
                bg.FillRectangle(surfaceBrush, 0, 0, buffer.Width, buffer.Height);
            }

            bg.SmoothingMode = SmoothingMode.AntiAlias;

            using (var fillBrush = new SolidBrush(fill))
            {
                bg.FillPath(fillBrush, path);
            }

            if (FlatAppearance.BorderSize > 0)
            {
                using var pen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize);
                bg.DrawPath(pen, path);
            }

            // NOT ControlPaint.DrawFocusRectangle: that draws a plain SQUARE
            // dashed rectangle, oblivious to the rounded fill beneath it - a
            // focus ring built from the SAME rounded path this button fills
            // with stays inside that silhouette by construction.
            if (Focused && ShowFocusCues)
            {
                Rectangle focusRect = Rectangle.Inflate(rect, -3, -3);
                if (focusRect.Width > 0 && focusRect.Height > 0)
                {
                    using GraphicsPath focusPath = RoundedGeometry.RoundedRectangle(focusRect, Math.Max(1, radius - 3));
                    using var focusPen = new Pen(ForeColor, 1F) { DashStyle = DashStyle.Dot };
                    bg.DrawPath(focusPen, focusPath);
                }
            }

            // GDI+ DrawString, not TextRenderer.DrawText (GDI/ClearType): GDI's
            // ClearType text rendering assumes it is compositing directly onto
            // its final on-screen destination, and subpixel color-fringes into
            // a bolded, shadowed-looking mess when drawn onto an intermediate
            // offscreen Bitmap like this one instead - confirmed live. GDI+
            // has no such assumption; AntiAliasGridFit renders correctly
            // regardless of where this buffer is later blitted to.
            bg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            using var textBrush = new SolidBrush(ForeColor);
            bg.DrawString(Text, Font, textBrush, ClientRectangle, stringFormat);
        }

        pevent.Graphics.DrawImageUnscaled(buffer, 0, 0);
    }
}
