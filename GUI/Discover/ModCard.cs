using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// The change-set relationship between a mod and what the user has queued.
    /// Drives the card's action button.
    /// </summary>
    public enum ModCardStatus
    {
        NotInstalled,
        Installed,
        QueuedInstall,
        QueuedRemove,
        QueuedUpdate,
        AutoDetected,
        Unavailable,
    }

    /// <summary>
    /// A single Netflix-style tile for one mod. Owns nothing but its own
    /// painting; the owning view supplies artwork and handles interaction.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModCard : Control
    {
        // The visual card is inset by ShadowSpread inside the control so the
        // shadow has room without changing the CSS-sized card itself.
        public const int DesignWidth  = 244;
        public const int DesignHeight = 304;
        public const int CompactWidth  = 190;
        public const int CompactHeight = 268;
        public const int CompactCoverHeight = 104;

        /// <summary>Height of the artwork band in 96 DPI design units.</summary>
        public const int DesignCoverHeight = 132;

        public ModCard(GUIMod mod)
        {
            Mod = mod;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop   = true;
            ApplyDesignSize();
            BackColor = SoftTheme.Backdrop;
            DpiChangedAfterParent += (sender, e) =>
            {
                cachedDpi = 0f;
                ApplyDesignSize();
            };
        }

        private DiscoverDensity density = DiscoverDensity.Compact;

        public DiscoverDensity Density => density;

        public static int WidthFor(DiscoverDensity value)
            => value == DiscoverDensity.Compact ? CompactWidth : DesignWidth;

        public static int HeightFor(DiscoverDensity value)
            => value == DiscoverDensity.Compact ? CompactHeight : DesignHeight;

        public static int CoverHeightFor(DiscoverDensity value)
            => value == DiscoverDensity.Compact ? CompactCoverHeight : DesignCoverHeight;

        public void SetDensity(DiscoverDensity value)
        {
            if (density == value)
            {
                return;
            }
            density = value;
            generatedCover?.Dispose();
            generatedCover = null;
            generatedCoverSize = Size.Empty;
            ApplyDesignSize();
            Invalidate();
        }

        /// <summary>
        /// Size the control in the same DPI-scaled units the paint code uses.
        /// The card's internal metrics (cover band, padding, action button) are all
        /// scaled by <see cref="Dpi"/>, so leaving the control itself at the raw
        /// 96 DPI design size makes the cover band overflow the card and pushes the
        /// title down into the action button on any display above 100%.
        /// </summary>
        private void ApplyDesignSize()
        {
            Size   = new Size(SoftTheme.ScaleInt(WidthFor(density), Dpi),
                              SoftTheme.ScaleInt(HeightFor(density), Dpi));
            Margin = new Padding(SoftTheme.ScaleInt(6, Dpi), SoftTheme.ScaleInt(4, Dpi),
                                 SoftTheme.ScaleInt(6, Dpi), SoftTheme.ScaleInt(12, Dpi));
        }

        /// <summary>The mod this card represents.</summary>
        public GUIMod Mod { get; }

        private ModCardStatus status = ModCardStatus.NotInstalled;
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ModCardStatus Status
        {
            get => status;
            set
            {
                if (status != value)
                {
                    status = value;
                    Invalidate();
                }
            }
        }

        private bool isSelected;
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    Invalidate();
                }
            }
        }

        private Image? cover;
        private Image? generatedCover;
        private Size generatedCoverSize;
        /// <summary>
        /// Artwork supplied by the owner view. Setting a new image disposes the
        /// previous one, since the view hands ownership to the card.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Image? Cover
        {
            get => cover;
            set
            {
                if (!ReferenceEquals(cover, value))
                {
                    var old = cover;
                    cover = value;
                    old?.Dispose();
                    if (value != null)
                    {
                        generatedCover?.Dispose();
                        generatedCover = null;
                        generatedCoverSize = Size.Empty;
                    }
                    Invalidate();
                }
            }
        }

        public event Action<ModCard>? Activated;
        public event Action<ModCard>? ActionClicked;
        public event Action<ModCard>? ContextRequested;
        public event Action<ModCard>? CardFocused;

        private bool hovered;
        private bool hoverAction;
        private float cachedDpi;

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
                using (var g = CreateGraphics())
                {
                    return g.DpiX > 0 ? g.DpiX : 96f;
                }
            }
            catch
            {
                return 96f;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            cachedDpi = 0f;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // The constructor can run before a device context exists, in which case
            // Dpi falls back to 96. Re-apply once the real DPI is known.
            cachedDpi = 0f;
            ApplyDesignSize();
        }

        #region Layout

        private int Pad           => SoftTheme.ScaleInt(12, Dpi);
        private int Radius        => SoftTheme.ScaleInt(SoftTheme.RadiusCard, Dpi);
        private int ShadowSpread  => SoftTheme.ScaleInt(4, Dpi);
        private int CoverHeight   => SoftTheme.ScaleInt(CoverHeightFor(density), Dpi);
        private int PillHeight    => SoftTheme.ScaleInt(19, Dpi);
        private int ActionHeight  => SoftTheme.ScaleInt(30, Dpi);

        private Rectangle CardRect
        {
            get
            {
                int s = ShadowSpread;
                return new Rectangle(s, s, Math.Max(1, Width - (s * 2)), Math.Max(1, Height - (s * 2)));
            }
        }

        private Rectangle CoverRect
        {
            get
            {
                var card = CardRect;
                return new Rectangle(card.X, card.Y, card.Width, Math.Min(CoverHeight, card.Height));
            }
        }

        private Rectangle ActionRect
        {
            get
            {
                var card = CardRect;
                int w = card.Width - Pad * 2 - SoftTheme.ScaleInt(36, Dpi);
                return new Rectangle(card.Left + Pad,
                                     card.Bottom - Pad - ActionHeight,
                                     w, ActionHeight);
            }
        }

        #endregion

        #region Painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);

            var card = CardRect;

            if (hovered || isSelected)
            {
                SoftTheme.DrawSoftShadow(g, card, Radius, ShadowSpread, 34);
            }

            var surface = hovered && !isSelected ? SoftTheme.SurfaceHover : SoftTheme.Surface;
            SoftTheme.FillRounded(g, card, surface, Radius);

            DrawCover(g, card);

            // Content
            int contentTop = CoverRect.Bottom + SoftTheme.ScaleInt(10, Dpi);
            int contentLeft = card.X + Pad;
            int contentWidth = card.Width - (Pad * 2);
            int actionTop = ActionRect.Top;

            DrawBadges(g, new Point(contentLeft, CoverRect.Y + SoftTheme.ScaleInt(8, Dpi)));

            using (var titleBrush = new SolidBrush(SoftTheme.TextPrimary))
            using (var secondaryBrush = new SolidBrush(SoftTheme.TextSecondary))
            using (var mutedBrush = new SolidBrush(SoftTheme.TextMuted))
            {
                var titleFont = SoftTheme.CardTitleFont;
                var authorFont = SoftTheme.CardAuthorFont;
                var bodyFont = SoftTheme.CardBodyFont;
                int y = contentTop;
                int titleHeight = (int)Math.Ceiling(titleFont.GetHeight(g));
                var titleRect = new Rectangle(contentLeft, y, contentWidth, titleHeight);
                DrawTrimmed(g, Mod.Name, titleFont, titleBrush, titleRect, 1);
                y = titleRect.Bottom;

                // The download count lives on the artwork now, so the author
                // line gets the full width to itself.
                var authorText = string.Join(", ", Mod.Authors);
                if (authorText.Length > 0)
                {
                    int authorHeight = (int)Math.Ceiling(authorFont.GetHeight(g));
                    var authorRect = new Rectangle(contentLeft, y, contentWidth, authorHeight);
                    DrawTrimmed(g, authorText, authorFont, secondaryBrush, authorRect, 1);
                    y = authorRect.Bottom + SoftTheme.ScaleInt(3, Dpi);
                }

                int bodySpace = actionTop - SoftTheme.ScaleInt(40, Dpi) - y;
                int lineHeight = (int)Math.Ceiling(bodyFont.GetHeight(g));
                if (density != DiscoverDensity.Compact && bodySpace >= lineHeight)
                {
                    var bodyRect = new Rectangle(contentLeft, y, contentWidth,
                                                 Math.Min(bodySpace, lineHeight * 2));
                    DrawTrimmed(g, Mod.Abstract, bodyFont, mutedBrush, bodyRect,
                                bodySpace >= lineHeight * 2 ? 2 : 1);
                }
            }

            DrawFooter(g, card, actionTop, contentLeft);

            if (isSelected)
            {
                SoftTheme.DrawRounded(g, card, SoftTheme.Accent, Radius, SoftTheme.Scale(2f, Dpi));
            }
            else if (hovered)
            {
                SoftTheme.DrawRounded(g, card, SoftTheme.BorderStrong, Radius, SoftTheme.Scale(1f, Dpi));
            }
            else
            {
                SoftTheme.DrawRounded(g, card, SoftTheme.Border, Radius, SoftTheme.Scale(1f, Dpi));
            }

            if (Focused)
            {
                var focus = Rectangle.Inflate(card, -SoftTheme.ScaleInt(3, Dpi), -SoftTheme.ScaleInt(3, Dpi));
                using (var pen = new Pen(SoftTheme.WithAlpha(SoftTheme.Accent, 150), 1f))
                {
                    pen.DashStyle = DashStyle.Dot;
                    using (var path = SoftTheme.RoundedPath(focus, Math.Max(2, Radius - 3)))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        private void DrawCover(Graphics g, Rectangle card)
        {
            var area = CoverRect;
            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            var saved = g.Save();
            try
            {
                using (var clip = SoftTheme.RoundedPath(card, Radius))
                {
                    g.SetClip(clip, CombineMode.Intersect);
                }
                // Also clip to the artwork band itself: aspect-fill of a portrait
                // banner would otherwise spill down over the card text.
                g.SetClip(area, CombineMode.Intersect);

                Image? artwork = cover;
                if (artwork == null || artwork.Width <= 0 || artwork.Height <= 0)
                {
                    artwork = EnsureGeneratedCover(area.Size);
                }

                if (artwork is Image image && image.Width > 0 && image.Height > 0)
                {
                    DrawImageCover(g, image, area);
                }
                else
                {
                    // A drawing failure is the only case that should reach this
                    // placeholder; normal no-network cards use generatedCover.
                    using (var placeholder = new LinearGradientBrush(
                               area,
                               SoftTheme.SurfaceSunken,
                               SoftTheme.Mix(SoftTheme.SurfaceSunken, SoftTheme.Border, 0.7f),
                               LinearGradientMode.ForwardDiagonal))
                    {
                        g.FillRectangle(placeholder, area);
                    }
                }
            }
            finally
            {
                g.Restore(saved);
            }

            // A soft dark wash at the top keeps the badges legible on any artwork
            var scrimRect = new Rectangle(area.X, area.Y, area.Width, SoftTheme.ScaleInt(56, Dpi));
            if (scrimRect.Height > 0)
            {
                using (var scrim = new LinearGradientBrush(
                           scrimRect,
                           Color.FromArgb(120, 8, 11, 18),
                           Color.FromArgb(0, 8, 11, 18),
                           LinearGradientMode.Vertical))
                {
                    g.FillRectangle(scrim, scrimRect);
                }
            }
        }

        private Image? EnsureGeneratedCover(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }
            if (generatedCover == null || generatedCoverSize != size)
            {
                generatedCover?.Dispose();
                generatedCover = ModArtGenerator.CreateFallbackCover(Mod.Identifier,
                                                                       Mod.Name,
                                                                       size.Width,
                                                                       size.Height);
                generatedCoverSize = size;
            }
            return generatedCover;
        }

        private static void DrawImageCover(Graphics g, Image img, Rectangle target)
        {
            float scale = Math.Max((float)target.Width / img.Width,
                                   (float)target.Height / img.Height);
            int w = (int)Math.Ceiling(img.Width * scale);
            int h = (int)Math.Ceiling(img.Height * scale);
            g.DrawImage(img, new Rectangle(target.X + ((target.Width - w) / 2),
                                           target.Y + ((target.Height - h) / 2),
                                           w, h));
        }

        /// <summary>
        /// The download count as a chip on the artwork, bottom left. It used to
        /// be a right-aligned run of text in the card body, where seven digits
        /// ("2,642,732 downloads") crowded the author line and read as noise.
        /// Overlaid on the artwork it becomes part of the poster, the way a
        /// streaming service labels a title, and the body gets its width back.
        /// </summary>
        private void DrawDownloadsPill(Graphics g, int contentLeft)
        {
            if (Mod.DownloadCount is not int count || count <= 0)
            {
                return;
            }

            string text = string.Format(Properties.Resources.DiscoverDownloadsShort,
                                        CompactCount(count));
            var font = SoftTheme.PillFont;
            var measured = TextRenderer.MeasureText(g, text, font,
                                                    new Size(int.MaxValue, int.MaxValue),
                                                    TextFormatFlags.NoPadding);
            int width = Math.Min(measured.Width + SoftTheme.ScaleInt(16, Dpi),
                                 CardRect.Width - (Pad * 2));
            if (width <= 0)
            {
                return;
            }

            var rect = new Rectangle(contentLeft,
                                     CoverRect.Bottom - PillHeight - SoftTheme.ScaleInt(8, Dpi),
                                     width, PillHeight);

            // Translucent rather than opaque, so the artwork still reads through
            // it while the text stays legible on any image.
            SoftTheme.FillRounded(g, rect, Color.FromArgb(150, 10, 14, 22), rect.Height / 2);
            TextRenderer.DrawText(g, text, font, rect, Color.White,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// Compact a download count to its magnitude: 842, 8.4K, 73.8K, 2.6M.
        /// One decimal place, dropped when it carries no information.
        /// </summary>
        private static string CompactCount(int value)
        {
            double scaled;
            string suffix;
            if (value >= 1000000000)
            {
                scaled = value / 1000000000.0;
                suffix = "B";
            }
            else if (value >= 1000000)
            {
                scaled = value / 1000000.0;
                suffix = "M";
            }
            else if (value >= 1000)
            {
                scaled = value / 1000.0;
                suffix = "K";
            }
            else
            {
                return value.ToString(CultureInfo.CurrentCulture);
            }

            return scaled.ToString("0.#", CultureInfo.CurrentCulture) + suffix;
        }

        private void DrawBadges(Graphics g, Point origin)
        {
            var font = SoftTheme.PillFont;
            int x = origin.X;
            foreach (var (text, back, fore) in Badges())
            {
                var size = TextRenderer.MeasureText(g, text, font,
                                                    new Size(int.MaxValue, int.MaxValue),
                                                    TextFormatFlags.NoPadding);
                int w = size.Width + SoftTheme.ScaleInt(16, Dpi);
                var rect = new Rectangle(x, origin.Y, w, PillHeight);
                SoftTheme.FillRounded(g, rect, back, rect.Height / 2);
                TextRenderer.DrawText(g, text, font, rect, fore,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPadding);
                x = rect.Right + SoftTheme.ScaleInt(5, Dpi);
                if (x > CardRect.Right - SoftTheme.ScaleInt(30, Dpi))
                {
                    break;
                }
            }
        }

        private IEnumerable<(string Text, Color Back, Color Fore)> Badges()
        {
            switch (Status)
            {
                case ModCardStatus.QueuedInstall:
                    yield return (Properties.Resources.ModCardBadgeQueued, SoftTheme.Accent, SoftTheme.OnAccent);
                    break;
                case ModCardStatus.QueuedRemove:
                    yield return (Properties.Resources.ModCardBadgeRemoving, SoftTheme.Danger, Color.White);
                    break;
                case ModCardStatus.QueuedUpdate:
                    yield return (Properties.Resources.ModCardBadgeUpdating, SoftTheme.Accent, SoftTheme.OnAccent);
                    break;
            }

            if (Mod.HasUpdate && Status != ModCardStatus.QueuedUpdate)
            {
                yield return (Properties.Resources.ModCardBadgeUpdate, SoftTheme.Warning, Color.FromArgb(46, 32, 8));
            }
            if (Mod.IsNew)
            {
                yield return (Properties.Resources.ModCardBadgeNew, SoftTheme.AccentSoft, SoftTheme.AccentDeep);
            }
            if (Status == ModCardStatus.Unavailable)
            {
                yield return (Properties.Resources.ModCardBadgeIncompatible, SoftTheme.DangerSoft, SoftTheme.Danger);
            }
            else if (Status == ModCardStatus.AutoDetected)
            {
                yield return (Properties.Resources.ModCardBadgeAutoDetected, SoftTheme.SurfaceSunken, SoftTheme.TextSecondary);
            }
            else if (Mod.IsInstalled && Status != ModCardStatus.QueuedRemove)
            {
                    yield return (Properties.Resources.ModCardBadgeInstalled, SoftTheme.Success, Color.White);
            }
        }

        private void DrawFooter(Graphics g, Rectangle card, int actionTop, int contentLeft)
        {
            var pillFont = SoftTheme.CardAuthorFont;
            var version = Mod.InstalledVersion ?? Mod.LatestVersion;
            if (version.Length > 0 && version != "-")
            {
                var rect = new Rectangle(contentLeft, actionTop - SoftTheme.ScaleInt(36, Dpi),
                        SoftTheme.ScaleInt(density == DiscoverDensity.Compact ? 64 : 88, Dpi), SoftTheme.ScaleInt(22, Dpi));
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceSunken, SoftTheme.ScaleInt(5, Dpi));
                TextRenderer.DrawText(g, version, pillFont, rect, SoftTheme.TextSecondary,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
            }

            int metaLeft = contentLeft + SoftTheme.ScaleInt(density == DiscoverDensity.Compact ? 70 : 94, Dpi);
            var metaRect = new Rectangle(metaLeft, actionTop - SoftTheme.ScaleInt(36, Dpi),
                Math.Max(1, card.Right - Pad - metaLeft), SoftTheme.ScaleInt(22, Dpi));
            string size = CkanModule.FmtSize(Mod.Module.download_size);
            TextRenderer.DrawText(g, size, SoftTheme.CardAuthorFont, metaRect, SoftTheme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Mod.DownloadCount is int downloads && downloads > 0)
            {
                TextRenderer.DrawText(g, CompactCount(downloads), SoftTheme.CardAuthorFont,
                    new Rectangle(contentLeft, actionTop - SoftTheme.ScaleInt(14, Dpi),
                                  card.Width - Pad * 2, SoftTheme.ScaleInt(12, Dpi)),
                    SoftTheme.TextMuted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            var action = ActionRect;
            var (label, back, fore, enabled) = ActionAppearance();
            var hoverBack = enabled ? SoftTheme.Mix(back, SoftTheme.TextPrimary, IsDarkAction(back) ? 0.10f : 0.06f)
                                    : back;
            SoftTheme.FillRounded(g, action, !enabled ? SoftTheme.SurfaceSunken
                                                     : hoverAction ? hoverBack : back,
                                  SoftTheme.RadiusControl);
            TextRenderer.DrawText(g, label, SoftTheme.ActionFont, action,
                                  enabled ? fore : SoftTheme.TextMuted,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
            int arrowX = card.Right - Pad - SoftTheme.ScaleInt(12, Dpi);
            int arrowY = action.Top + action.Height / 2;
            using (var pen = new Pen(SoftTheme.TextMuted, 1.4f))
            {
                g.DrawLine(pen, arrowX - 6, arrowY, arrowX + 5, arrowY);
                g.DrawLine(pen, arrowX + 1, arrowY - 4, arrowX + 5, arrowY);
                g.DrawLine(pen, arrowX + 1, arrowY + 4, arrowX + 5, arrowY);
            }
        }

        private static bool IsDarkAction(Color back)
            => !back.IsLight();

        private (string Label, Color Back, Color Fore, bool Enabled) ActionAppearance()
        {
            switch (Status)
            {
                case ModCardStatus.Installed:
                    return (Properties.Resources.ModCardActionRemove,
                            SoftTheme.SurfaceSunken, SoftTheme.TextPrimary, true);
                case ModCardStatus.QueuedInstall:
                    return (Properties.Resources.ModCardActionQueued,
                            SoftTheme.AccentSoft, SoftTheme.AccentDeep, true);
                case ModCardStatus.QueuedRemove:
                    return (Properties.Resources.ModCardActionUndoRemove,
                            SoftTheme.DangerSoft, SoftTheme.Danger, true);
                case ModCardStatus.QueuedUpdate:
                    return (Properties.Resources.ModCardActionUpdating,
                            SoftTheme.AccentSoft, SoftTheme.AccentDeep, true);
                case ModCardStatus.AutoDetected:
                    return (Properties.Resources.ModCardActionAutoDetected,
                            SoftTheme.SurfaceSunken, SoftTheme.TextMuted, false);
                case ModCardStatus.Unavailable:
                    return (Properties.Resources.ModCardActionUnavailable,
                            SoftTheme.SurfaceSunken, SoftTheme.TextMuted, false);
                default:
                    return (Properties.Resources.ModCardActionInstall,
                            SoftTheme.Accent, SoftTheme.OnAccent, true);
            }
        }

        private static void DrawTrimmed(Graphics g, string text, Font font, Brush brush,
                                        Rectangle bounds, int maxLines)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrEmpty(text))
            {
                return;
            }
            var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Trimming     = StringTrimming.EllipsisWord,
                FormatFlags  = StringFormatFlags.NoClip,
                LineAlignment = StringAlignment.Near,
            };
            if (maxLines == 1)
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
            }
            var layout = maxLines > 1
                ? new RectangleF(bounds.X, bounds.Y, bounds.Width,
                                 Math.Min(bounds.Height, font.GetHeight(g) * maxLines))
                : new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            g.DrawString(text, font, brush, layout, format);
        }

        #endregion

        #region Interaction

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            hoverAction = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool overAction = ActionRect.Contains(e.Location) && IsActionEnabled();
            if (overAction != hoverAction)
            {
                hoverAction = overAction;
                Invalidate();
            }
            Cursor = overAction ? Cursors.Hand : Cursors.Default;
        }

        private bool IsActionEnabled()
            => Status != ModCardStatus.AutoDetected
               && Status != ModCardStatus.Unavailable;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                if (ActionRect.Contains(e.Location) && IsActionEnabled())
                {
                    ActionClicked?.Invoke(this);
                }
                else
                {
                    Activated?.Invoke(this);
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                ContextRequested?.Invoke(this);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && !ActionRect.Contains(e.Location))
            {
                ActionClicked?.Invoke(this);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                case Keys.Space:
                    ActionClicked?.Invoke(this);
                    e.Handled = true;
                    break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            CardFocused?.Invoke(this);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cover?.Dispose();
                cover = null;
                generatedCover?.Dispose();
                generatedCover = null;
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
