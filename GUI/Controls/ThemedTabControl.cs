using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// A flat, soft tab strip drawn in the same visual language as the Discover
    /// surface: no bevels, no gradient, a hairline under the strip and an accent
    /// underline beneath the active tab.
    ///
    /// The previous implementation fell back to the OS visual style in dark mode
    /// and drew bare text on a plain rectangle in light mode, which is what made
    /// the tab strips look like leftovers from a different application. Both the
    /// outer application tabs and the mod detail pane use this control, so
    /// styling it here brings both onto the soft palette at once.
    ///
    /// Painting is taken over completely (<see cref="ControlStyles.UserPaint"/>)
    /// rather than using <see cref="TabDrawMode.OwnerDrawFixed"/>. With
    /// owner-draw the native control still paints the strip background and the
    /// page frame, and it repaints only the invalidated region, so a strip-wide
    /// background pass from the item draw handler erased the other tabs' labels
    /// whenever a single tab was invalidated. Owning the whole paint keeps the
    /// strip and the frame consistent.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public class ThemedTabControl : TabControl
    {
        // 96 DPI design units.
        private const int DesignTabPadding  = 12;
        private const int DesignIconGap     = 6;
        private const int DesignUnderline   = 2;
        private const int DesignIconSize    = 16;

        private int   hoverIndex = -1;
        private float cachedDpi;

        public ThemedTabControl()
        {
            SetStyle(ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.ResizeRedraw, true);

            // Content-sized rather than uniform. Uniform tabs take the width of
            // the longest label for every tab, which pushed the detail pane's
            // five tabs onto two rows even though the labels together would fit
            // on one; the mod detail pane is only ~690 px wide, so the wasted
            // width was the difference between one row and two.
            SizeMode   = TabSizeMode.Normal;
            Multiline  = true;
            Appearance = TabAppearance.Normal;

            BackColor  = SoftTheme.Backdrop;
            StripColor = SoftTheme.Backdrop;
            Font       = SoftTheme.TabFont;
            Padding    = new Point(0, 0);
        }

        /// <summary>
        /// The fill behind the tab labels. Defaults to the backdrop, which is
        /// right for the outer strip because it sits directly on the window.
        /// The detail pane sets it to the sheet colour instead, so its header
        /// reads as one surface rather than as a stack of differently coloured
        /// bands.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color StripColor { get; set; }

        /// <summary>
        /// Re-apply the tab metrics. Tab text arrives from resources after the
        /// control is constructed, so this is called once the handle exists and
        /// again whenever a page is added, renamed or re-selected.
        /// </summary>
        public void RefreshTabMetrics()
        {
            // Nothing to measure: Normal size mode derives each tab's width from
            // its own label. Assigning ItemSize here would override that and
            // force every tab to the assigned width, which ellipsized labels as
            // short as "Manage Mods". The call is kept because the strip has to
            // be redrawn once a late-arriving label has changed the widths.
            PerformLayout();
            Invalidate();
        }

        private static bool HasIcon(TabPage page)
            => !string.IsNullOrEmpty(page.ImageKey) || page.ImageIndex > -1;

        private float Dpi
        {
            get
            {
                if (cachedDpi <= 0f)
                {
                    cachedDpi = ReadDpi();
                }
                return cachedDpi;
            }
        }

        private float ReadDpi()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    return g.DpiX > 0 ? g.DpiX : 96f;
                }
            }
            catch
            {
                return 96f;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // The constructor can run before a device context exists, in which
            // case the DPI read falls back to 96. Re-measure now that it is real.
            cachedDpi = 0f;
            RefreshTabMetrics();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            RefreshTabMetrics();
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            // Cheap, and it catches localized tab text applied after construction.
            RefreshTabMetrics();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            cachedDpi = 0f;
            RefreshTabMetrics();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = IndexAt(e.Location);
            if (index != hoverIndex)
            {
                hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIndex != -1)
            {
                hoverIndex = -1;
                Invalidate();
            }
        }

        private int IndexAt(Point point)
        {
            for (int i = 0; i < TabCount; ++i)
            {
                if (GetTabRect(i).Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int stripBottom = Math.Max(0, DisplayRectangle.Top);

            // Nothing else paints for us now, so cover the whole client area
            // first: the page frame the native control used to draw was a grey
            // beveled border, and leaving gaps shows stale pixels.
            using (var page = new SolidBrush(SoftTheme.Surface))
            {
                g.FillRectangle(page, 0, stripBottom, Width, Height - stripBottom);
            }

            if (stripBottom > 0)
            {
                using (var band = new SolidBrush(StripColor))
                {
                    g.FillRectangle(band, 0, 0, Width, stripBottom);
                }
            }

            for (int i = 0; i < TabCount; ++i)
            {
                DrawTab(g, i, stripBottom);
            }

            if (stripBottom > 0)
            {
                // Hairline closing the strip off from the content below it. The
                // active tab's accent underline is drawn on top of this.
                using (var pen = new Pen(SoftTheme.Border))
                {
                    g.DrawLine(pen, 0, stripBottom - 1, Width, stripBottom - 1);
                }
            }

            if (SelectedIndex >= 0 && SelectedIndex < TabCount)
            {
                DrawUnderline(g, SelectedIndex, stripBottom);
            }
        }

        private void DrawTab(Graphics g, int index, int stripBottom)
        {
            TabPage page = TabPages[index];
            Rectangle rect = GetTabRect(index);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            bool active = index == SelectedIndex;
            bool hot    = index == hoverIndex && !active;

            // Resting tabs sit on the band; hovering one lifts it slightly.
            if (hot)
            {
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceHover, SoftTheme.RadiusControl);
            }

            // Reserve room for the alert icon the detail pane puts on a tab.
            var text = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
            if (HasIcon(page))
            {
                int icon   = SoftTheme.ScaleInt(DesignIconSize, Dpi);
                int gap    = SoftTheme.ScaleInt(DesignIconGap, Dpi);
                int offset = text.X + ((text.Width - (icon + gap)) / 2);
                int top    = text.Y + ((text.Height - icon) / 2);
                DrawTabIcon(g, page, new Rectangle(offset, top, icon, icon));
                text = new Rectangle(offset + icon + gap, text.Y,
                                     Math.Max(0, text.Right - (offset + icon + gap)),
                                     text.Height);
            }

            // Both weights are the same on purpose. The native control sizes a
            // tab from its label in the page font, and a bolder face for the
            // selected tab would either clip inside the space reserved for it or
            // force the page font itself to be bold - which the page's content
            // then inherits, turning the whole detail pane bold. The accent
            // underline and the colour change carry the selected state instead.
            TextRenderer.DrawText(g, page.Text,
                                  SoftTheme.TabFont,
                                  text,
                                  active ? SoftTheme.TextPrimary : SoftTheme.TextSecondary,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
        }

        private void DrawUnderline(Graphics g, int index, int stripBottom)
        {
            // Accent underline, the cue that ties the strip to the page below it
            // without resorting to a beveled tab shape. Anchored to the bottom of
            // the band rather than to the tab's own rect: the native tab rect is
            // noticeably shorter than the band, so anchoring to it left the
            // underline floating in mid-air.
            Rectangle rect = GetTabRect(index);
            if (rect.Width <= 0 || stripBottom <= 0)
            {
                return;
            }

            int thickness = SoftTheme.ScaleInt(DesignUnderline, Dpi);
            int inset     = SoftTheme.ScaleInt(DesignTabPadding, Dpi) / 2;
            var underline = new Rectangle(rect.X + inset,
                                          Math.Max(0, stripBottom - thickness),
                                          Math.Max(1, rect.Width - (inset * 2)),
                                          thickness);
            SoftTheme.FillRounded(g, underline, SoftTheme.Accent, thickness / 2);
        }

        private void DrawTabIcon(Graphics g, TabPage page, Rectangle bounds)
        {
            Image? image = null;
            try
            {
                if (ImageList == null)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(page.ImageKey))
                {
                    int index = ImageList.Images.IndexOfKey(page.ImageKey);
                    if (index >= 0)
                    {
                        image = ImageList.Images[index];
                    }
                }
                else if (page.ImageIndex >= 0 && page.ImageIndex < ImageList.Images.Count)
                {
                    image = ImageList.Images[page.ImageIndex];
                }
            }
            catch (ArgumentException)
            {
                // Unknown key; draw no icon rather than throwing mid-paint.
            }

            if (image == null)
            {
                return;
            }

            var previous = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, bounds);
            g.InterpolationMode = previous;
        }
    }
}
