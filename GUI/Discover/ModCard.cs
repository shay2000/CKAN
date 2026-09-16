using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        public const int DesignWidth  = 216;
        public const int DesignHeight = 316;

        public ModCard(GUIMod mod)
        {
            Mod = mod;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop   = true;
            Size      = new Size(DesignWidth, DesignHeight);
            Margin    = new Padding(6, 4, 6, 12);
            BackColor = SoftTheme.Backdrop;
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

        #region Layout

        private int Pad           => SoftTheme.ScaleInt(12, Dpi);
        private int Radius        => SoftTheme.ScaleInt(SoftTheme.RadiusCard, Dpi);
        private int ShadowSpread  => SoftTheme.ScaleInt(4, Dpi);
        private int CoverHeight   => SoftTheme.ScaleInt(134, Dpi);
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
                int w = SoftTheme.ScaleInt(104, Dpi);
                return new Rectangle(card.Right - Pad - w,
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
                int titleHeight = (int)Math.Ceiling(titleFont.GetHeight(g) * 2f);
                var titleRect = new Rectangle(contentLeft, y, contentWidth, titleHeight);
                DrawTrimmed(g, Mod.Name, titleFont, titleBrush, titleRect, 2);
                y = titleRect.Bottom;

                var authorText = string.Join(", ", Mod.Authors);
                if (authorText.Length > 0)
                {
                    int authorHeight = (int)Math.Ceiling(authorFont.GetHeight(g));
                    var authorRect = new Rectangle(contentLeft, y, contentWidth, authorHeight);
                    string? downloads = Mod.DownloadCount.HasValue
                        ? string.Format(Properties.Resources.DiscoverDownloads, Mod.DownloadCount.Value)
                        : null;
                    int downloadsWidth = downloads == null
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(downloads, authorFont).Width);
                    int authorWidth = downloads == null
                        ? contentWidth
                        : Math.Max(0, contentWidth - downloadsWidth - SoftTheme.ScaleInt(8, Dpi));
                    DrawTrimmed(g, authorText, authorFont, secondaryBrush,
                                new Rectangle(authorRect.X, authorRect.Y, authorWidth, authorRect.Height), 1);
                    if (downloads != null && authorWidth > 0)
                    {
                        TextRenderer.DrawText(g, downloads, authorFont,
                                              new Point(contentLeft + contentWidth - downloadsWidth,
                                                        authorRect.Y),
                                              SoftTheme.TextMuted,
                                              TextFormatFlags.NoPadding);
                    }
                    y = authorRect.Bottom + SoftTheme.ScaleInt(3, Dpi);
                }

                int bodySpace = actionTop - SoftTheme.ScaleInt(8, Dpi) - y;
                int lineHeight = (int)Math.Ceiling(bodyFont.GetHeight(g));
                if (bodySpace >= lineHeight)
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

                if (cover is Image artwork && artwork.Width > 0 && artwork.Height > 0)
                {
                    DrawImageCover(g, artwork, area);
                }
                else
                {
                    // Neutral shimmer while the real artwork is on its way
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
                yield return (Properties.Resources.ModCardBadgeInstalled, SoftTheme.SuccessSoft, SoftTheme.Success);
            }
        }

        private void DrawFooter(Graphics g, Rectangle card, int actionTop, int contentLeft)
        {
            var pillFont = SoftTheme.PillFont;
            var version = Mod.InstalledVersion ?? Mod.LatestVersion;
            if (version.Length > 0 && version != "-")
            {
                var rect = new Rectangle(contentLeft, actionTop,
                                         SoftTheme.ScaleInt(84, Dpi), ActionHeight);
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceSunken, SoftTheme.RadiusControl);
                TextRenderer.DrawText(g, version, pillFont, rect, SoftTheme.TextSecondary,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
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
        }

        private static bool IsDarkAction(Color back)
            => !back.IsLight();

        private (string Label, Color Back, Color Fore, bool Enabled) ActionAppearance()
        {
            switch (Status)
            {
                case ModCardStatus.Installed:
                    return (Properties.Resources.ModCardActionRemove,
                            SoftTheme.SurfaceSunken, SoftTheme.Danger, true);
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

        #endregion
    }
}
