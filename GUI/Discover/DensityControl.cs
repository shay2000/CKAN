using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// Gallery density used by the native Discover surface. The three states
    /// mirror the concept art's compact gallery, flat list and table glyphs.
    /// </summary>
    public enum DiscoverDensity
    {
        Compact = 0,
        List    = 1,
        Table   = 2,
    }

    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class DensityControl : Control
    {
        private const int SegmentCount = 3;
        private int selectedIndex;
        private int hoverIndex = -1;

        public DensityControl()
        {
            // Geometry is laid out with SoftTheme's explicit DPI scale.  Do
            // not let a Font/DPI parent rescale the control a second time.
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            BackColor = SoftTheme.Backdrop;
            Size = new Size(SoftTheme.ScaleInt(108, SoftTheme.LayoutDpi), SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi));
            TabStop = true;
            AccessibleRole = AccessibleRole.RadioButton;
            Cursor = Cursors.Hand;
            UpdateAccessibleName();
        }

        public event Action<DiscoverDensity>? SelectionChanged;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DiscoverDensity SelectedDensity
        {
            get => (DiscoverDensity)selectedIndex;
            set
            {
                var clamped = Math.Max(0, Math.Min(SegmentCount - 1, (int)value));
                if (selectedIndex == clamped)
                {
                    return;
                }
                selectedIndex = clamped;
                UpdateAccessibleName();
                Invalidate();
                SelectionChanged?.Invoke((DiscoverDensity)selectedIndex);
            }
        }

        public void SetSelectedDensityQuietly(DiscoverDensity value)
        {
            selectedIndex = Math.Max(0, Math.Min(SegmentCount - 1, (int)value));
            UpdateAccessibleName();
            Invalidate();
        }

        private Rectangle SegmentBounds(int index)
        {
            int padding = SoftTheme.ScaleInt(3, DeviceDpiSafe);
            int innerWidth = Math.Max(1, Width - (padding * 2));
            int each = innerWidth / SegmentCount;
            int left = padding + (each * index);
            int right = index == SegmentCount - 1 ? Width - padding : left + each;
            return new Rectangle(left, padding, Math.Max(1, right - left),
                                 Math.Max(1, Height - (padding * 2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            SoftTheme.FillRounded(g, track, SoftTheme.SurfaceSunken, SoftTheme.RadiusControl);
            SoftTheme.DrawRounded(g, track, SoftTheme.Border, SoftTheme.RadiusControl, 1f);

            if (hoverIndex >= 0 && hoverIndex != selectedIndex)
            {
                SoftTheme.FillRounded(g, SegmentBounds(hoverIndex),
                                      SoftTheme.Mix(SoftTheme.SurfaceSunken, SoftTheme.Surface, 0.5f),
                                      Math.Max(3, SoftTheme.RadiusControl - 3));
            }

            var thumb = SegmentBounds(selectedIndex);
            SoftTheme.FillRounded(g, thumb, SoftTheme.Surface,
                                  Math.Max(3, SoftTheme.RadiusControl - 3));
            SoftTheme.DrawRounded(g, thumb, SoftTheme.Border,
                                  Math.Max(3, SoftTheme.RadiusControl - 3), 1f);

            for (int i = 0; i < SegmentCount; ++i)
            {
                DrawGlyph(g, SegmentBounds(i), (DiscoverDensity)i,
                          i == selectedIndex ? SoftTheme.TextPrimary : SoftTheme.TextMuted);
            }
        }

        private void DrawGlyph(Graphics g, Rectangle bounds, DiscoverDensity density, Color color)
        {
            int centerX = bounds.Left + (bounds.Width / 2);
            int centerY = bounds.Top + (bounds.Height / 2);
            using (var pen = new Pen(color, Math.Max(1f, SoftTheme.Scale(1.35f, DeviceDpiSafe))))
            using (var brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (density == DiscoverDensity.Compact)
                {
                    int barWidth = SoftTheme.ScaleInt(3, DeviceDpiSafe);
                    int gap = SoftTheme.ScaleInt(2, DeviceDpiSafe);
                    int height = SoftTheme.ScaleInt(15, DeviceDpiSafe);
                    int start = centerX - ((barWidth * 4 + gap * 3) / 2);
                    for (int i = 0; i < 4; ++i)
                    {
                        var bar = new Rectangle(start + (i * (barWidth + gap)),
                                                centerY - height / 2, barWidth, height);
                        g.FillRectangle(brush, bar);
                    }
                }
                else if (density == DiscoverDensity.List)
                {
                    int left = centerX - SoftTheme.ScaleInt(9, DeviceDpiSafe);
                    int right = centerX + SoftTheme.ScaleInt(9, DeviceDpiSafe);
                    for (int i = -1; i <= 1; ++i)
                    {
                        int y = centerY + (i * SoftTheme.ScaleInt(5, DeviceDpiSafe));
                        g.DrawLine(pen, left, y, right, y);
                    }
                }
                else
                {
                    int size = SoftTheme.ScaleInt(16, DeviceDpiSafe);
                    var box = new Rectangle(centerX - size / 2, centerY - size / 2, size, size);
                    g.DrawRectangle(pen, box);
                    g.DrawLine(pen, box.Left, box.Top + size / 2, box.Right, box.Top + size / 2);
                    g.DrawLine(pen, box.Left + size / 3, box.Top + size / 2, box.Left + size / 3, box.Bottom);
                }
            }
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
            hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                var index = IndexAt(e.Location);
                if (index >= 0)
                {
                    Focus();
                    SelectedDensity = (DiscoverDensity)index;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left)
            {
                SelectedDensity = (DiscoverDensity)Math.Max(0, selectedIndex - 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                SelectedDensity = (DiscoverDensity)Math.Min(SegmentCount - 1, selectedIndex + 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                SelectedDensity = (DiscoverDensity)selectedIndex;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private int IndexAt(Point point)
        {
            for (int i = 0; i < SegmentCount; ++i)
            {
                if (SegmentBounds(i).Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        private void UpdateAccessibleName()
        {
            AccessibleName = selectedIndex == (int)DiscoverDensity.List
                           ? Properties.Resources.DiscoverDensityList
                           : selectedIndex == (int)DiscoverDensity.Table
                             ? Properties.Resources.DiscoverDensityTable
                             : Properties.Resources.DiscoverDensityCompact;
        }

        private float DeviceDpiSafe
        {
            get
            {
                try
                {
                    return SoftTheme.LayoutDpi;
                }
                catch
                {
                    return 96f;
                }
            }
        }
    }
}
