using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// A flat, hairline-bordered renderer for menu and status strips, so the
    /// window chrome matches the soft Discover styling instead of the classic
    /// raised 3D look.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class SoftToolStripRenderer : ToolStripProfessionalRenderer
    {
        public SoftToolStripRenderer()
            : base(new SoftColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Color background = e.ToolStrip is StatusStrip
                ? SoftTheme.Backdrop
                : SoftTheme.Surface;
            using (var brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        /// <summary>
        /// Draw a single hairline instead of the default chunky border. Status
        /// strips get a top rule, menu strips a bottom one.
        /// </summary>
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = e.ToolStrip.ClientRectangle;
            using (var pen = new Pen(SoftTheme.Border))
            {
                if (e.ToolStrip is StatusStrip)
                {
                    e.Graphics.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right - 1, bounds.Top);
                }
                else
                {
                    e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1,
                                        bounds.Right - 1, bounds.Bottom - 1);
                }
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed)
            {
                return;
            }
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
            SoftTheme.FillRounded(g, rect,
                                  e.Item.Pressed ? SoftTheme.Border : SoftTheme.AccentSoft,
                                  SoftTheme.RadiusControl);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(1, 1, e.Item.Width - 3, e.Item.Height - 3);
            if (e.Item.Pressed)
            {
                SoftTheme.FillRounded(g, rect, SoftTheme.AccentSoft, SoftTheme.RadiusControl);
            }
            else if (e.Item.Selected)
            {
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceHover, SoftTheme.RadiusControl);
            }
            else if (e.Item is ToolStripButton { Checked: true })
            {
                SoftTheme.FillRounded(g, rect, SoftTheme.AccentSoft, SoftTheme.RadiusControl);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (e.Item.Selected || e.Item.Pressed ? SoftTheme.AccentDeep
                                                     : SoftTheme.TextPrimary)
                : SoftTheme.TextMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = e.Item.Bounds;
            using (var pen = new Pen(SoftTheme.Border))
            {
                int y = bounds.Height / 2;
                e.Graphics.DrawLine(pen, bounds.Left + 4, y, bounds.Right - 4, y);
            }
        }

        protected override void OnRenderToolStripStatusLabelBackground(ToolStripItemRenderEventArgs e)
        {
            // Status labels stay flat; the strip background does the work
        }

        protected override void OnRenderItemBackground(ToolStripItemRenderEventArgs e)
        {
            // No default item chrome
        }

        /// <summary>
        /// A soft replacement for the harsh default professional palette.
        /// </summary>
        private sealed class SoftColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => SoftTheme.Surface;
            public override Color MenuBorder                  => SoftTheme.Border;
            public override Color MenuItemBorder              => SoftTheme.AccentSoft;
            public override Color MenuItemSelected            => SoftTheme.AccentSoft;
            public override Color MenuItemSelectedGradientBegin => SoftTheme.AccentSoft;
            public override Color MenuItemSelectedGradientEnd   => SoftTheme.AccentSoft;
            public override Color MenuItemPressedGradientBegin  => SoftTheme.Border;
            public override Color MenuItemPressedGradientEnd    => SoftTheme.Border;
            public override Color ImageMarginGradientBegin    => SoftTheme.Surface;
            public override Color ImageMarginGradientMiddle   => SoftTheme.Surface;
            public override Color ImageMarginGradientEnd      => SoftTheme.Surface;
            public override Color SeparatorDark               => SoftTheme.Border;
            public override Color SeparatorLight              => SoftTheme.Surface;
        }
    }
}
