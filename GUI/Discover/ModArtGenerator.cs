using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// Generates deterministic placeholder "cover art" for mods that have no
    /// real artwork, in the style of a streaming service tile: a vertical
    /// gradient between a light tint and a dark shade of the seed hue, a diagonal
    /// light streak, large semi transparent initials and a soft dark band along
    /// the bottom for overlaid text.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public static class ModArtGenerator
    {
        private const int InitialsAlpha = 90;
        private const int BandAlpha     = 130;

        // Nothing sane is anywhere near this big; it just keeps absurd inputs from OOMing.
        private const int MaxDimension  = 8192;

        // Resolved once, shared by every generated tile.
        // SystemFonts.DefaultFont.FontFamily is a shared object and must not be disposed.
        private static readonly FontFamily InitialsFontFamily = ResolveFontFamily();

        /// <summary>
        /// A saturated but not garish color derived deterministically from a mod identifier.
        /// The same identifier always yields the same color, even across processes.
        /// </summary>
        public static Color SeedColor(string identifier)
        {
            uint hash = StableHash(identifier);

            // Spread the hash bits so hue, saturation and value vary independently.
            float hue = (hash % 360u) / 360f;
            float sat = 0.45f + ((hash >> 9)  % 21u) / 100f;
            float val = 0.55f + ((hash >> 17) % 21u) / 100f;

            return FromHsv(hue, sat, val);
        }

        /// <summary>
        /// The first character of up to the first two whitespace separated words,
        /// uppercased. Returns "?" for empty input.
        /// </summary>
        public static string InitialsForName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "?";
            }

            string[] words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return "?";
            }

            string initials = "";
            for (int i = 0; i < words.Length && initials.Length < 2; ++i)
            {
                // Guard against zero length words, which RemoveEmptyEntries should
                // already have filtered out but let's not rely on it.
                if (words[i].Length > 0)
                {
                    // Non letters (digits, symbols, surrogate halves) are kept as is.
                    initials += char.ToUpperInvariant(words[i][0]);
                }
            }

            return initials.Length == 0 ? "?" : initials;
        }

        /// <summary>
        /// Draws a fallback cover. Never throws and never returns null;
        /// if anything goes wrong a blank (or partially drawn) tile is returned.
        /// The caller owns the returned bitmap and must dispose it.
        /// </summary>
        public static Bitmap CreateFallbackCover(string identifier, string name, int width, int height)
        {
            int w = width  < 1 ? 1 : (width  > MaxDimension ? MaxDimension : width);
            int h = height < 1 ? 1 : (height > MaxDimension ? MaxDimension : height);

            Bitmap bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                Color seed   = SeedColor(identifier);
                Color top    = Mix(seed, Color.White, 0.42f);
                Color bottom = Mix(seed, Color.Black, 0.45f);

                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode     = SmoothingMode.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.PixelOffsetMode   = PixelOffsetMode.HighQuality;

                    using (LinearGradientBrush background = new LinearGradientBrush(
                               new Rectangle(0, 0, w, h), top, bottom, LinearGradientMode.Vertical))
                    {
                        g.FillRectangle(background, 0, 0, w, h);
                    }

                    DrawStreak(g, w, h);
                    DrawInitials(g, InitialsForName(name), w, h);
                    DrawBottomBand(g, w, h);
                }
            }
            catch (Exception)
            {
                // A drawing failure must never bubble up to the caller.
            }

            return bitmap;
        }

        /// <summary>
        /// A translucent diagonal highlight running across the tile.
        /// </summary>
        private static void DrawStreak(Graphics g, int width, int height)
        {
            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(width / 2f, height / 2f);
                g.RotateTransform(-28f);

                float streakWidth = Math.Max(8f, width * 0.18f);
                float span        = (width + height) * 1.2f;
                if (span < 1f)
                {
                    span = 1f;
                }

                RectangleF rect = new RectangleF(-streakWidth / 2f, -span / 2f, streakWidth, span);
                using (LinearGradientBrush streak = new LinearGradientBrush(
                           rect, Color.Transparent, Color.White, LinearGradientMode.Horizontal))
                {
                    // Fade in and out across the streak so it reads as a soft light sweep.
                    streak.InterpolationColors = new ColorBlend(3)
                    {
                        Colors    = new[] { Color.FromArgb(0,  Color.White),
                                            Color.FromArgb(46, Color.White),
                                            Color.FromArgb(0,  Color.White) },
                        Positions = new[] { 0f, 0.5f, 1f },
                    };
                    g.FillRectangle(streak, rect);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }

        /// <summary>
        /// The mod's initials, large and semi transparent, centered on the tile.
        /// </summary>
        private static void DrawInitials(Graphics g, string initials, int width, int height)
        {
            float pixelSize = height * 0.38f;
            if (pixelSize < 1f)
            {
                pixelSize = 1f;
            }

            using (Font font = CreateInitialsFont(pixelSize))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(InitialsAlpha, 255, 255, 255)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment     = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming      = StringTrimming.None;
                format.FormatFlags   = StringFormatFlags.NoWrap;
                g.DrawString(initials, font, brush, new RectangleF(0f, 0f, width, height), format);
            }
        }

        /// <summary>
        /// A dark gradient over the bottom ~35% of the tile, so any text drawn
        /// on top of the cover stays readable.
        /// </summary>
        private static void DrawBottomBand(Graphics g, int width, int height)
        {
            int bandHeight = Math.Max(1, (int)(height * 0.35f));
            Rectangle band = new Rectangle(0, height - bandHeight, width, bandHeight);

            using (LinearGradientBrush bandBrush = new LinearGradientBrush(
                       band, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(BandAlpha, 0, 0, 0),
                       LinearGradientMode.Vertical))
            {
                g.FillRectangle(bandBrush, band);
            }
        }

        /// <summary>
        /// FNV-1a over the UTF-16 code units of the identifier.
        /// Deliberately not string.GetHashCode(), which is randomized per process.
        /// </summary>
        private static uint StableHash(string? identifier)
        {
            const uint Offset = 2166136261u;
            const uint Prime  = 16777619u;

            uint hash = Offset;
            if (identifier != null)
            {
                foreach (char c in identifier)
                {
                    hash ^= (byte)(c & 0xFF);
                    hash *= Prime;
                    hash ^= (byte)((c >> 8) & 0xFF);
                    hash *= Prime;
                }
            }
            return hash;
        }

        private static Color Mix(Color from, Color to, float amount)
        {
            float t = amount < 0f ? 0f : (amount > 1f ? 1f : amount);
            return Color.FromArgb(
                from.A,
                Channel(from.R, to.R, t),
                Channel(from.G, to.G, t),
                Channel(from.B, to.B, t));
        }

        private static int Channel(int from, int to, float amount)
        {
            return (int)Math.Round(from + (to - from) * amount);
        }

        private static Color FromHsv(float hue, float saturation, float value)
        {
            float h = hue - (float)Math.Floor(hue);
            float s = saturation < 0f ? 0f : (saturation > 1f ? 1f : saturation);
            float v = value      < 0f ? 0f : (value      > 1f ? 1f : value);

            float h6 = h * 6f;
            int   i  = (int)Math.Floor(h6) % 6;
            float f  = h6 - (float)Math.Floor(h6);

            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float t = v * (1f - s * (1f - f));

            switch (i)
            {
                case 0:  return Rgb(v, t, p);
                case 1:  return Rgb(q, v, p);
                case 2:  return Rgb(p, v, t);
                case 3:  return Rgb(p, q, v);
                case 4:  return Rgb(t, p, v);
                default: return Rgb(v, p, q);
            }
        }

        private static Color Rgb(float r, float g, float b)
        {
            return Color.FromArgb(ToByte(r), ToByte(g), ToByte(b));
        }

        private static int ToByte(float channel)
        {
            int value = (int)Math.Round(channel * 255f);
            return value < 0 ? 0 : (value > 255 ? 255 : value);
        }

        private static FontFamily ResolveFontFamily()
        {
            foreach (string candidate in new[] { "Segoe UI", "Tahoma", "Microsoft Sans Serif", "Arial" })
            {
                try
                {
                    return new FontFamily(candidate);
                }
                catch (Exception)
                {
                    // Not installed, try the next candidate.
                }
            }

            try
            {
                return SystemFonts.DefaultFont.FontFamily;
            }
            catch (Exception)
            {
                // Fall through to a generic family below.
            }

            return new FontFamily(GenericFontFamilies.SansSerif);
        }

        private static Font CreateInitialsFont(float pixelSize)
        {
            try
            {
                return new Font(InitialsFontFamily, pixelSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch (Exception)
            {
                return new Font(SystemFonts.DefaultFont, FontStyle.Bold);
            }
        }
    }
}
