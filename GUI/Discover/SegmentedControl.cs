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
    /// A two-or-more segment switch in the spirit of a native segmented
    /// control: a soft track with a raised thumb behind the active segment.
    /// Used for the Discover / List switch.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class SegmentedControl : Control
    {
        private string[] items = Array.Empty<string>();
        private int selectedIndex;
        private int hoverIndex = -1;

        public SegmentedControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            BackColor = SoftTheme.Surface;
            TabStop   = false;
            Cursor    = Cursors.Hand;
        }

        public event Action<int>? SelectionChanged;

        public void SetItems(params string[] texts)
        {
            items = texts ?? Array.Empty<string>();
            selectedIndex = 0;
            hoverIndex = -1;
            Invalidate();
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                int clamped = Math.Max(0, Math.Min(items.Length - 1, value));
                if (clamped != selectedIndex)
                {
                    selectedIndex = clamped;
                    Invalidate();
                    SelectionChanged?.Invoke(selectedIndex);
                }
            }
        }

        /// <summary>
        /// Update the highlighted segment without raising the change event,
        /// for when the owner is the one driving the switch.
        /// </summary>
        public void SetSelectedIndexQuietly(int value)
        {
            int clamped = Math.Max(0, Math.Min(items.Length - 1, value));
            if (clamped != selectedIndex)
            {
                selectedIndex = clamped;
                Invalidate();
            }
        }

        /// <summary>
        /// Size the control to its content, in the soft HIG-ish proportions
        /// used by the toolbar.
        /// </summary>
        public Size PreferredControlSize
        {
            get
            {
                int width = 0;
                using (var g = CreateGraphics())
                {
                    foreach (string item in items)
                    {
                        width += TextRenderer.MeasureText(g, item, Font).Width + 26;
                    }
                }
                return new Size(Math.Max(72, width + 6), 28);
            }
        }

        private int Padding_ => 3;

        private Rectangle SegmentBounds(int index)
        {
            if (items.Length == 0)
            {
                return Rectangle.Empty;
            }
            int inner = Width - (Padding_ * 2);
            int each = inner / items.Length;
            int last = Width - Padding_ - (each * (items.Length - 1)) - 1;
            int w = index == items.Length - 1 ? last - Padding_ : each;
            return new Rectangle(Padding_ + (each * index), Padding_, w, Height - (Padding_ * 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            if (items.Length == 0)
            {
                return;
            }

            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            SoftTheme.FillRounded(g, track, SoftTheme.SurfaceSunken, SoftTheme.RadiusControl);
            SoftTheme.DrawRounded(g, track, SoftTheme.Border, SoftTheme.RadiusControl, 1f);

            int radius = Math.Max(3, SoftTheme.RadiusControl - 3);

            // Hover feedback on the inactive segment
            if (hoverIndex >= 0 && hoverIndex != selectedIndex)
            {
                SoftTheme.FillRounded(g, SegmentBounds(hoverIndex),
                                      SoftTheme.Mix(SoftTheme.SurfaceSunken, SoftTheme.Surface, 0.5f),
                                      radius);
            }

            var thumb = SegmentBounds(selectedIndex);
            SoftTheme.FillRounded(g, thumb, SoftTheme.Surface, radius);
            SoftTheme.DrawRounded(g, thumb, SoftTheme.Border, radius, 1f);

            for (int i = 0; i < items.Length; ++i)
            {
                bool active = i == selectedIndex;
                TextRenderer.DrawText(g, items[i], Font, SegmentBounds(i),
                                      active ? SoftTheme.TextPrimary : SoftTheme.TextSecondary,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
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
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
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
                int index = IndexAt(e.Location);
                if (index >= 0)
                {
                    SelectedIndex = index;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left:
                    SelectedIndex = selectedIndex - 1;
                    e.Handled = true;
                    break;
                case Keys.Right:
                    SelectedIndex = selectedIndex + 1;
                    e.Handled = true;
                    break;
            }
            base.OnKeyDown(e);
        }

        private int IndexAt(Point point)
        {
            for (int i = 0; i < items.Length; ++i)
            {
                if (SegmentBounds(i).Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }
    }
}
