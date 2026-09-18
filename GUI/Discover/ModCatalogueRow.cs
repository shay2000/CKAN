using System;
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
    /// A native, owner-drawn catalogue row used by Discover's List and Table
    /// densities. It deliberately raises events instead of touching GUIMod so
    /// ManageMods remains the only owner of the CKAN changeset.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModCatalogueRow : Control
    {
        private const int DesignHeight = 58;
        private readonly bool tableMode;
        private readonly bool updateMode;
        private ModCardStatus status = ModCardStatus.NotInstalled;
        private bool hovered;
        private bool hoverAction;
        private bool isSelected;
        private float cachedDpi;

        public ModCatalogueRow(GUIMod mod, bool tableMode, bool updateMode = false)
        {
            Mod = mod;
            this.tableMode = tableMode;
            this.updateMode = updateMode;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop = true;
            Height = SoftTheme.ScaleInt(updateMode ? 70 : DesignHeight, Dpi);
            Margin = new Padding(0, 0, 0, SoftTheme.ScaleInt(2, Dpi));
            BackColor = SoftTheme.Backdrop;
        }

        public GUIMod Mod { get; }

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

        public event Action<ModCatalogueRow>? Activated;
        public event Action<ModCatalogueRow>? ActionClicked;
        public event Action<ModCatalogueRow>? ContextRequested;
        public event Action<ModCatalogueRow>? RowFocused;

        private float Dpi
        {
            get
            {
                if (cachedDpi <= 0f)
                {
                    try
                    {
                        using (var g = CreateGraphics())
                        {
                            cachedDpi = g.DpiX > 0 ? g.DpiX : 96f;
                        }
                    }
                    catch
                    {
                        cachedDpi = 96f;
                    }
                }
                return cachedDpi;
            }
        }

        private int Pad => SoftTheme.ScaleInt(14, Dpi);

        private Rectangle RowRect
            => new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        private Rectangle ActionRect
        {
            get
            {
                int width = SoftTheme.ScaleInt(92, Dpi);
                return new Rectangle(Math.Max(Pad, Width - Pad - width),
                                     (Height - SoftTheme.ScaleInt(32, Dpi)) / 2,
                                     width, SoftTheme.ScaleInt(32, Dpi));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);

            var row = RowRect;
            if (updateMode)
            {
                PaintUpdateRow(g, row);
                return;
            }
            var surface = IsSelected ? SoftTheme.AccentSoft
                         : hovered && !tableMode ? SoftTheme.SurfaceHover
                         : SoftTheme.Surface;
            if (tableMode)
            {
                using (var brush = new SolidBrush(surface))
                {
                    g.FillRectangle(brush, row);
                }
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    g.DrawLine(pen, row.Left, row.Bottom, row.Right, row.Bottom);
                }
                if (IsSelected)
                {
                    using (var accent = new SolidBrush(SoftTheme.Accent))
                    {
                        g.FillRectangle(accent, row.Left, row.Top, SoftTheme.ScaleInt(3, Dpi), row.Height);
                    }
                }
            }
            else
            {
                SoftTheme.FillRounded(g, row, surface, SoftTheme.RadiusControl);
                SoftTheme.DrawRounded(g, row,
                                      IsSelected || Focused ? SoftTheme.Accent : SoftTheme.Border,
                                      SoftTheme.RadiusControl, Focused ? 1.5f : 1f);
            }

            int centerY = row.Top + row.Height / 2;
            int tileSize = SoftTheme.ScaleInt(tableMode ? 30 : 38, Dpi);
            var tile = new Rectangle(Pad, centerY - tileSize / 2, tileSize, tileSize);
            var seed = ModArtGenerator.SeedColor(Mod.Identifier);
            SoftTheme.FillRounded(g, tile, seed, SoftTheme.ScaleInt(tableMode ? 7 : 10, Dpi));
            TextRenderer.DrawText(g, ModArtGenerator.InitialsForName(Mod.Name),
                                  SoftTheme.PillFont, tile, Color.White,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.NoPadding);

            int left = tile.Right + SoftTheme.ScaleInt(12, Dpi);
            int actionLeft = ActionRect.Left;
            int right = actionLeft - SoftTheme.ScaleInt(18, Dpi);
            int statusWidth = SoftTheme.ScaleInt(86, Dpi);
            int versionWidth = SoftTheme.ScaleInt(115, Dpi);
            int sizeWidth = SoftTheme.ScaleInt(82, Dpi);
            int categoryWidth = SoftTheme.ScaleInt(96, Dpi);
            int columnGap = SoftTheme.ScaleInt(8, Dpi);
            int statusLeft = right - statusWidth;
            int versionLeft = statusLeft - columnGap - versionWidth;
            int sizeLeft = versionLeft - columnGap - sizeWidth;
            int categoryLeft = sizeLeft - columnGap - categoryWidth;
            int nameWidth = Math.Max(60, categoryLeft - left - SoftTheme.ScaleInt(14, Dpi));
            using (var nameBrush = new SolidBrush(SoftTheme.TextPrimary))
            using (var secondaryBrush = new SolidBrush(SoftTheme.TextSecondary))
            using (var mutedBrush = new SolidBrush(SoftTheme.TextMuted))
            {
                TextRenderer.DrawText(g, Mod.Name, SoftTheme.CardTitleFont,
                                      new Rectangle(left, centerY - SoftTheme.ScaleInt(16, Dpi),
                                                    nameWidth, SoftTheme.ScaleInt(19, Dpi)),
                                      nameBrush.Color,
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                string authors = string.Join(", ", Mod.Authors);
                TextRenderer.DrawText(g, authors, SoftTheme.CardAuthorFont,
                                      new Rectangle(left, centerY + SoftTheme.ScaleInt(3, Dpi),
                                                    nameWidth, SoftTheme.ScaleInt(16, Dpi)),
                                      secondaryBrush.Color,
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                if (tableMode)
                {
                    TextRenderer.DrawText(g, ModDiscoverView.CategoryLabel(ModDiscoverView.CategoryFor(Mod)),
                                          SoftTheme.MetaFont, new Rectangle(categoryLeft, centerY - 8, categoryWidth, 18),
                                          secondaryBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Mod.DownloadSize, SoftTheme.MetaFont,
                                          new Rectangle(sizeLeft, centerY - 8, sizeWidth, 18),
                                          secondaryBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, DisplayVersion(), SoftTheme.MetaFont,
                                          new Rectangle(versionLeft, centerY - 8, versionWidth, 18),
                                          nameBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, StatusText(), SoftTheme.PillFont,
                                          new Rectangle(statusLeft, centerY - 8, statusWidth, 18),
                                          StatusForeground(), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                else
                {
                    TextRenderer.DrawText(g, ModDiscoverView.CategoryLabel(ModDiscoverView.CategoryFor(Mod)),
                                          SoftTheme.MetaFont, new Rectangle(categoryLeft, centerY - 8, categoryWidth, 18),
                                          mutedBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Mod.DownloadSize, SoftTheme.MetaFont,
                                          new Rectangle(sizeLeft, centerY - 8, sizeWidth, 18),
                                          mutedBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, DisplayVersion(), SoftTheme.MetaFont,
                                          new Rectangle(versionLeft, centerY - 8, versionWidth, 18),
                                          secondaryBrush.Color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, StatusText(), SoftTheme.PillFont,
                                          new Rectangle(statusLeft, centerY - 8, statusWidth, 18),
                                          StatusForeground(), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }

            var action = ActionAppearance();
            var actionRect = ActionRect;
            SoftTheme.FillRounded(g, actionRect,
                                  !action.Enabled ? SoftTheme.SurfaceSunken
                                  : hoverAction ? SoftTheme.Mix(action.Back, SoftTheme.TextPrimary,
                                                                action.Back.IsLight() ? 0.06f : 0.1f)
                                  : action.Back,
                                  SoftTheme.RadiusControl);
            TextRenderer.DrawText(g, action.Label, SoftTheme.ActionFont, actionRect,
                                  action.Enabled ? action.Fore : SoftTheme.TextMuted,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
        }

        private void PaintUpdateRow(Graphics g, Rectangle row)
        {
            var surface = IsSelected ? SoftTheme.AccentSoft
                         : hovered ? SoftTheme.SurfaceHover
                                   : SoftTheme.Surface;
            SoftTheme.FillRounded(g, row, surface, SoftTheme.RadiusControl);
            SoftTheme.DrawRounded(g, row,
                                  IsSelected || Focused ? SoftTheme.Accent : SoftTheme.Border,
                                  SoftTheme.RadiusControl, Focused ? 1.5f : 1f);

            int pad = SoftTheme.ScaleInt(14, Dpi);
            int tileSize = SoftTheme.ScaleInt(42, Dpi);
            var tile = new Rectangle(pad, (row.Height - tileSize) / 2, tileSize, tileSize);
            SoftTheme.FillRounded(g, tile, ModArtGenerator.SeedColor(Mod.Identifier),
                                  SoftTheme.ScaleInt(11, Dpi));
            TextRenderer.DrawText(g, ModArtGenerator.InitialsForName(Mod.Name),
                                  SoftTheme.PillFont, tile, Color.White,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.NoPadding);

            int actionLeft = ActionRect.Left;
            int right = actionLeft - SoftTheme.ScaleInt(18, Dpi);
            int columnGap = SoftTheme.ScaleInt(12, Dpi);
            int sizeWidth = SoftTheme.ScaleInt(74, Dpi);
            int versionWidth = SoftTheme.ScaleInt(88, Dpi);
            int installedLeft = right - sizeWidth - columnGap - versionWidth;
            int latestLeft = installedLeft + versionWidth + columnGap;
            int nameLeft = tile.Right + SoftTheme.ScaleInt(14, Dpi);
            int nameWidth = Math.Max(80, installedLeft - nameLeft - columnGap);
            int centerY = row.Top + row.Height / 2;

            TextRenderer.DrawText(g, Mod.Name, SoftTheme.CardTitleFont,
                                  new Rectangle(nameLeft, centerY - SoftTheme.ScaleInt(18, Dpi),
                                                nameWidth, SoftTheme.ScaleInt(20, Dpi)),
                                  SoftTheme.TextPrimary,
                                  TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string author = string.Join(", ", Mod.Authors);
            string release = string.Format(Properties.Resources.DiscoverReleased,
                                           RelativeReleaseDate());
            TextRenderer.DrawText(g, author + "  \u00B7  " + release,
                                  SoftTheme.MetaFont,
                                  new Rectangle(nameLeft, centerY + SoftTheme.ScaleInt(4, Dpi),
                                                nameWidth, SoftTheme.ScaleInt(18, Dpi)),
                                  SoftTheme.TextMuted,
                                  TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, Mod.InstalledVersion ?? Mod.Version,
                                  SoftTheme.MetaFont,
                                  new Rectangle(installedLeft, centerY - SoftTheme.ScaleInt(9, Dpi),
                                                versionWidth, SoftTheme.ScaleInt(18, Dpi)),
                                  SoftTheme.TextMuted,
                                  TextFormatFlags.Right | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Mod.LatestVersion,
                                  SoftTheme.MetaFont,
                                  new Rectangle(latestLeft, centerY - SoftTheme.ScaleInt(9, Dpi),
                                                versionWidth, SoftTheme.ScaleInt(18, Dpi)),
                                  SoftTheme.Accent,
                                  TextFormatFlags.Right | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Mod.DownloadSize,
                                  SoftTheme.MetaFont,
                                  new Rectangle(right - sizeWidth, centerY - SoftTheme.ScaleInt(9, Dpi),
                                                sizeWidth, SoftTheme.ScaleInt(18, Dpi)),
                                  SoftTheme.TextMuted,
                                  TextFormatFlags.Right | TextFormatFlags.NoPadding);

            var action = ActionAppearance();
            var actionRect = ActionRect;
            Color actionBack = !action.Enabled ? SoftTheme.SurfaceSunken
                             : hoverAction ? SoftTheme.Mix(action.Back, SoftTheme.TextPrimary,
                                                           action.Back.IsLight() ? 0.06f : 0.1f)
                                           : action.Back;
            SoftTheme.FillRounded(g, actionRect, actionBack, SoftTheme.RadiusControl);
            TextRenderer.DrawText(g, action.Label, SoftTheme.ActionFont, actionRect,
                                  action.Enabled ? action.Fore : SoftTheme.TextMuted,
                                  TextFormatFlags.HorizontalCenter
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
        }

        private string RelativeReleaseDate()
        {
            DateTime? release = Mod.LatestCompatibleMod?.release_date
                              ?? Mod.LatestAvailableMod?.release_date
                              ?? Mod.Module.release_date;
            if (!release.HasValue)
            {
                return Properties.Resources.GUIModUnknown;
            }

            int days = (DateTime.Now.Date - release.Value.ToLocalTime().Date).Days;
            if (days <= 0)
            {
                return Properties.Resources.DiscoverToday;
            }
            if (days == 1)
            {
                return Properties.Resources.DiscoverYesterday;
            }
            if (days < 7)
            {
                return string.Format(Properties.Resources.DiscoverDaysAgo, days);
            }
            if (days < 56)
            {
                return string.Format(Properties.Resources.DiscoverWeeksAgo, days / 7);
            }
            return release.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        private string DisplayVersion()
        {
            if (Mod.HasUpdate && !string.IsNullOrWhiteSpace(Mod.InstalledVersion))
            {
                return string.Format(Properties.Resources.DiscoverVersionChange,
                                     Mod.InstalledVersion, Mod.LatestVersion);
            }
            return Mod.Version;
        }

        private string StatusText()
        {
            switch (Status)
            {
                case ModCardStatus.QueuedInstall:
                case ModCardStatus.QueuedUpdate:
                    return Properties.Resources.ModCardBadgeQueued;
                case ModCardStatus.QueuedRemove:
                    return Properties.Resources.ModCardBadgeRemoving;
                case ModCardStatus.AutoDetected:
                    return Properties.Resources.ModCardBadgeAutoDetected;
                case ModCardStatus.Unavailable:
                    return Properties.Resources.ModCardBadgeIncompatible;
                case ModCardStatus.Installed:
                    return Properties.Resources.ModCardBadgeInstalled;
                default:
                    return Properties.Resources.ModCardActionInstall;
            }
        }

        private Color StatusForeground()
        {
            switch (Status)
            {
                case ModCardStatus.QueuedInstall:
                case ModCardStatus.QueuedUpdate:
                    return SoftTheme.AccentDeep;
                case ModCardStatus.QueuedRemove:
                case ModCardStatus.Unavailable:
                    return SoftTheme.Danger;
                case ModCardStatus.AutoDetected:
                    return SoftTheme.TextSecondary;
                case ModCardStatus.Installed:
                    return SoftTheme.Success;
                default:
                    return SoftTheme.TextMuted;
            }
        }

        private (string Label, Color Back, Color Fore, bool Enabled) ActionAppearance()
        {
            if (updateMode && Status == ModCardStatus.Installed && Mod.HasUpdate)
            {
                return (Properties.Resources.ModernShellUpdate, SoftTheme.Accent,
                        SoftTheme.OnAccent, true);
            }
            switch (Status)
            {
                case ModCardStatus.Installed:
                    return (Properties.Resources.ModCardActionRemove, SoftTheme.SurfaceSunken, SoftTheme.Danger, true);
                case ModCardStatus.QueuedInstall:
                case ModCardStatus.QueuedUpdate:
                    return (Properties.Resources.ModCardActionQueued, SoftTheme.AccentSoft, SoftTheme.AccentDeep, true);
                case ModCardStatus.QueuedRemove:
                    return (Properties.Resources.ModCardActionUndoRemove, SoftTheme.DangerSoft, SoftTheme.Danger, true);
                case ModCardStatus.AutoDetected:
                    return (Properties.Resources.ModCardActionAutoDetected, SoftTheme.SurfaceSunken, SoftTheme.TextMuted, false);
                case ModCardStatus.Unavailable:
                    return (Properties.Resources.ModCardActionUnavailable, SoftTheme.SurfaceSunken, SoftTheme.TextMuted, false);
                default:
                    return (Properties.Resources.ModCardActionInstall, SoftTheme.Accent, SoftTheme.OnAccent, true);
            }
        }

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
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var overAction = ActionRect.Contains(e.Location) && ActionAppearance().Enabled;
            if (overAction != hoverAction)
            {
                hoverAction = overAction;
                Invalidate();
            }
            Cursor = overAction ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                if (ActionRect.Contains(e.Location) && ActionAppearance().Enabled)
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                if (ActionAppearance().Enabled)
                {
                    ActionClicked?.Invoke(this);
                }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RowFocused?.Invoke(this);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }
    }
}
