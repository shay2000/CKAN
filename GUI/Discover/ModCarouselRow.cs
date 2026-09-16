using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
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
        private const int HeaderHeight = 42;

        private readonly Label            titleLabel;
        private readonly Label            countLabel;
        private readonly Button           prevButton;
        private readonly Button           nextButton;
        private readonly Button           seeAllButton;
        private readonly BufferedFlowPanel strip;

        private readonly List<ModCard> cards = new List<ModCard>();

        public ModCarouselRow(string sectionKey, string title, bool showSeeAll)
        {
            SectionKey = sectionKey;
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
            };

            countLabel = new Label
            {
                AutoSize  = true,
                Font      = SoftTheme.CardAuthorFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
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
            strip.HorizontalScroll.Visible = true;

            Controls.Add(strip);
            Controls.Add(titleLabel);
            Controls.Add(countLabel);
            Controls.Add(prevButton);
            Controls.Add(nextButton);
            Controls.Add(seeAllButton);

            strip.MouseWheel += Strip_MouseWheel;
            strip.Scroll         += (sender, e) => UpdateNavButtons();
        }

        private float cachedDpi;

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
            int max = Math.Max(0, strip.HorizontalScroll.Maximum - strip.ClientSize.Width + 1);
            int current = -strip.AutoScrollPosition.X;
            bool canScroll = e.Delta < 0 ? current < max : current > 0;
            if (canScroll)
            {
                if (e is HandledMouseEventArgs handled)
                {
                    handled.Handled = true;
                }
                ScrollBy(e.Delta < 0 ? StripPage() : -StripPage());
            }
        }

        public string SectionKey { get; }

        public IReadOnlyList<ModCard> Cards => cards;

        public event Action<ModCard>?    ModActivated;
        public event Action<ModCard>?    ModActionClicked;
        public event Action<ModCard>?    ModContextRequested;
        public event Action<string>?     SeeAllClicked;

        private static Button MakeNavButton(string glyph)
        {
            var button = new Button
            {
                Text      = glyph,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.CreateFont(12f, FontStyle.Bold),
                ForeColor = SoftTheme.TextPrimary,
                BackColor = SoftTheme.Surface,
                Size      = new Size(28, 26),
                TabStop   = false,
                Cursor    = Cursors.Hand,
            };
            button.FlatAppearance.BorderSize         = 0;
            button.FlatAppearance.BorderColor         = SoftTheme.Border;
            button.FlatAppearance.MouseOverBackColor = SoftTheme.SurfaceHover;
            button.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
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
                    card.Activated        += c => ModActivated?.Invoke(c);
                    card.ActionClicked    += c => ModActionClicked?.Invoke(c);
                    card.ContextRequested += c => ModContextRequested?.Invoke(c);
                    card.CardFocused      += c => EnsureVisible(c);
                    cards.Add(card);
                    strip.Controls.Add(card);
                }

                countLabel.Text = mods.Count.ToString("N0");
                var show = mods.Count > 0;
                Visible = show;
                titleLabel.Visible = show;
                countLabel.Visible = show;

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

        private int DesignScroll => ModCard.DesignWidth + 12;

        private void EnsureVisible(ModCard card)
        {
            var bounds = card.Bounds;
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
            int max = Math.Max(0, strip.HorizontalScroll.Maximum - strip.ClientSize.Width + 1);
            int target = Math.Max(0, Math.Min(max, x));
            strip.AutoScrollPosition = new Point(target, 0);
        }

        private void ScrollBy(int dx)
        {
            int current = -strip.AutoScrollPosition.X;
            ScrollTo(current + dx);
        }

        private void UpdateNavButtons()
        {
            int max = Math.Max(0, strip.HorizontalScroll.Maximum - strip.ClientSize.Width + 1);
            int current = -strip.AutoScrollPosition.X;
            prevButton.Enabled = current > 0;
            nextButton.Enabled = current < max;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            // The shelf needs room for the header, a full card plus its margins and
            // the horizontal scrollbar. Setting Height re-enters this method once;
            // the second pass then positions everything.
            int desiredHeight = HeaderHeight
                                + SoftTheme.ScaleInt(ModCard.DesignHeight, Dpi)
                                + SoftTheme.ScaleInt(16, Dpi)
                                + SystemInformation.HorizontalScrollBarHeight;
            if (Height != desiredHeight)
            {
                Height = desiredHeight;
                return;
            }

            int headerPad = 4;
            titleLabel.Location = new Point(headerPad, 9);
            countLabel.Location = new Point(titleLabel.Right + 8,
                                            titleLabel.Top + titleLabel.Padding.Top + 4);

            int right = Width - headerPad;
            if (seeAllButton.Visible)
            {
                seeAllButton.Size = new Size(TextRenderer.MeasureText(seeAllButton.Text,
                                                                     seeAllButton.Font).Width + 20,
                                             26);
                seeAllButton.Location = new Point(right - seeAllButton.Width, 8);
                right = seeAllButton.Left - 8;
            }

            nextButton.Location = new Point(right - nextButton.Width, 8);
            right = nextButton.Left - 4;
            prevButton.Location = new Point(right - prevButton.Width, 8);

            strip.Location = new Point(0, HeaderHeight);
            strip.Size     = new Size(Width, Math.Max(1, Height - HeaderHeight));
        }

        /// <summary>
        /// A FlowLayoutPanel that doesn't flicker while scrolling.
        /// </summary>
        private sealed class BufferedFlowPanel : FlowLayoutPanel
        {
            public BufferedFlowPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint, true);
                DoubleBuffered = true;
            }
        }
    }
}
