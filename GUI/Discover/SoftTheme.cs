using System;
using System.Drawing;
using System.Drawing.Drawing2D;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// A single source of truth for the soft, modern "streaming service" look
    /// used by the Discover view. The rest of the classic GUI is intentionally
    /// left alone; everything here is additive styling.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public static class SoftTheme
    {
        /// <summary>
        /// The host is using a dark color scheme. Cached because reading the
        /// Windows registry on every paint would be silly.
        /// </summary>
        // The current concept file is authored in its dark scheme.  Keep that
        // as the native shell's deterministic starting point; the T shortcut
        // still lets the user switch to the light variant at any time.
        public static bool IsDark { get; private set; } = true;

        public static void SetDark(bool dark)
        {
            IsDark = dark;
        }

        // net481's Control.DeviceDpi can stay at 96 with system-DPI awareness.
        // The drawing surface is authoritative for both text and layout.
        public static readonly float LayoutDpi = ReadLayoutDpi();

        private static float ReadLayoutDpi()
        {
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                return graphics.DpiX > 0 ? graphics.DpiX : 96f;
            }
        }

        public static int Px(int value) => ScaleInt(value, LayoutDpi);

        #region Palette

        /// <summary>
        /// The page/stage colour behind the rounded application surface. It is
        /// intentionally distinct from <see cref="Backdrop"/>, which is the
        /// app's content window colour.
        /// </summary>
        public static Color Backdrop      => IsDark ? Color.FromArgb(17, 19, 24)    : Color.FromArgb(243, 245, 249);
        public static Color Surface       => IsDark ? Color.FromArgb(28, 31, 38)    : Color.FromArgb(255, 255, 255);
        public static Color SurfaceRaised => IsDark ? Color.FromArgb(37, 41, 50)    : Color.FromArgb(255, 255, 255);
        public static Color SurfaceSunken => IsDark ? Color.FromArgb(23, 26, 32)    : Color.FromArgb(232, 236, 242);
        public static Color SurfaceHover  => IsDark ? Color.FromArgb(45, 50, 61)    : Color.FromArgb(247, 249, 253);
        // The concept uses translucent window chrome rather than a second
        // opaque slab. These are the pre-composited equivalents used by the
        // native painter when a WinForms child cannot backdrop-filter itself.
        public static Color TitlebarSurface => Mix(Backdrop, Surface, 0.55f);
        public static Color RailSurface     => Mix(Backdrop, Surface, 0.32f);
        public static Color StatusSurface   => Mix(Backdrop, Surface, 0.62f);
        public static Color TextPrimary   => IsDark ? Color.FromArgb(237, 240, 246) : Color.FromArgb(26, 30, 38);
        public static Color TextSecondary => IsDark ? Color.FromArgb(152, 160, 175) : Color.FromArgb(107, 115, 130);
        public static Color TextMuted     => IsDark ? Color.FromArgb(112, 120, 134) : Color.FromArgb(150, 156, 168);
        public static Color Border        => IsDark ? Color.FromArgb(51, 56, 67)    : Color.FromArgb(220, 224, 232);
        public static Color BorderStrong  => IsDark ? Color.FromArgb(75, 82, 97)    : Color.FromArgb(196, 203, 215);

        public static Color Accent        => Color.FromArgb(94, 134, 246);
        public static Color AccentDeep    => Color.FromArgb(64, 102, 219);
        public static Color AccentSoft    => IsDark ? Color.FromArgb(38, 51, 88)    : Color.FromArgb(228, 235, 253);
        public static Color OnAccent      => Color.White;

        public static Color Success       => IsDark ? Color.FromArgb(72, 176, 128)
                                                   : Color.FromArgb(47, 156, 107);
        public static Color SuccessSoft   => IsDark ? Color.FromArgb(28, 56, 45)    : Color.FromArgb(224, 244, 234);
        public static Color Warning       => IsDark ? Color.FromArgb(228, 166, 72)
                                                   : Color.FromArgb(201, 135, 31);
        public static Color WarningSoft   => IsDark ? Color.FromArgb(60, 47, 25)    : Color.FromArgb(253, 243, 223);
        public static Color Danger        => IsDark ? Color.FromArgb(224, 96, 96)
                                                   : Color.FromArgb(211, 75, 75);
        public static Color DangerSoft    => IsDark ? Color.FromArgb(63, 33, 33)    : Color.FromArgb(252, 232, 232);

        /// <summary>
        /// A translucent scrim painted over artwork so overlaid text stays readable.
        /// </summary>
        public static Color ScrimTop    => Color.FromArgb(0,   10, 14, 22);
        public static Color ScrimBottom => Color.FromArgb(235, 10, 14, 22);

        #endregion

        #region Geometry

        // Keep the native geometry in lockstep with the current visual design:
        // cards use the larger 18px radius while controls use the softer 11px
        // radius shared by the search fields, pills, and action buttons.
        public const int RadiusCard     = 18;
        public const int RadiusControl  = 11;
        public const int RadiusPill     = 999;

        /// <summary>
        /// Scale a 96 DPI design value to the target DPI.
        /// </summary>
        public static float Scale(float value, float dpi)
            => value * dpi / 96f;

        public static int ScaleInt(int value, float dpi)
            => (int)Math.Round(Scale(value, dpi));

        /// <summary>
        /// Build a rounded rectangle path. The radius is clamped so it can never
        /// exceed half of the shorter side.
        /// </summary>
        public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
            => RoundedPath(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

        public static GraphicsPath RoundedPath(RectangleF bounds, int radius)
        {
            var path = new GraphicsPath();
            float r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            if (r <= 0.5f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = r * 2f;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle bounds, Color color, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            using (var path = RoundedPath(bounds, radius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        public static void FillRounded(Graphics g, RectangleF bounds, Brush brush, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            using (var path = RoundedPath(bounds, radius))
            {
                g.FillPath(brush, path);
            }
        }

        public static void DrawRounded(Graphics g, Rectangle bounds, Color color, int radius, float thickness = 1f)
            => DrawRounded(g, new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), color, radius, thickness);

        public static void DrawRounded(Graphics g, RectangleF bounds, Color color, int radius, float thickness = 1f)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            // Inset by half the pen width so the stroke lands inside the bounds
            var inset = new RectangleF(bounds.X + thickness / 2f,
                                       bounds.Y + thickness / 2f,
                                       Math.Max(0, bounds.Width - thickness),
                                       Math.Max(0, bounds.Height - thickness));
            using (var path = RoundedPath(inset, radius))
            using (var pen = new Pen(color, thickness))
            {
                pen.Alignment = PenAlignment.Center;
                g.DrawPath(pen, path);
            }
        }

        /// <summary>
        /// Paint a soft drop shadow beneath a rounded rectangle. Cheap and
        /// good enough: a handful of progressively more transparent outlines.
        /// </summary>
        public static void DrawSoftShadow(Graphics g, Rectangle bounds, int radius, int spread, int alpha)
        {
            for (int i = spread; i >= 1; --i)
            {
                var rect = new Rectangle(bounds.X - i,
                                         bounds.Y - i + Math.Max(1, i / 2),
                                         bounds.Width + (i * 2),
                                         bounds.Height + (i * 2));
                int a = Math.Max(1, alpha / (i + 2));
                DrawRounded(g, rect, Color.FromArgb(a, 0, 0, 0), radius + i, 1f);
            }
        }

        #endregion

        #region Color helpers

        public static Color Mix(Color a, Color b, float amount)
        {
            float t = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round(a.A + ((b.A - a.A) * t)),
                (int)Math.Round(a.R + ((b.R - a.R) * t)),
                (int)Math.Round(a.G + ((b.G - a.G) * t)),
                (int)Math.Round(a.B + ((b.B - a.B) * t)));
        }

        public static Color WithAlpha(Color c, int alpha)
            => Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B);

        /// <summary>
        /// A readable foreground color for the given background.
        /// </summary>
        public static Color ReadableOn(Color back)
            => back.IsLight() ? Color.FromArgb(24, 28, 36) : Color.White;

        #endregion

        #region Typography

        private const string PreferredFontFamily = "Segoe UI";

        /// <summary>
        /// Create a font in the preferred UI family, degrading gracefully on
        /// systems (or Mono installs) where it is missing.
        /// </summary>
        public static Font CreateFont(float size, FontStyle style)
        {
            try
            {
                return new Font(PreferredFontFamily, size, style, GraphicsUnit.Point);
            }
            catch
            {
                try
                {
                    return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
                }
                catch
                {
                    return new Font(SystemFonts.DefaultFont, style);
                }
            }
        }

        // App-lifetime fonts. Created once and reused for every paint so we do
        // not leak GDI handles on the hot path.
        public static readonly Font DisplayFont     = CreateFont(19f,   FontStyle.Bold);
        public static readonly Font InspectorTitleFont = CreateFont(14f, FontStyle.Bold);
        public static readonly Font InspectorByFont    = CreateFont(9.25f, FontStyle.Regular);
        public static readonly Font SectionFont     = CreateFont(12.5f, FontStyle.Bold);
        public static readonly Font TitleFont       = CreateFont(11f,   FontStyle.Bold);
        public static readonly Font SubtitleFont    = CreateFont(9.5f,  FontStyle.Regular);
        public static readonly Font SearchFont      = CreateFont(10f,   FontStyle.Regular);
        public static readonly Font PillButtonFont  = CreateFont(8.75f, FontStyle.Regular);
        public static readonly Font MetaFont        = CreateFont(8.25f, FontStyle.Regular);
        public static readonly Font CardTitleFont   = CreateFont(10.5f, FontStyle.Bold);
        public static readonly Font CardAuthorFont  = CreateFont(8.25f, FontStyle.Regular);
        public static readonly Font CardBodyFont    = CreateFont(8f,    FontStyle.Regular);
        public static readonly Font PillFont        = CreateFont(7.5f,  FontStyle.Bold);
        public static readonly Font ActionFont      = CreateFont(8.5f,  FontStyle.Bold);

        // Tab strip. One weight for resting and selected tabs alike: the tab
        // strip's selected state is carried by colour and the accent underline,
        // because a bold face would not fit the width the native control
        // reserves from the label.
        public static readonly Font TabFont         = CreateFont(9.5f,  FontStyle.Regular);

        #endregion
    }
}
