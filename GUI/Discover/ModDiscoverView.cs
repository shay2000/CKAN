using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// How the catalogue is ranked. Drives the order of every shelf and the
    /// title of the "top ranked" shelf.
    /// </summary>
    public enum DiscoverSortMode
    {
        Name             = 0,
        Downloads        = 1,
        NewestRelease    = 2,
        SmallestDownload = 3,
        Author           = 4,
    }

    /// <summary>
    /// A Netflix-style browsing surface for the mod catalogue.
    ///
    /// It is intentionally read-only with respect to the CKAN data model:
    /// selecting a card raises <see cref="ModActivated"/> and pressing the
    /// action button raises <see cref="ModActionClicked"/>, and the owning
    /// ManageMods control drives the real change set. This keeps a single
    /// source of truth for what is queued.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModDiscoverView : UserControl
    {
        private enum QuickFilter
        {
            All,
            Installed,
            Updates,
            New,
        }

        private const int MaxCardsPerRow   = 30;
        private const int MaxConcurrentArt = 3;
        private const int LibraryRowLimit  = 40;

        private readonly Label           titleLabel;
        private readonly Label           subtitleLabel;
        private readonly SoftPanel       searchField;
        private readonly TextBox         searchBox;
        private readonly Label           searchHint;
        private readonly Label           statLabel;
        private readonly FlowLayoutPanel filterPills;
        private readonly Label           sortLabel;
        private readonly ComboBox        sortBox;
        private readonly Panel           bodyPanel;
        private readonly FlowLayoutPanel rowsPanel;
        private readonly Panel           emptyPanel;
        private readonly Label           emptyTitle;
        private readonly Label           emptyBody;
        private readonly Panel           headerPanel;
        private readonly System.Windows.Forms.Timer artTimer;

        private readonly Dictionary<QuickFilter, Button> pillButtons =
            new Dictionary<QuickFilter, Button>();

        private readonly List<ModCarouselRow>      rows = new List<ModCarouselRow>();
        private readonly Queue<ModCard>            pendingArt = new Queue<ModCard>();
        private readonly HashSet<string>           artRequested = new HashSet<string>();
        private readonly Dictionary<string, Image> artByMod = new Dictionary<string, Image>();

        private IReadOnlyList<GUIMod> allMods = Array.Empty<GUIMod>();
        private Dictionary<string, GUIModChangeType> changes =
            new Dictionary<string, GUIModChangeType>(StringComparer.Ordinal);

        private ModVisualMetadataService? visuals;
        private CancellationTokenSource?  artCancellation;
        private QuickFilter               filter = QuickFilter.All;
        private DiscoverSortMode          sortMode = DiscoverSortMode.Name;
        private int                       activeArtLoads;
        private GUIMod?                   selected;
        private bool                      rebuilding;

        public ModDiscoverView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
                     true);
            BackColor = SoftTheme.Backdrop;
            Dock      = DockStyle.Fill;

            titleLabel = new Label
            {
                Text      = Properties.Resources.DiscoverTitle,
                Font      = SoftTheme.DisplayFont,
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };
            subtitleLabel = new Label
            {
                Text      = Properties.Resources.DiscoverSubtitle,
                Font      = SoftTheme.SubtitleFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };

            searchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor   = SoftTheme.Surface,
                ForeColor   = SoftTheme.TextPrimary,
                Font        = SoftTheme.SearchFont,
                AutoSize    = false,
            };

            searchHint = new Label
            {
                Text      = Properties.Resources.DiscoverSearchHint,
                Font      = SoftTheme.SearchFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Cursor    = Cursors.IBeam,
            };
            searchHint.Click += (sender, e) => searchBox.Focus();

            searchBox.TextChanged += (sender, e) =>
            {
                searchHint.Visible = searchBox.TextLength == 0;
                RebuildRows();
                SearchChanged?.Invoke();
            };

            searchField = new SoftPanel();
            searchField.Controls.Add(searchBox);
            searchField.Controls.Add(searchHint);
            searchHint.BringToFront();
            searchField.Click += (sender, e) => searchBox.Focus();

            filterPills = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                BackColor     = Color.Transparent,
                Margin        = new Padding(0),
                Padding       = new Padding(0),
            };
            BuildFilterPills();

            statLabel = new Label
            {
                Font      = SoftTheme.MetaFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize  = true,
                TextAlign = ContentAlignment.MiddleRight,
            };

            sortLabel = new Label
            {
                Text      = Properties.Resources.DiscoverSortLabel,
                Font      = SoftTheme.PillButtonFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };

            sortBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle     = FlatStyle.Flat,
                BackColor     = SoftTheme.Surface,
                ForeColor     = SoftTheme.TextPrimary,
                Font          = SoftTheme.PillButtonFont,
                DrawMode      = DrawMode.OwnerDrawFixed,
                ItemHeight    = SoftTheme.ScaleInt(20, DeviceDpiSafe),
                Width         = SoftTheme.ScaleInt(184, DeviceDpiSafe),
                TabStop       = false,
            };
            sortBox.DrawItem += SortBox_DrawItem;
            sortBox.Items.Add(Properties.Resources.DiscoverSortName);
            sortBox.Items.Add(Properties.Resources.DiscoverSortDownloads);
            sortBox.Items.Add(Properties.Resources.DiscoverSortNewest);
            sortBox.Items.Add(Properties.Resources.DiscoverSortSmallest);
            sortBox.Items.Add(Properties.Resources.DiscoverSortAuthor);
            sortBox.SelectedIndex = 0;
            sortBox.SelectedIndexChanged += (sender, e) =>
            {
                sortMode = (DiscoverSortMode)Math.Max(0, sortBox.SelectedIndex);
                RebuildRows();
                SortChanged?.Invoke();
            };

            rowsPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = SoftTheme.Backdrop,
                Padding       = new Padding(0, 4, 0, 8),
                Margin        = new Padding(0),
            };
            EnableDoubleBuffer(rowsPanel);
            rowsPanel.Scroll      += (sender, e) => PumpArtLoads();
            rowsPanel.SizeChanged += (sender, e) => LayoutRows();

            bodyPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = SoftTheme.Backdrop,
            };
            bodyPanel.Controls.Add(rowsPanel);

            emptyTitle = new Label
            {
                Text      = Properties.Resources.DiscoverEmptyTitle,
                Font      = SoftTheme.SectionFont,
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock      = DockStyle.Fill,
            };
            emptyBody = new Label
            {
                Text      = Properties.Resources.DiscoverEmptyMessage,
                Font      = SoftTheme.CardBodyFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.TopCenter,
                Dock      = DockStyle.Fill,
            };
            emptyPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = SoftTheme.Backdrop,
                Visible   = false,
            };
            var emptyLayout = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                RowCount    = 2,
                BackColor   = Color.Transparent,
            };
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            emptyLayout.Controls.Add(emptyTitle, 0, 0);
            emptyLayout.Controls.Add(emptyBody, 0, 1);
            emptyPanel.Controls.Add(emptyLayout);
            bodyPanel.Controls.Add(emptyPanel);

            headerPanel = BuildHeader();

            Controls.Add(bodyPanel);
            Controls.Add(headerPanel);

            artTimer = new System.Windows.Forms.Timer { Interval = 350 };
            artTimer.Tick += (sender, e) => PumpArtLoads();
            artTimer.Start();

            Resize += (sender, e) => LayoutRows();
        }

        /// <summary>Service used to fetch artwork on demand. May be null (art is skipped).</summary>
        public void SetArtworkService(ModVisualMetadataService? service)
        {
            visuals = service;
        }

        public event Action<GUIMod>?        ModActivated;
        public event Action<GUIMod>?        ModActionClicked;
        public event Action<GUIMod, Point>? ModContextRequested;
        public event Action?                SeeAllRequested;
        public event Action?                SearchChanged;
        public event Action?                SortChanged;

        /// <summary>
        /// Which ranking the shelves use. Setting it re-ranks everything.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DiscoverSortMode SortMode
        {
            get => sortMode;
            set
            {
                if (sortMode != value)
                {
                    sortMode = value;
                    int index = (int)sortMode;
                    if (index >= 0 && index < sortBox.Items.Count && sortBox.SelectedIndex != index)
                    {
                        // The change handler re-ranks for us
                        sortBox.SelectedIndex = index;
                        return;
                    }
                    RebuildRows();
                }
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string SearchText
        {
            get => searchBox.Text;
            set => searchBox.Text = value;
        }

        private static void EnableDoubleBuffer(Control control)
            => typeof(Control)
               .GetProperty("DoubleBuffered",
                            System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)
               ?.SetValue(control, true, null);

        #region Header

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 122,
                BackColor = SoftTheme.Backdrop,
            };

            header.Controls.Add(titleLabel);
            header.Controls.Add(subtitleLabel);
            header.Controls.Add(searchField);
            header.Controls.Add(filterPills);
            header.Controls.Add(sortLabel);
            header.Controls.Add(sortBox);
            header.Controls.Add(statLabel);

            header.Resize      += (sender, e) => LayoutHeader(header);
            header.HandleCreated += (sender, e) => LayoutHeader(header);
            LayoutHeader(header);
            return header;
        }

        private void LayoutHeader(Panel header)
        {
            int pad = SoftTheme.ScaleInt(20, DeviceDpiSafe);
            titleLabel.Location    = new Point(pad, SoftTheme.ScaleInt(14, DeviceDpiSafe));
            subtitleLabel.Location = new Point(pad + 2, titleLabel.Bottom + SoftTheme.ScaleInt(3, DeviceDpiSafe));

            int fieldWidth = Math.Max(SoftTheme.ScaleInt(220, DeviceDpiSafe),
                                      Math.Min(SoftTheme.ScaleInt(360, DeviceDpiSafe),
                                               header.Width / 3));
            searchField.Size     = new Size(fieldWidth, SoftTheme.ScaleInt(34, DeviceDpiSafe));
            searchField.Location = new Point(pad + 1, subtitleLabel.Bottom + SoftTheme.ScaleInt(12, DeviceDpiSafe));
            int boxHeight = searchBox.PreferredHeight;
            searchBox.SetBounds(SoftTheme.ScaleInt(14, DeviceDpiSafe),
                                (searchField.Height - boxHeight) / 2 + SoftTheme.ScaleInt(1, DeviceDpiSafe),
                                searchField.Width - SoftTheme.ScaleInt(38, DeviceDpiSafe),
                                boxHeight);
            searchHint.Location = new Point(searchBox.Left, searchBox.Top + 2);

            filterPills.Location = new Point(searchField.Right + SoftTheme.ScaleInt(14, DeviceDpiSafe),
                                             searchField.Top + SoftTheme.ScaleInt(2, DeviceDpiSafe));

            int metaTop = searchField.Top + SoftTheme.ScaleInt(6, DeviceDpiSafe);
            sortLabel.Location = new Point(filterPills.Right + SoftTheme.ScaleInt(18, DeviceDpiSafe),
                                           metaTop + SoftTheme.ScaleInt(6, DeviceDpiSafe));
            sortBox.Location = new Point(sortLabel.Right + SoftTheme.ScaleInt(6, DeviceDpiSafe),
                                         metaTop);

            statLabel.Location = new Point(sortBox.Right + SoftTheme.ScaleInt(16, DeviceDpiSafe),
                                           searchField.Top + SoftTheme.ScaleInt(8, DeviceDpiSafe));
            if (statLabel.Right > header.Width - pad)
            {
                statLabel.Location = new Point(Math.Max(pad, header.Width - pad - statLabel.Width),
                                               statLabel.Top);
            }
        }

        private void SortBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= sortBox.Items.Count)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var back = new SolidBrush(selected ? SoftTheme.AccentSoft : SoftTheme.Surface))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics,
                                  sortBox.Items[e.Index]?.ToString() ?? "",
                                  sortBox.Font,
                                  new Rectangle(e.Bounds.X + SoftTheme.ScaleInt(6, DeviceDpiSafe),
                                                e.Bounds.Y,
                                                e.Bounds.Width,
                                                e.Bounds.Height),
                                  selected ? SoftTheme.AccentDeep : SoftTheme.TextPrimary,
                                  TextFormatFlags.Left
                                  | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis
                                  | TextFormatFlags.NoPadding);
        }

        private void BuildFilterPills()
        {
            AddPill(QuickFilter.All,       Properties.Resources.DiscoverFilterAll);
            AddPill(QuickFilter.Installed, Properties.Resources.DiscoverFilterInstalled);
            AddPill(QuickFilter.Updates,   Properties.Resources.DiscoverFilterUpdates);
            AddPill(QuickFilter.New,       Properties.Resources.DiscoverFilterNew);
        }

        private void AddPill(QuickFilter which, string text)
        {
            var button = new Button
            {
                Text      = text,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.PillButtonFont,
                Height    = SoftTheme.ScaleInt(28, DeviceDpiSafe),
                AutoSize  = false,
                Tag       = which,
                Cursor    = Cursors.Hand,
                TabStop   = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (sender, e) =>
            {
                filter = which;
                UpdatePillStyles();
                RebuildRows();
            };
            using (var g = CreateGraphics())
            {
                button.Width = TextRenderer.MeasureText(g, text, button.Font).Width
                               + SoftTheme.ScaleInt(26, DeviceDpiSafe);
            }
            pillButtons[which] = button;
            filterPills.Controls.Add(button);
            UpdatePillStyles();
        }

        private void UpdatePillStyles()
        {
            foreach (var pair in pillButtons)
            {
                bool active = pair.Key == filter;
                pair.Value.BackColor = active ? SoftTheme.AccentSoft : SoftTheme.Surface;
                pair.Value.ForeColor = active ? SoftTheme.AccentDeep : SoftTheme.TextSecondary;
                pair.Value.FlatAppearance.MouseOverBackColor =
                    active ? SoftTheme.AccentSoft : SoftTheme.SurfaceHover;
                pair.Value.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            }
        }

        #endregion

        #region Data

        /// <summary>
        /// Rebuild the whole browsing surface from a new mod collection.
        /// Called on the UI thread after the repository has been refreshed.
        /// </summary>
        public void SetMods(IEnumerable<GUIMod> mods, IReadOnlyCollection<ModChange>? changeSet)
        {
            allMods = mods?.ToList() ?? new List<GUIMod>();
            SetChangeSet(changeSet);
            RebuildRows();
        }

        public void SetChangeSet(IReadOnlyCollection<ModChange>? changeSet)
        {
            changes = (changeSet ?? Array.Empty<ModChange>())
                      .Where(ch => ch?.Mod != null)
                      .GroupBy(ch => ch.Mod.identifier, StringComparer.Ordinal)
                      .ToDictionary(grp => grp.Key,
                                    grp => grp.Last().ChangeType,
                                    StringComparer.Ordinal);
            foreach (var row in rows)
            {
                row.RefreshStatuses(StatusFor);
            }
        }

        public void Highlight(GUIMod? mod)
        {
            selected = mod;
            foreach (var row in rows)
            {
                row.Highlight(mod);
            }
        }

        private ModCardStatus StatusFor(GUIMod mod)
        {
            if (mod.IsAutodetected)
            {
                return ModCardStatus.AutoDetected;
            }
            if (!mod.IsInstallable())
            {
                return ModCardStatus.Unavailable;
            }
            if (changes.TryGetValue(mod.Identifier, out var type))
            {
                switch (type)
                {
                    case GUIModChangeType.Install:
                        return mod.IsInstalled ? ModCardStatus.QueuedUpdate
                                               : ModCardStatus.QueuedInstall;
                    case GUIModChangeType.Remove:
                        return ModCardStatus.QueuedRemove;
                    case GUIModChangeType.Update:
                    case GUIModChangeType.Replace:
                        return ModCardStatus.QueuedUpdate;
                }
            }
            return mod.IsInstalled ? ModCardStatus.Installed : ModCardStatus.NotInstalled;
        }

        private void RebuildRows()
        {
            if (rebuilding)
            {
                return;
            }
            rebuilding = true;
            try
            {
                CancelInFlightArt();
                rowsPanel.SuspendLayout();
                try
                {
                    foreach (var row in rows)
                    {
                        rowsPanel.Controls.Remove(row);
                        row.Dispose();
                    }
                    rows.Clear();

                    var search = searchBox.Text?.Trim() ?? "";
                    var filtered = allMods.Where(m => Matches(m, search)).ToList();

                    UpdateStats();

                    if (filtered.Count > 0)
                    {
                        var installed = ApplySort(filtered.Where(m => m.IsInstalled && !m.IsAutodetected))
                                            .Take(LibraryRowLimit)
                                            .ToList();
                        var updates = ApplySort(filtered.Where(m => m.HasUpdate))
                                          .Take(MaxCardsPerRow)
                                          .ToList();
                        var fresh = ApplySort(filtered.Where(m => m.IsNew))
                                        .Take(MaxCardsPerRow)
                                        .ToList();
                        var ranked = ApplySort(filtered)
                                         .Take(MaxCardsPerRow)
                                         .ToList();
                        var everything = ApplySort(filtered)
                                             .Take(MaxCardsPerRow)
                                             .ToList();

                        if (filter == QuickFilter.All || filter == QuickFilter.Installed)
                        {
                            AddRow("library", Properties.Resources.DiscoverRowLibrary, installed, false);
                        }
                        if (filter == QuickFilter.All || filter == QuickFilter.Updates)
                        {
                            AddRow("updates", Properties.Resources.DiscoverRowUpdates, updates, false);
                        }
                        if (filter == QuickFilter.All || filter == QuickFilter.New)
                        {
                            AddRow("new", Properties.Resources.DiscoverRowNew, fresh, false);
                        }
                        if (filter == QuickFilter.All)
                        {
                            AddRow("ranked", RankedRowTitle, ranked, false);
                            AddRow("all", Properties.Resources.DiscoverRowAll, everything, true);
                        }
                    }
                }
                finally
                {
                    rowsPanel.ResumeLayout(true);
                }

                bool anything = rows.Count > 0;
                rowsPanel.Visible  = anything;
                emptyPanel.Visible = !anything;
                if (anything)
                {
                    LayoutRows();
                }
                else
                {
                    emptyPanel.BringToFront();
                }
            }
            finally
            {
                rebuilding = false;
            }
            PumpArtLoads();
        }

        /// <summary>
        /// Rank a sequence of mods with the user's chosen method. Download
        /// counts come from the repository's download statistics.
        /// </summary>
        private IOrderedEnumerable<GUIMod> ApplySort(IEnumerable<GUIMod> mods)
        {
            switch (sortMode)
            {
                case DiscoverSortMode.Downloads:
                    return mods.OrderByDescending(m => m.DownloadCount ?? 0)
                               .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

                case DiscoverSortMode.NewestRelease:
                    return mods.OrderByDescending(m => m.Module.release_date ?? DateTime.MinValue)
                               .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

                case DiscoverSortMode.SmallestDownload:
                    return mods.OrderBy(m => m.Module.download_size <= 0
                                                 ? long.MaxValue
                                                 : m.Module.download_size)
                               .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

                case DiscoverSortMode.Author:
                    return mods.OrderBy(m => string.Join(", ", m.Authors),
                                        StringComparer.CurrentCultureIgnoreCase)
                               .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);

                default:
                    return mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
            }
        }

        private string RankedRowTitle
        {
            get
            {
                switch (sortMode)
                {
                    case DiscoverSortMode.Downloads:
                        return Properties.Resources.DiscoverRowRankedDownloads;
                    case DiscoverSortMode.NewestRelease:
                        return Properties.Resources.DiscoverRowRankedNewest;
                    case DiscoverSortMode.SmallestDownload:
                        return Properties.Resources.DiscoverRowRankedSmallest;
                    case DiscoverSortMode.Author:
                        return Properties.Resources.DiscoverRowRankedAuthor;
                    default:
                        return Properties.Resources.DiscoverRowRankedName;
                }
            }
        }

        private void AddRow(string key, string title, IReadOnlyList<GUIMod> mods, bool seeAll)
        {
            if (mods.Count == 0)
            {
                return;
            }
            var row = new ModCarouselRow(key, title, seeAll);
            row.ModActivated     += card =>
            {
                Prioritise(card);
                ModActivated?.Invoke(card.Mod);
            };
            row.ModActionClicked += card =>
            {
                Prioritise(card);
                ModActionClicked?.Invoke(card.Mod);
            };
            row.ModContextRequested += card => ModContextRequested?.Invoke(
                card.Mod, card.PointToScreen(new Point(0, card.Height)));
            row.SeeAllClicked       += _ => SeeAllRequested?.Invoke();
            row.SetMods(mods);
            row.RefreshStatuses(StatusFor);
            row.Highlight(selected);
            rows.Add(row);
            rowsPanel.Controls.Add(row);
        }

        private void LayoutRows()
        {
            if (rows.Count == 0)
            {
                return;
            }
            // Stale scroll offsets left over from a previous set of rows would
            // place the new cards off-screen.
            rowsPanel.AutoScrollPosition = new Point(0, 0);
            int width = rowsPanel.ClientSize.Width - 8;
            foreach (var row in rows)
            {
                row.Width = Math.Max(SoftTheme.ScaleInt(200, DeviceDpiSafe), width);
            }
        }

        private void UpdateStats()
        {
            int available = allMods.Count;
            int installed = allMods.Count(m => m.IsInstalled);
            int updates = allMods.Count(m => m.HasUpdate);
            statLabel.Text = string.Format(Properties.Resources.DiscoverStats,
                                           available, installed, updates);
        }

        private static bool Matches(GUIMod mod, string search)
            => search.Length == 0
               || mod.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
               || mod.Identifier.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
               || mod.SearchableAbstract.Contains(search.ToLowerInvariant())
               || mod.Authors.Any(a => a.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0);

        #endregion

        #region On-demand artwork

        private float DeviceDpiSafe
        {
            get
            {
                try
                {
                    return DeviceDpi > 0 ? DeviceDpi : 96f;
                }
                catch
                {
                    return 96f;
                }
            }
        }

        /// <summary>
        /// Drop in-flight artwork requests but keep already downloaded images
        /// so rebuilding a row does not re-download everything.
        /// </summary>
        private void CancelInFlightArt()
        {
            artCancellation?.Cancel();
            artCancellation?.Dispose();
            artCancellation = null;
            pendingArt.Clear();
            artRequested.Clear();
        }

        private void PumpArtLoads()
        {
            if (IsDisposed || visuals == null || !IsHandleCreated || rebuilding)
            {
                return;
            }

            int totalCards = rows.Sum(r => r.Cards.Count);
            if (artRequested.Count < totalCards)
            {
                var viewport = RectangleToScreen(ClientRectangle);
                foreach (var row in rows)
                {
                    if (row.IsDisposed || !row.Visible)
                    {
                        continue;
                    }
                    var rowBounds = row.RectangleToScreen(row.ClientRectangle);
                    if (!viewport.IntersectsWith(rowBounds))
                    {
                        continue;
                    }
                    foreach (var card in row.Cards)
                    {
                        if (card.IsDisposed || !artRequested.Add(card.Mod.Identifier))
                        {
                            continue;
                        }
                        var cardBounds = card.RectangleToScreen(card.ClientRectangle);
                        if (viewport.IntersectsWith(cardBounds))
                        {
                            pendingArt.Enqueue(card);
                        }
                    }
                }
            }

            while (activeArtLoads < MaxConcurrentArt && pendingArt.Count > 0)
            {
                var card = pendingArt.Dequeue();
                if (card.IsDisposed)
                {
                    continue;
                }
                activeArtLoads++;
                _ = LoadArtAsync(card);
            }
        }

        private async Task LoadArtAsync(ModCard card)
        {
            try
            {
                if (visuals == null)
                {
                    return;
                }
                artCancellation ??= new CancellationTokenSource();
                var token = artCancellation.Token;

                string identifier = card.Mod.Identifier;
                if (artByMod.TryGetValue(identifier, out var cached))
                {
                    if (!card.IsDisposed)
                    {
                        card.Cover = (Image)cached.Clone();
                    }
                    return;
                }

                var image = await visuals.GetCoverAsync(card.Mod.Module,
                                                        ModCard.DesignWidth,
                                                        134,
                                                        token).ConfigureAwait(true);
                if (!IsDisposed && !card.IsDisposed && !token.IsCancellationRequested)
                {
                    artByMod[identifier] = image;
                    card.Cover = (Image)image.Clone();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the view is rebuilt mid-flight
            }
            catch
            {
                // Artwork is decorative; never let it break the view
            }
            finally
            {
                activeArtLoads--;
                if (!IsDisposed && IsHandleCreated && pendingArt.Count > 0)
                {
                    BeginInvoke(new MethodInvoker(PumpArtLoads));
                }
            }
        }

        /// <summary>Immediately fetch artwork for a card the user is interacting with.</summary>
        private void Prioritise(ModCard card)
        {
            if (visuals == null || card.IsDisposed)
            {
                return;
            }
            if (artByMod.TryGetValue(card.Mod.Identifier, out var cached))
            {
                card.Cover = (Image)cached.Clone();
                return;
            }
            artRequested.Add(card.Mod.Identifier);
            if (activeArtLoads < MaxConcurrentArt)
            {
                activeArtLoads++;
                _ = LoadArtAsync(card);
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                artTimer.Stop();
                artTimer.Dispose();
                CancelInFlightArt();
                foreach (var image in artByMod.Values)
                {
                    image.Dispose();
                }
                artByMod.Clear();
                artCancellation?.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Themed helpers

        /// <summary>
        /// A panel that paints itself with a rounded, soft background so the
        /// search box can look like a modern input field.
        /// </summary>
        private sealed class SoftPanel : Panel
        {
            public SoftPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);
                BackColor = SoftTheme.Backdrop;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(SoftTheme.Backdrop);
                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                SoftTheme.FillRounded(g, bounds, SoftTheme.Surface, SoftTheme.RadiusControl);
                SoftTheme.DrawRounded(g, bounds, SoftTheme.Border, SoftTheme.RadiusControl, 1f);
                base.OnPaint(e);
            }
        }

        #endregion
    }
}
