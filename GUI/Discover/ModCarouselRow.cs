using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// One horizontally scrolling row of mod cards, in the spirit of a
    /// streaming service's category shelf.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModCarouselRow : UserControl
    {
        private const int DesignHeaderHeight = 42;

        /// <summary>Disclosure glyphs, down when open and right when folded away.</summary>
        private const string ExpandedGlyph  = "\u25BE";
        private const string CollapsedGlyph = "\u25B8";

        private readonly Label            titleLabel;
        private readonly Label            countLabel;
        private readonly Button           collapseButton;
        private readonly Button           prevButton;
        private readonly Button           nextButton;
        private readonly Button           seeAllButton;
        private readonly BufferedFlowPanel strip;

        private readonly List<ModCard> cards = new List<ModCard>();
        private readonly bool showSeeAll;
        private bool collapsed;
        private bool expanded;
        private bool showDisclosure = true;
        private DiscoverDensity density = DiscoverDensity.Compact;

        private const int SbHorz = 0;
        private const int SbVert = 1;

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr handle, int bar, bool show);

        public ModCarouselRow(string sectionKey, string title, bool showSeeAll)
        {
            SectionKey    = sectionKey;
            AutoScaleMode = AutoScaleMode.None;
            this.showSeeAll = showSeeAll;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
                     true);
            BackColor = SoftTheme.Backdrop;
            Margin    = new Padding(0, 6, 0, 6);

            titleLabel = new Label
            {
                Text      = title,
                AutoSize  = true,
                Font      = SoftTheme.SectionFont,
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                Cursor    = Cursors.Hand,
            };

            countLabel = new Label
            {
                AutoSize  = true,
                Font      = SoftTheme.CardAuthorFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
            };

            // The disclosure sits to the left of the title, the way a disclosure
            // triangle reads on the platforms this is modelled on. The title
            // toggles too, so the hit target is the whole header text rather
            // than a 22 px square.
            collapseButton = new Button
            {
                Text      = ExpandedGlyph,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.CreateFont(9f, FontStyle.Regular),
                ForeColor = SoftTheme.TextMuted,
                BackColor = SoftTheme.Backdrop,
                TabStop   = false,
                Cursor    = Cursors.Hand,
            };
            collapseButton.FlatAppearance.BorderSize          = 0;
            collapseButton.FlatAppearance.MouseOverBackColor  = SoftTheme.SurfaceHover;
            collapseButton.FlatAppearance.MouseDownBackColor  = SoftTheme.AccentSoft;
            collapseButton.Click += (sender, e) => Toggle();
            titleLabel.Click     += (sender, e) =>
            {
                if (showDisclosure && !expanded)
                {
                    Toggle();
                }
            };

            prevButton = MakeNavButton("‹");
            nextButton = MakeNavButton("›");
            prevButton.Click += (sender, e) => ScrollBy(-StripPage());
            nextButton.Click += (sender, e) => ScrollBy(StripPage());

            seeAllButton = new Button
            {
                Text      = Properties.Resources.DiscoverSeeAll,
                Visible   = showSeeAll,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.ActionFont,
                ForeColor = SoftTheme.Accent,
                BackColor = SoftTheme.Backdrop,
                TabStop   = false,
                Cursor    = Cursors.Hand,
            };
            seeAllButton.FlatAppearance.BorderSize         = 0;
            seeAllButton.FlatAppearance.MouseOverBackColor  = SoftTheme.AccentSoft;
            seeAllButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            seeAllButton.Click += (sender, e) => SeeAllClicked?.Invoke(SectionKey);

            strip = new BufferedFlowPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = SoftTheme.Backdrop,
                Margin        = new Padding(0),
                Padding       = new Padding(0),
            };
            strip.HorizontalScroll.Enabled = true;
            strip.HorizontalScroll.Visible = false;
            strip.VerticalScroll.Enabled = false;
            strip.VerticalScroll.Visible = false;

            Controls.Add(strip);
            Controls.Add(titleLabel);
            Controls.Add(countLabel);
            Controls.Add(collapseButton);
            Controls.Add(prevButton);
            Controls.Add(nextButton);
            Controls.Add(seeAllButton);

            strip.MouseWheel += Strip_MouseWheel;
            strip.Scroll         += (sender, e) => UpdateNavButtons();
        }

        /// <summary>
        /// Whether the shelf is folded down to its header.
        /// </summary>
        public bool Collapsed => collapsed;

        /// <summary>
        /// The concept-art shell uses plain shelf headings. The disclosure is
        /// retained for the older embedded control, but can be removed without
        /// changing the row's data or interaction model.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowDisclosure
        {
            get => showDisclosure;
            set
            {
                if (showDisclosure == value)
                {
                    return;
                }
                showDisclosure = value;
                collapseButton.Visible = value && !collapsed && !expanded;
                titleLabel.Cursor = value && !expanded ? Cursors.Hand : Cursors.Default;
                strip.HorizontalScroll.Visible = false;
                strip.VerticalScroll.Visible = false;
                UpdateCardMargins();
                LayoutChildren();
            }
        }

        /// <summary>
        /// Switch the shelf from a horizontal carousel to the flat, wrapping
        /// gallery used by the concept art's "See all" view.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Expanded => expanded;

        public void SetExpanded(bool value)
        {
            if (expanded == value)
            {
                return;
            }
            expanded = value;
            if (expanded)
            {
                collapsed = false;
                strip.WrapContents = true;
                strip.Visible = true;
                strip.AutoScroll = false;
                strip.HorizontalScroll.Enabled = false;
                strip.HorizontalScroll.Visible = false;
                strip.VerticalScroll.Enabled = false;
                strip.VerticalScroll.Visible = false;
            }
            else
            {
                strip.WrapContents = false;
                strip.AutoScroll = true;
                strip.HorizontalScroll.Enabled = true;
                strip.HorizontalScroll.Visible = false;
                strip.VerticalScroll.Enabled = true;
                strip.VerticalScroll.Visible = false;
                strip.Visible = !collapsed;
            }
            collapseButton.Visible = showDisclosure && !collapsed && !expanded;
            titleLabel.Cursor = showDisclosure && !expanded ? Cursors.Hand : Cursors.Default;
            prevButton.Visible = !collapsed && !expanded;
            nextButton.Visible = !collapsed && !expanded;
            seeAllButton.Visible = !collapsed && !expanded && showSeeAll;
            LayoutChildren();
        }

        /// <summary>
        /// Raised when the user folds or unfolds the shelf, so the owner can
        /// remember the choice.
        /// </summary>
        public event Action<string, bool>? CollapsedChanged;

        /// <summary>
        /// Fold or unfold the shelf. The owner drives this as well as the
        /// disclosure button, because the view rebuilds every row from scratch
        /// on each filter or sort change and has to reapply the state.
        /// </summary>
        public void SetCollapsed(bool value, bool notify = true)
        {
            if (collapsed == value)
            {
                return;
            }
            collapsed = value;
            collapseButton.Text = collapsed ? CollapsedGlyph : ExpandedGlyph;

            // The header stays: the title and the count are a useful summary of
            // what was folded away.
            strip.Visible        = !collapsed;
            collapseButton.Visible = showDisclosure && !collapsed && !expanded;
            titleLabel.Cursor    = showDisclosure && !expanded ? Cursors.Hand : Cursors.Default;
            prevButton.Visible   = !collapsed && !expanded;
            nextButton.Visible   = !collapsed && !expanded;
            seeAllButton.Visible = !collapsed && !expanded && showSeeAll;

            LayoutChildren();
            if (notify)
            {
                CollapsedChanged?.Invoke(SectionKey, collapsed);
            }
        }

        private void Toggle() => SetCollapsed(!collapsed);

        private float cachedDpi;

        /// <summary>
        /// Height of the shelf header strip, in the same DPI-scaled units as the
        /// cards, so the strip is not left floating below a too-tall row.
        /// </summary>
        private int HeaderHeight => SoftTheme.ScaleInt(DesignHeaderHeight, Dpi);

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

        /// <summary>
        /// The wheel scrolls the shelf sideways while there is more to see,
        /// then hands the event back to the page so the catalogue still
        /// scrolls vertically. This avoids trapping the user in a row.
        /// </summary>
        private void Strip_MouseWheel(object? sender, MouseEventArgs e)
        {
            int max = ScrollMaximum;
            int current = -strip.AutoScrollPosition.X;
            bool canScroll = e.Delta < 0 ? current < max : current > 0;
            if (canScroll)
            {
                if (e is HandledMouseEventArgs handled)
                {
                    handled.Handled = true;
                }
                ScrollBy(e.Delta < 0 ? DesignScroll : -DesignScroll);
            }
        }

        public string SectionKey { get; }

        public IReadOnlyList<ModCard> Cards => cards;

        public DiscoverDensity Density => density;

        public void SetDensity(DiscoverDensity value)
        {
            if (density == value)
            {
                return;
            }
            density = value;
            foreach (var card in cards)
            {
                card.SetDensity(value);
            }
            LayoutChildren();
        }

        public event Action<ModCard>?    ModActivated;
        public event Action<ModCard>?    ModActionClicked;
        public event Action<ModCard>?    ModContextRequested;
        public event Action<string>?     SeeAllClicked;

        private Button MakeNavButton(string glyph)
        {
            var button = new Button
            {
                Text      = glyph,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.CreateFont(12f, FontStyle.Bold),
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                Size      = new Size(SoftTheme.ScaleInt(40, Dpi), SoftTheme.ScaleInt(40, Dpi)),
                TabStop   = false,
                Cursor    = Cursors.Hand,
            };
            button.Text = "";
            button.FlatAppearance.BorderSize         = 0;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.Resize += (sender, e) =>
            {
                button.Region?.Dispose();
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddEllipse(0, 0, Math.Max(1, button.Width - 1),
                                    Math.Max(1, button.Height - 1));
                    button.Region = new Region(path);
                }
            };
            button.Paint += (sender, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var bounds = new Rectangle(0, 0, Math.Max(1, button.Width - 1),
                                            Math.Max(1, button.Height - 1));
                var back = !button.Enabled ? SoftTheme.SurfaceSunken
                         : button.Focused ? SoftTheme.AccentSoft : SoftTheme.SurfaceRaised;
                SoftTheme.FillRounded(g, bounds, back, bounds.Width / 2);
                SoftTheme.DrawRounded(g, bounds,
                                      button.Enabled ? SoftTheme.BorderStrong : SoftTheme.Border,
                                      bounds.Width / 2, 1f);
                TextRenderer.DrawText(g, glyph, button.Font, bounds,
                                      button.Enabled ? SoftTheme.TextPrimary : SoftTheme.TextMuted,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPadding);
            };
            button.MouseEnter += (sender, e) => button.Invalidate();
            button.MouseLeave += (sender, e) => button.Invalidate();
            return button;
        }

        /// <summary>
        /// Replace the contents of the row. Passing an empty sequence hides the row.
        /// </summary>
        public void SetMods(IReadOnlyList<GUIMod> mods)
        {
            SuspendLayout();
            try
            {
                foreach (var card in cards)
                {
                    strip.Controls.Remove(card);
                    card.Dispose();
                }
                cards.Clear();

                foreach (var mod in mods)
                {
                    var card = new ModCard(mod);
                    card.SetDensity(density);
                    ApplyCardMargin(card);
                    card.Activated        += c => ModActivated?.Invoke(c);
                    card.ActionClicked    += c => ModActionClicked?.Invoke(c);
                    card.ContextRequested += c => ModContextRequested?.Invoke(c);
                    card.CardFocused      += c => EnsureVisible(c);
                    cards.Add(card);
                    strip.Controls.Add(card);
                }

                countLabel.Text = string.Format(Properties.Resources.DiscoverModsCount,
                                                mods.Count);
                var show = mods.Count > 0;
                Visible = show;
                titleLabel.Visible     = show;
                countLabel.Visible     = show;
                collapseButton.Visible = show && showDisclosure && !expanded;

                // A FlowLayoutPanel keeps its scroll offset across content
                // replacement, which would leave the new cards shifted off-view.
                strip.AutoScrollPosition = new Point(0, 0);
            }
            finally
            {
                ResumeLayout(true);
            }

            LayoutChildren();
            UpdateNavButtons();
        }

        private void ApplyCardMargin(ModCard card)
        {
            if (showDisclosure)
            {
                return;
            }
            int dpi = (int)Dpi;
            card.Margin = new Padding(0,
                                      SoftTheme.ScaleInt(4, dpi),
                                      SoftTheme.ScaleInt(16, dpi),
                                      SoftTheme.ScaleInt(14, dpi));
        }

        private void UpdateCardMargins()
        {
            foreach (var card in cards)
            {
                if (showDisclosure)
                {
                    card.Margin = new Padding(SoftTheme.ScaleInt(6, Dpi),
                                              SoftTheme.ScaleInt(4, Dpi),
                                              SoftTheme.ScaleInt(6, Dpi),
                                              SoftTheme.ScaleInt(12, Dpi));
                }
                else
                {
                    ApplyCardMargin(card);
                }
            }
        }

        /// <summary>
        /// Re-evaluate the change-set badges without rebuilding the cards.
        /// </summary>
        public void RefreshStatuses(Func<GUIMod, ModCardStatus> statusFor)
        {
            foreach (var card in cards)
            {
                card.Status = statusFor(card.Mod);
            }
        }

        public void Highlight(GUIMod? mod)
        {
            foreach (var card in cards)
            {
                card.IsSelected = mod != null && card.Mod.Identifier == mod.Identifier;
            }
        }

        private int StripPage()
            => Math.Max(DesignScroll, strip.ClientSize.Width - DesignScroll);

        private int DesignScroll => SoftTheme.ScaleInt(ModCard.WidthFor(density) + 12, Dpi);
        private int ScrollMaximum => cards.Count == 0 ? 0 : Math.Max(0,
            cards[cards.Count - 1].Right - strip.AutoScrollPosition.X
            + strip.Padding.Right - strip.ClientSize.Width);

        private void EnsureVisible(ModCard card)
        {
            var bounds = card.Bounds;
            bounds.Offset(-strip.AutoScrollPosition.X, 0);
            int viewLeft = -strip.AutoScrollPosition.X;
            int viewRight = viewLeft + strip.ClientSize.Width;
            if (bounds.Left < viewLeft)
            {
                ScrollTo(bounds.Left);
            }
            else if (bounds.Right > viewRight)
            {
                ScrollTo(bounds.Right - strip.ClientSize.Width);
            }
        }

        private void ScrollTo(int x)
        {
            int max = ScrollMaximum;
            int target = Math.Max(0, Math.Min(max, x));
            strip.AutoScrollPosition = new Point(target, 0);
            UpdateNavButtons();
        }

        private void ScrollBy(int dx)
        {
            int current = -strip.AutoScrollPosition.X;
            ScrollTo(current + dx);
        }

        private void UpdateNavButtons()
        {
            if (expanded)
            {
                prevButton.Enabled = false;
                nextButton.Enabled = false;
                return;
            }
            int max = ScrollMaximum;
            int current = -strip.AutoScrollPosition.X;
            prevButton.Enabled = current > 0;
            nextButton.Enabled = current < max;
            prevButton.Visible = !collapsed && max > 0 && current > 0;
            nextButton.Visible = !collapsed && max > 0 && current < max;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            // The shelf needs room for the header, a full card plus its margins and
            // the horizontal scrollbar. Folded away it is just the header.
            // Setting Height re-enters this method once; the second pass then
            // positions everything.
            int desiredHeight;
            if (collapsed)
            {
                desiredHeight = HeaderHeight;
            }
            else if (expanded)
            {
                int cardWidth = SoftTheme.ScaleInt(ModCard.WidthFor(density), Dpi);
                int cardHeight = SoftTheme.ScaleInt(ModCard.HeightFor(density), Dpi);
                int cardGap = SoftTheme.ScaleInt(12, Dpi);
                int columns = Math.Max(1, (Width - cardGap) / (cardWidth + cardGap));
                int lineCount = Math.Max(1, (cards.Count + columns - 1) / columns);
                desiredHeight = HeaderHeight
                              + (lineCount * (cardHeight + SoftTheme.ScaleInt(16, Dpi)))
                              + SoftTheme.ScaleInt(10, Dpi);
            }
            else
            {
                desiredHeight = HeaderHeight
                              + SoftTheme.ScaleInt(ModCard.HeightFor(density), Dpi)
                              + SoftTheme.ScaleInt(16, Dpi);
            }
            if (Height != desiredHeight)
            {
                Height = desiredHeight;
                return;
            }

            int headerPad = SoftTheme.ScaleInt(showDisclosure ? 4 : 30, Dpi);
            titleLabel.Location = new Point(headerPad
                                            + (showDisclosure ? SoftTheme.ScaleInt(24, Dpi) : 0),
                                            SoftTheme.ScaleInt(9, Dpi));

            int chevron = SoftTheme.ScaleInt(22, Dpi);
            collapseButton.Size = new Size(chevron, chevron);
            collapseButton.Location = new Point(headerPad,
                                                titleLabel.Top
                                                + ((titleLabel.Height - chevron) / 2));

            countLabel.Location = new Point(titleLabel.Right + SoftTheme.ScaleInt(8, Dpi),
                                            titleLabel.Top + titleLabel.Padding.Top
                                            + SoftTheme.ScaleInt(4, Dpi));

            int right = Width - headerPad;
            if (seeAllButton.Visible)
            {
                seeAllButton.Size = new Size(TextRenderer.MeasureText(seeAllButton.Text,
                                                                     seeAllButton.Font).Width
                                             + SoftTheme.ScaleInt(20, Dpi),
                                             SoftTheme.ScaleInt(26, Dpi));
                seeAllButton.Location = new Point(right - seeAllButton.Width,
                                                  SoftTheme.ScaleInt(8, Dpi));
                right = seeAllButton.Left - SoftTheme.ScaleInt(8, Dpi);
            }

            int navTop = strip.Top + Math.Max(0, (strip.Height - nextButton.Height) / 2);
            nextButton.Location = new Point(right - nextButton.Width, navTop);
            right = nextButton.Left - SoftTheme.ScaleInt(4, Dpi);
            prevButton.Location = new Point(right - prevButton.Width, navTop);

            int gutter = showDisclosure ? 0 : SoftTheme.ScaleInt(30, Dpi);
            strip.Padding = new Padding(gutter, 0, gutter, 0);
            strip.Location = new Point(0, HeaderHeight);
            strip.Size     = new Size(Width, Math.Max(1, Height - HeaderHeight));
            int navSize = SoftTheme.ScaleInt(40, Dpi);
            int navY = strip.Top + (strip.Height - navSize) / 2;
            prevButton.SetBounds(SoftTheme.ScaleInt(12, Dpi), navY, navSize, navSize);
            nextButton.SetBounds(Width - navSize - SoftTheme.ScaleInt(12, Dpi), navY, navSize, navSize);
            prevButton.BringToFront();
            nextButton.BringToFront();
            UpdateNavButtons();
        }

        private static void HideNativeScrollbars(BufferedFlowPanel panel)
        {
            if (!panel.IsHandleCreated)
            {
                return;
            }
            panel.HorizontalScroll.Visible = false;
            panel.VerticalScroll.Visible = false;
            ShowScrollBar(panel.Handle, SbHorz, false);
            ShowScrollBar(panel.Handle, SbVert, false);
        }

        /// <summary>
        /// A FlowLayoutPanel that doesn't flicker while scrolling.
        /// </summary>
        private sealed class BufferedFlowPanel : FlowLayoutPanel
        {
            protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if ((ModifierKeys & Keys.Shift) == 0)
                {
                    for (Control? ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
                    {
                        if (ancestor is ScrollableControl page && page.AutoScroll)
                        {
                            int maximum = Math.Max(0, page.DisplayRectangle.Height - page.ClientSize.Height);
                            int step = -e.Delta * SoftTheme.Px(60) / 120;
                            int position = Math.Max(0, Math.Min(maximum, -page.AutoScrollPosition.Y + step));
                            page.AutoScrollPosition = new Point(0, position);
                            if (e is HandledMouseEventArgs handled) handled.Handled = true;
                            return;
                        }
                    }
                }
                base.OnMouseWheel(e);
            }
            public BufferedFlowPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint, true);
                DoubleBuffered = true;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                HideNativeScrollbars(this);
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                HideNativeScrollbars(this);
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                HideNativeScrollbars(this);
            }
        }
    }
}
