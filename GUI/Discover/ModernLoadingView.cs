using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal sealed class ModernLoadingView : Control
    {
        private readonly Timer animation = new Timer { Interval = 33 };
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        private string detail = "";
        private int progress;
        private bool indeterminate = true;

        public ModernLoadingView()
        {
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            animation.Tick += (s, e) => Invalidate();
            VisibleChanged += (s, e) => animation.Enabled = Visible;
            HandleCreated += (s, e) => animation.Enabled = Visible;
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        public void UpdateProgress(int value, bool marquee, string text)
        {
            progress = Math.Max(0, Math.Min(100, value));
            indeterminate = marquee;
            detail = text ?? "";
            Invalidate();
        }

        private static string Resource(string key)
            => Properties.Resources.ResourceManager.GetString(key) ?? key;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SoftTheme.Backdrop);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int P(int value) => SoftTheme.Px(value);
            int width = Math.Min(P(520), Math.Max(P(240), Width - P(64)));
            int left = (Width - width) / 2;
            int top = Math.Max(P(32), (Height - P(340)) / 2);
            var mark = new Rectangle(Width / 2 - P(28), top, P(56), P(56));
            using (var brush = new LinearGradientBrush(mark, SoftTheme.Accent,
                Color.FromArgb(125, 89, 244), 45f))
            using (var path = SoftTheme.RoundedPath(mark, P(17)))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, "CK", SoftTheme.SectionFont, mark, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Resource("ModernLoadingTitle"), SoftTheme.DisplayFont,
                new Rectangle(left, top + P(78), width, P(40)), SoftTheme.TextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Resource("ModernLoadingSubtitle"), SoftTheme.SearchFont,
                new Rectangle(left, top + P(126), width, P(46)), SoftTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            var track = new Rectangle(left + P(46), top + P(189), width - P(92), P(5));
            SoftTheme.FillRounded(g, track, SoftTheme.SurfaceRaised, P(3));
            if (indeterminate)
            {
                int span = track.Width / 3;
                double phase = (Math.Sin(elapsed.Elapsed.TotalSeconds * 2.2) + 1) / 2;
                var pulse = new Rectangle(track.Left + (int)((track.Width - span) * phase),
                    track.Top, span, track.Height);
                SoftTheme.FillRounded(g, pulse, SoftTheme.Accent, P(3));
            }
            else if (progress > 0)
                SoftTheme.FillRounded(g, new Rectangle(track.Left, track.Top,
                    Math.Max(P(5), track.Width * progress / 100), track.Height), SoftTheme.Accent, P(3));
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(detail)
                ? Resource("ModernLoadingDetail") : detail, SoftTheme.MetaFont,
                new Rectangle(left, top + P(210), width, P(32)), SoftTheme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int gap = P(12), cardWidth = (width - gap * 2) / 3;
            for (int i = 0; i < 3; i++)
            {
                int x = left + i * (cardWidth + gap);
                SoftTheme.FillRounded(g, new Rectangle(x, top + P(264), cardWidth, P(72)),
                    SoftTheme.Surface, P(12));
                SoftTheme.FillRounded(g, new Rectangle(x + P(14), top + P(282), cardWidth - P(28), P(8)),
                    SoftTheme.SurfaceRaised, P(4));
                SoftTheme.FillRounded(g, new Rectangle(x + P(14), top + P(305), cardWidth * 2 / 3, P(6)),
                    SoftTheme.SurfaceRaised, P(3));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
