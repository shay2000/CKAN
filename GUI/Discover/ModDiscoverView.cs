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
    /// Lightweight catalogue buckets used by the concept-art toolbar. CKAN's
    /// repository does not require a separate taxonomy, so the native browser
    /// derives a stable presentation category from the mod's existing metadata.
    /// </summary>
    public enum DiscoverCategory
    {
        All = 0,
        Tools,
        Visuals,
        Parts,
        Gameplay,
        Core,
        Planets,
        Physics,
        Audio,
    }

    /// <summary>
    /// The small set of catalogue scopes exposed by the native rail and the
    /// concept-art filter pills. This is presentation state only; the actual
    /// install/update decisions remain owned by ManageMods and CKAN's model.
    /// </summary>
    public enum DiscoverFilter
    {
        All,
        Installed,
        Updates,
        New,
        Compatible,
    }

    /// <summary>
    /// The native shell keeps the catalogue, Library and Updates surfaces in
    /// one data-bound control, but each page gets the intentionally sparse
    /// toolbar and heading from the concept art.
    /// </summary>
    public enum DiscoverPageMode
    {
        Discover,
        Library,
        Updates,
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
        private const int MaxCardsPerRow   = 30;
        private const int MaxConcurrentArt = 3;
        private const int LibraryRowLimit  = 40;
        private const int MaxCatalogueRows = 400;

        private readonly Label           titleLabel;
        private readonly Label           subtitleLabel;
        private readonly SoftPanel       searchField;
        private readonly SearchIconControl searchIcon;
        private readonly TextBox         searchBox;
        private readonly Label           searchHint;
        private readonly Label           statLabel;
        private readonly PillStripPanel filterPills;
        private readonly Label           sortLabel;
        private readonly SoftComboBox    sortBox;
        private readonly SoftComboBox    categoryBox;
        private readonly DensityControl  densityControl;
        private readonly Button          playButton;
        private readonly Button          notesButton;
        private readonly Button          backToShelvesButton;
        private readonly Panel           bodyPanel;
        private readonly FlowLayoutPanel rowsPanel;
        private readonly FlowLayoutPanel catalogueRowsPanel;
        private readonly Panel           catalogueHeader;
        private readonly Panel           emptyPanel;
        private readonly Label           emptyTitle;
        private readonly Label           emptyBody;
        private readonly Panel           libraryUpdatePanel;
        private readonly Label           libraryUpdateIcon;
        private readonly Label           libraryUpdateTitle;
        private readonly Label           libraryUpdateNames;
        private readonly Button          libraryUpdateButton;
        private readonly Button          updatesQueueAllButton;
        private readonly Panel           designNotesPanel;
        private readonly Label           designNotesLabel;
        private readonly Panel           headerPanel;
        private readonly System.Windows.Forms.Timer artTimer;
        private int artGeneration;
        private readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer { Interval = 180 };

        private readonly Dictionary<DiscoverFilter, SoftPillButton> pillButtons =
            new Dictionary<DiscoverFilter, SoftPillButton>();

        private readonly List<ModCarouselRow>      rows = new List<ModCarouselRow>();
        private readonly List<ModCatalogueRow>      catalogueRows = new List<ModCatalogueRow>();
        private readonly Queue<ModCard>            pendingArt = new Queue<ModCard>();
        private readonly HashSet<string>           artRequested = new HashSet<string>();
        private readonly Dictionary<string, Image> artByMod = new Dictionary<string, Image>();

        /// <summary>
        /// Identifiers that already have a request queued or in flight. Deliberately
        /// separate from <see cref="artRequested"/>, which is only set once a card is
        /// actually enqueued: marking an identifier while merely *scanning* would stop
        /// cards that are still scrolled out of view from ever loading.
        /// </summary>
        private readonly HashSet<string> artInFlight = new HashSet<string>();

        /// <summary>
        /// Shelves the user has folded away, by section key. The state has to
        /// live here rather than on the row because every filter or sort change
        /// rebuilds the rows from scratch; a row that owned its own folded flag
        /// would spring back open the moment anything was re-filtered.
        /// </summary>
        private readonly HashSet<string> collapsedSections = new HashSet<string>();

        private IReadOnlyList<GUIMod> allMods = Array.Empty<GUIMod>();
        private Dictionary<string, GUIModChangeType> changes =
            new Dictionary<string, GUIModChangeType>(StringComparer.Ordinal);

        private ModVisualMetadataService? visuals;
        private CancellationTokenSource?  artCancellation;
        private DiscoverFilter            filter = DiscoverFilter.All;
        private DiscoverPageMode          pageMode = DiscoverPageMode.Discover;
        private DiscoverSortMode          sortMode = DiscoverSortMode.Downloads;
        private DiscoverCategory          category = DiscoverCategory.All;
        private DiscoverDensity            density = DiscoverDensity.Compact;
        private bool                      showHeaderActions = true;
        private bool                      showSubtitle = true;
        private string                    discoverSearchText = "";
        private string                    librarySearchText = "";
        private bool                      syncingSearchText;
        private int                       activeArtLoads;
        private GUIMod?                   selected;
        private bool                      rebuilding;
        private bool                      showShelfDisclosure = true;
        private string?                   expandedSection;
        private bool                      conceptSpacing;
        private bool                      layingOutHeader;

        public ModDiscoverView()
        {
            // All geometry below is deliberately DPI-scaled by SoftTheme.  A
            // parent such as ManageMods may still use AutoScaleMode.Dpi, so
            // leaving this control on the default Font mode scales the manual
            // rectangles a second time and produces clipped toolbars.
            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96f, 96f);
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

            searchIcon = new SearchIconControl();

            searchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor   = SoftTheme.Surface,
                ForeColor   = SoftTheme.TextPrimary,
                Font        = SoftTheme.SearchFont,
                AutoSize    = false,
            };
            searchIcon.Click += (sender, e) => searchBox.Focus();

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
                if (!syncingSearchText)
                {
                    if (pageMode == DiscoverPageMode.Library)
                    {
                        librarySearchText = searchBox.Text;
                    }
                    else
                    {
                        discoverSearchText = searchBox.Text;
                    }
                }
                searchTimer.Stop();
                searchTimer.Start();
                SearchChanged?.Invoke();
            };

            searchField = new SoftPanel();
            searchField.Controls.Add(searchBox);
            searchField.Controls.Add(searchHint);
            searchField.Controls.Add(searchIcon);
            searchHint.BringToFront();
            searchField.Click += (sender, e) => searchBox.Focus();

            filterPills = new PillStripPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = false,
                AutoSize      = true,
                AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                BackColor     = SoftTheme.Backdrop,
                Margin        = new Padding(0),
                Padding       = new Padding(SoftTheme.ScaleInt(4, DeviceDpiSafe)),
            };
            BuildFilterPills();

            statLabel = new Label
            {
                Font      = SoftTheme.MetaFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize  = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            sortLabel = new Label
            {
                Text      = Properties.Resources.DiscoverSortLabel,
                Font      = SoftTheme.PillButtonFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };

            sortBox = new SoftComboBox
            {
                BackColor     = SoftTheme.Surface,
                ForeColor     = SoftTheme.TextPrimary,
                Font          = SoftTheme.PillButtonFont,
                Width         = SoftTheme.ScaleInt(184, DeviceDpiSafe),
                TabStop       = false,
            };
            sortBox.Items.Add(Properties.Resources.DiscoverSortName);
            sortBox.Items.Add(Properties.Resources.DiscoverSortDownloads);
            sortBox.Items.Add(Properties.Resources.DiscoverSortNewest);
            sortBox.Items.Add(Properties.Resources.DiscoverSortSmallest);
            sortBox.Items.Add(Properties.Resources.DiscoverSortAuthor);
            sortBox.SelectedIndex = (int)sortMode;
            sortBox.SelectedIndexChanged += (sender, e) =>
            {
                sortMode = (DiscoverSortMode)Math.Max(0, sortBox.SelectedIndex);
                RebuildRows();
                SortChanged?.Invoke();
            };

            categoryBox = new SoftComboBox
            {
                BackColor     = SoftTheme.Surface,
                ForeColor     = SoftTheme.TextPrimary,
                Font          = SoftTheme.PillButtonFont,
                Width         = SoftTheme.ScaleInt(148, DeviceDpiSafe),
                TabStop       = false,
            };
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryAll);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryTools);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryVisuals);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryParts);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryGameplay);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryCore);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryPlanets);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryPhysics);
            categoryBox.Items.Add(Properties.Resources.DiscoverCategoryAudio);
            categoryBox.SelectedIndex = 0;
            categoryBox.SelectedIndexChanged += (sender, e) =>
            {
                category = (DiscoverCategory)Math.Max(0, categoryBox.SelectedIndex);
                searchTimer.Stop();
                searchTimer.Start();
            };

            densityControl = new DensityControl();
            densityControl.SetSelectedDensityQuietly(density);
            densityControl.SelectionChanged += value =>
            {
                density = value;
                RebuildRows();
                DensityChanged?.Invoke();
            };

            playButton = new Button
            {
                Text      = Properties.Resources.DiscoverPlayKsp,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.ActionFont,
                ForeColor = SoftTheme.OnAccent,
                BackColor = SoftTheme.Accent,
                AutoSize  = true,
                TabStop   = false,
                Cursor    = Cursors.Hand,
                Padding   = new Padding(SoftTheme.ScaleInt(12, DeviceDpiSafe), 0,
                                        SoftTheme.ScaleInt(12, DeviceDpiSafe), 0),
            };
            playButton.FlatAppearance.BorderSize = 0;
            playButton.FlatAppearance.MouseOverBackColor = SoftTheme.Mix(SoftTheme.Accent,
                                                                          SoftTheme.TextPrimary,
                                                                          0.08f);
            playButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentDeep;
            playButton.Click += (sender, e) => PlayRequested?.Invoke();

            notesButton = new Button
            {
                Text      = Properties.Resources.DiscoverDesignNotes,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.PillButtonFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = SoftTheme.Surface,
                AutoSize  = true,
                TabStop   = false,
                Cursor    = Cursors.Hand,
                Padding   = new Padding(SoftTheme.ScaleInt(10, DeviceDpiSafe), 0,
                                        SoftTheme.ScaleInt(10, DeviceDpiSafe), 0),
            };
            notesButton.FlatAppearance.BorderSize = 0;
            notesButton.FlatAppearance.MouseOverBackColor = SoftTheme.SurfaceHover;
            notesButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            notesButton.Click += (sender, e) => ToggleDesignNotes();

            backToShelvesButton = new Button
            {
                Text      = Properties.Resources.DiscoverBackToShelves,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.ActionFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = SoftTheme.Backdrop,
                AutoSize  = false,
                TabStop   = false,
                Cursor    = Cursors.Hand,
                Visible   = false,
            };
            backToShelvesButton.FlatAppearance.BorderSize = 0;
            backToShelvesButton.FlatAppearance.MouseOverBackColor = SoftTheme.SurfaceHover;
            backToShelvesButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            backToShelvesButton.Click += (sender, e) => CollapseShelf();

            rowsPanel = new SoftFlowPanel
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

            catalogueRowsPanel = new SoftFlowPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = SoftTheme.Backdrop,
                Padding       = new Padding(SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                            SoftTheme.ScaleInt(4, DeviceDpiSafe),
                                            SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                            SoftTheme.ScaleInt(12, DeviceDpiSafe)),
                Margin        = new Padding(0),
                Visible       = false,
            };
            EnableDoubleBuffer(catalogueRowsPanel);
            catalogueRowsPanel.SizeChanged += (sender, e) => LayoutCatalogueRows();

            catalogueHeader = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = SoftTheme.ScaleInt(34, DeviceDpiSafe),
                BackColor = SoftTheme.SurfaceSunken,
                Visible   = false,
            };
            catalogueHeader.Paint += CatalogueHeader_Paint;

            designNotesPanel = new SoftPanel
            {
                Dock      = DockStyle.Top,
                Height    = SoftTheme.ScaleInt(58, DeviceDpiSafe),
                BackColor = SoftTheme.Backdrop,
                Visible   = false,
                Padding   = new Padding(SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                        SoftTheme.ScaleInt(8, DeviceDpiSafe),
                                        SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                        SoftTheme.ScaleInt(8, DeviceDpiSafe)),
            };
            designNotesLabel = new Label
            {
                Text      = Properties.Resources.DiscoverNotesText,
                Font      = SoftTheme.CardBodyFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            designNotesPanel.Controls.Add(designNotesLabel);
            designNotesPanel.Resize += (sender, e) =>
            {
                designNotesLabel.Bounds = new Rectangle(designNotesPanel.Padding.Left,
                                                         designNotesPanel.Padding.Top,
                                                         Math.Max(1, designNotesPanel.ClientSize.Width
                                                                  - designNotesPanel.Padding.Horizontal),
                                                         Math.Max(1, designNotesPanel.ClientSize.Height
                                                                  - designNotesPanel.Padding.Vertical));
            };

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

            libraryUpdatePanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = SoftTheme.ScaleInt(76, DeviceDpiSafe),
                BackColor = SoftTheme.Backdrop,
                Padding = new Padding(SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                      SoftTheme.ScaleInt(8, DeviceDpiSafe),
                                      SoftTheme.ScaleInt(20, DeviceDpiSafe),
                                      SoftTheme.ScaleInt(8, DeviceDpiSafe)),
                Visible = false,
            };
            var updateBanner = new SoftPanel
            {
                Dock = DockStyle.Fill,
                BackColor = SoftTheme.Backdrop,
                Padding = new Padding(SoftTheme.ScaleInt(12, DeviceDpiSafe)),
            };
            libraryUpdateIcon = new Label
            {
                Text = "↑",
                Font = SoftTheme.SectionFont,
                ForeColor = SoftTheme.Warning,
                BackColor = SoftTheme.WarningSoft,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                Width = SoftTheme.ScaleInt(38, DeviceDpiSafe),
                Dock = DockStyle.Left,
                Margin = new Padding(0),
            };
            var updateText = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(SoftTheme.ScaleInt(12, DeviceDpiSafe),
                                      SoftTheme.ScaleInt(1, DeviceDpiSafe),
                                      SoftTheme.ScaleInt(10, DeviceDpiSafe), 0),
            };
            libraryUpdateTitle = new Label
            {
                Font = SoftTheme.CardTitleFont,
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize = true,
                Text = Properties.Resources.DiscoverLibraryUpdatesAvailable,
            };
            libraryUpdateNames = new Label
            {
                Font = SoftTheme.MetaFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                AutoSize = false,
            };
            updateText.Controls.Add(libraryUpdateNames);
            updateText.Controls.Add(libraryUpdateTitle);
            updateText.Resize += (sender, e) =>
            {
                libraryUpdateTitle.Location = new Point(0, 0);
                libraryUpdateNames.Location = new Point(0, libraryUpdateTitle.Bottom + 2);
                libraryUpdateNames.Size = new Size(Math.Max(1, updateText.ClientSize.Width),
                                                   Math.Max(1, updateText.ClientSize.Height
                                                            - libraryUpdateNames.Top));
            };
            libraryUpdateButton = new Button
            {
                Text = Properties.Resources.DiscoverUpdateAll,
                FlatStyle = FlatStyle.Flat,
                Font = SoftTheme.ActionFont,
                ForeColor = SoftTheme.OnAccent,
                BackColor = SoftTheme.Accent,
                AutoSize = false,
                Width = SoftTheme.ScaleInt(92, DeviceDpiSafe),
                Dock = DockStyle.Right,
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(SoftTheme.ScaleInt(8, DeviceDpiSafe), 0,
                                      SoftTheme.ScaleInt(8, DeviceDpiSafe), 0),
            };
            libraryUpdateButton.FlatAppearance.BorderSize = 0;
            libraryUpdateButton.FlatAppearance.MouseOverBackColor = SoftTheme.AccentDeep;
            libraryUpdateButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentDeep;
            libraryUpdateButton.Click += (sender, e) => QueueAllUpdatesRequested?.Invoke();
            updateBanner.Controls.Add(updateText);
            updateBanner.Controls.Add(libraryUpdateButton);
            updateBanner.Controls.Add(libraryUpdateIcon);
            libraryUpdatePanel.Controls.Add(updateBanner);

            updatesQueueAllButton = new Button
            {
                Text = Properties.Resources.DiscoverQueueAllUpdates,
                FlatStyle = FlatStyle.Flat,
                Font = SoftTheme.ActionFont,
                ForeColor = SoftTheme.OnAccent,
                BackColor = SoftTheme.Accent,
                AutoSize = false,
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(SoftTheme.ScaleInt(10, DeviceDpiSafe), 0,
                                      SoftTheme.ScaleInt(10, DeviceDpiSafe), 0),
                Visible = false,
            };
            updatesQueueAllButton.FlatAppearance.BorderSize = 0;
            updatesQueueAllButton.FlatAppearance.MouseOverBackColor = SoftTheme.AccentDeep;
            updatesQueueAllButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentDeep;
            updatesQueueAllButton.Click += (sender, e) => QueueAllUpdatesRequested?.Invoke();

            bodyPanel.Controls.Add(catalogueRowsPanel);
            bodyPanel.Controls.Add(catalogueHeader);
            bodyPanel.Controls.Add(emptyPanel);
            bodyPanel.Controls.Add(designNotesPanel);
            bodyPanel.Controls.Add(libraryUpdatePanel);

            headerPanel = BuildHeader();

            Controls.Add(bodyPanel);
            Controls.Add(headerPanel);
            headerPanel.Controls.Add(updatesQueueAllButton);

            artTimer = new System.Windows.Forms.Timer { Interval = 350 };
            artTimer.Tick += (sender, e) => PumpArtLoads();
            artTimer.Start();
            searchTimer.Tick += (sender, e) => { searchTimer.Stop(); RebuildRows(); };

            Resize += (sender, e) => LayoutRows();
        }

        /// <summary>Service used to fetch artwork on demand. May be null (art is skipped).</summary>
        public void SetArtworkService(ModVisualMetadataService? service)
        {
            visuals = service;
        }

        /// <summary>
        /// Clear the optional artwork cache and return every visible card to its
        /// deterministic generated cover.  The native shell uses this from its
        /// Storage page; it deliberately does not touch CKAN's download cache.
        /// </summary>
        public void ClearArtworkCache()
        {
            CancelInFlightArt();
            foreach (var image in artByMod.Values)
            {
                image.Dispose();
            }
            artByMod.Clear();
            foreach (var card in rows.SelectMany(row => row.Cards))
            {
                card.Cover = null;
            }
            Invalidate(true);
        }

        public event Action<GUIMod>?        ModActivated;
        public event Action<GUIMod>?        ModActionClicked;
        public event Action<GUIMod, Point>? ModContextRequested;
        public event Action<string>?        SeeAllRequested;
        public event Action?                SearchChanged;
        public event Action?                SortChanged;
        public event Action?                DensityChanged;
        public event Action?                PlayRequested;
        public event Action?                QueueAllUpdatesRequested;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DiscoverPageMode PageMode
        {
            get => pageMode;
            set
            {
                if (pageMode == value)
                {
                    return;
                }

                if (pageMode == DiscoverPageMode.Library)
                {
                    librarySearchText = searchBox.Text;
                }
                else
                {
                    discoverSearchText = searchBox.Text;
                }

                pageMode = value;
                expandedSection = null;
                switch (pageMode)
                {
                    case DiscoverPageMode.Library:
                        titleLabel.Text = Properties.Resources.DiscoverLibraryTitle;
                        filter = DiscoverFilter.Installed;
                        searchHint.Text = Properties.Resources.DiscoverLibrarySearchHint;
                        searchTextForPage = librarySearchText;
                        break;
                    case DiscoverPageMode.Updates:
                        titleLabel.Text = Properties.Resources.DiscoverUpdatesTitle;
                        filter = DiscoverFilter.Updates;
                        searchHint.Text = Properties.Resources.DiscoverSearchHint;
                        searchTextForPage = "";
                        density = DiscoverDensity.List;
                        densityControl.SetSelectedDensityQuietly(density);
                        break;
                    default:
                        titleLabel.Text = Properties.Resources.DiscoverTitle;
                        filter = DiscoverFilter.All;
                        searchHint.Text = Properties.Resources.DiscoverSearchHint;
                        searchTextForPage = discoverSearchText;
                        break;
                }
                SetSearchTextWithoutChangingPage(searchTextForPage);
                UpdatePillStyles();
                RebuildRows();
                LayoutHeader(headerPanel);
            }
        }

        private string searchTextForPage = "";

        /// <summary>
        /// Whether the view owns the small Play and Design notes actions in its
        /// page header. The full native shell renders those actions in the
        /// stage/titlebar, matching the concept art, while the standalone
        /// Discover control keeps them for the classic embedded view.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowHeaderActions
        {
            get => showHeaderActions;
            set
            {
                if (showHeaderActions != value)
                {
                    showHeaderActions = value;
                    LayoutHeader(headerPanel);
                }
            }
        }

        /// <summary>
        /// The standalone control includes a helpful subtitle. The native
        /// shell moves that copy to the stage header so the page itself stays
        /// as spare as the HTML concept.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowSubtitle
        {
            get => showSubtitle;
            set
            {
                if (showSubtitle != value)
                {
                    showSubtitle = value;
                    subtitleLabel.Visible = value;
                    LayoutHeader(headerPanel);
                }
            }
        }

        /// <summary>
        /// Whether shelves show the optional legacy disclosure affordance. The
        /// concept-art shell uses clean headings, while the standalone embedded
        /// control can keep the fold/unfold interaction.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowShelfDisclosure
        {
            get => showShelfDisclosure;
            set
            {
                if (showShelfDisclosure == value)
                {
                    return;
                }
                showShelfDisclosure = value;
                foreach (var row in rows)
                {
                    row.ShowDisclosure = value;
                }
            }
        }

        /// <summary>
        /// Use the page-head and toolbar spacing from the supplied concept art.
        /// The standalone control keeps its denser legacy embedding; the
        /// borderless native shell opts into the roomier 30px content gutters.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool UseConceptSpacing
        {
            get => conceptSpacing;
            set
            {
                if (conceptSpacing == value)
                {
                    return;
                }
                conceptSpacing = value;
                LayoutHeader(headerPanel);
                LayoutRows();
            }
        }

        /// <summary>
        /// Expand one shelf into the flat wrapping gallery used by the concept
        /// art's "See all" action. This never switches to the hidden CKAN grid.
        /// </summary>
        public void ExpandShelf(string sectionKey)
        {
            if (pageMode != DiscoverPageMode.Discover || string.IsNullOrWhiteSpace(sectionKey))
            {
                return;
            }

            expandedSection = sectionKey;
            filter = DiscoverFilter.All;
            category = DiscoverCategory.All;
            discoverSearchText = "";
            syncingSearchText = true;
            try
            {
                searchBox.Text = "";
                if (categoryBox.SelectedIndex != 0)
                {
                    categoryBox.SelectedIndex = 0;
                }
            }
            finally
            {
                syncingSearchText = false;
            }
            UpdatePillStyles();
            RebuildRows();
            LayoutHeader(headerPanel);
        }

        public void CollapseShelf()
        {
            if (expandedSection == null)
            {
                return;
            }
            expandedSection = null;
            RebuildRows();
            LayoutHeader(headerPanel);
        }

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
        public DiscoverDensity Density
        {
            get => density;
            set
            {
                var clamped = pageMode == DiscoverPageMode.Updates
                            ? DiscoverDensity.List
                            : (DiscoverDensity)Math.Max(0, Math.Min(2, (int)value));
                if (density == clamped)
                {
                    return;
                }
                density = clamped;
                densityControl.SetSelectedDensityQuietly(clamped);
                RebuildRows();
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DiscoverCategory Category
        {
            get => category;
            set
            {
                var clamped = (DiscoverCategory)Math.Max(0, Math.Min(8, (int)value));
                if (category == clamped)
                {
                    return;
                }
                category = clamped;
                if (categoryBox.SelectedIndex != (int)clamped)
                {
                    // The selection-change handler re-filters the catalogue.
                    categoryBox.SelectedIndex = (int)clamped;
                }
                else
                {
                    RebuildRows();
                }
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DiscoverFilter Filter
        {
            get => filter;
            set
            {
                var clamped = (DiscoverFilter)Math.Max(0,
                    Math.Min(4, (int)value));
                if (filter == clamped)
                {
                    return;
                }
                filter = clamped;
                UpdatePillStyles();
                RebuildRows();
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string SearchText
        {
            get => searchBox.Text;
            set
            {
                if (pageMode == DiscoverPageMode.Library)
                {
                    librarySearchText = value ?? "";
                }
                else
                {
                    discoverSearchText = value ?? "";
                }
                SetSearchTextWithoutChangingPage(value ?? "");
            }
        }

        public void FocusSearch()
        {
            searchBox.Focus();
            searchBox.SelectAll();
        }

        private void SetSearchTextWithoutChangingPage(string value)
        {
            syncingSearchText = true;
            try
            {
                if (!string.Equals(searchBox.Text, value, StringComparison.Ordinal))
                {
                    searchBox.Text = value;
                }
                searchHint.Visible = searchBox.TextLength == 0;
            }
            finally
            {
                syncingSearchText = false;
            }
        }

        /// <summary>
        /// Repaint the native controls after the shell toggles its palette.
        /// The card and row controls read SoftTheme during their paint pass;
        /// the editor controls need their cached WinForms colours refreshed.
        /// </summary>
        public void RefreshTheme()
        {
            BackColor = SoftTheme.Backdrop;
            headerPanel.BackColor = SoftTheme.Backdrop;
            bodyPanel.BackColor = SoftTheme.Backdrop;
            rowsPanel.BackColor = SoftTheme.Backdrop;
            catalogueRowsPanel.BackColor = SoftTheme.Backdrop;
            emptyPanel.BackColor = SoftTheme.Backdrop;
            designNotesPanel.BackColor = SoftTheme.Backdrop;
            searchField.BackColor = SoftTheme.Backdrop;
            searchBox.BackColor = SoftTheme.Surface;
            searchBox.ForeColor = SoftTheme.TextPrimary;
            searchHint.ForeColor = SoftTheme.TextMuted;
            titleLabel.ForeColor = SoftTheme.TextPrimary;
            subtitleLabel.ForeColor = SoftTheme.TextSecondary;
            statLabel.ForeColor = SoftTheme.TextMuted;
            sortLabel.ForeColor = SoftTheme.TextSecondary;
            sortBox.BackColor = SoftTheme.Surface;
            sortBox.ForeColor = SoftTheme.TextPrimary;
            categoryBox.BackColor = SoftTheme.Surface;
            categoryBox.ForeColor = SoftTheme.TextPrimary;
            densityControl.BackColor = SoftTheme.Backdrop;
            emptyTitle.ForeColor = SoftTheme.TextPrimary;
            emptyBody.ForeColor = SoftTheme.TextSecondary;
            designNotesLabel.ForeColor = SoftTheme.TextSecondary;
            libraryUpdatePanel.BackColor = SoftTheme.Backdrop;
            libraryUpdateIcon.ForeColor = SoftTheme.Warning;
            libraryUpdateIcon.BackColor = SoftTheme.WarningSoft;
            libraryUpdateTitle.ForeColor = SoftTheme.TextPrimary;
            libraryUpdateNames.ForeColor = SoftTheme.TextMuted;
            libraryUpdateButton.BackColor = SoftTheme.Accent;
            libraryUpdateButton.ForeColor = SoftTheme.OnAccent;
            libraryUpdateButton.FlatAppearance.MouseOverBackColor = SoftTheme.AccentDeep;
            updatesQueueAllButton.BackColor = SoftTheme.Accent;
            updatesQueueAllButton.ForeColor = SoftTheme.OnAccent;
            updatesQueueAllButton.FlatAppearance.MouseOverBackColor = SoftTheme.AccentDeep;
            backToShelvesButton.BackColor = SoftTheme.Backdrop;
            backToShelvesButton.ForeColor = SoftTheme.TextSecondary;
            backToShelvesButton.FlatAppearance.MouseOverBackColor = SoftTheme.SurfaceHover;
            backToShelvesButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            UpdatePillStyles();
            foreach (var row in rows)
            {
                row.Invalidate(true);
            }
            foreach (var row in catalogueRows)
            {
                row.Invalidate(true);
            }
            Invalidate(true);
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
                Height    = SoftTheme.ScaleInt(156, DeviceDpiSafe),
                BackColor = SoftTheme.Backdrop,
            };

            header.Controls.Add(titleLabel);
            header.Controls.Add(subtitleLabel);
            header.Controls.Add(searchField);
            header.Controls.Add(filterPills);
            header.Controls.Add(sortLabel);
            header.Controls.Add(sortBox);
            header.Controls.Add(categoryBox);
            header.Controls.Add(densityControl);
            header.Controls.Add(playButton);
            header.Controls.Add(notesButton);
            header.Controls.Add(backToShelvesButton);
            header.Controls.Add(statLabel);

            header.Resize      += (sender, e) => LayoutHeader(header);
            header.HandleCreated += (sender, e) => LayoutHeader(header);
            LayoutHeader(header);
            return header;
        }

        private void LayoutHeader(Panel header)
        {
            if (layingOutHeader)
            {
                return;
            }
            layingOutHeader = true;
            try
            {
                LayoutHeaderCore(header);
            }
            finally
            {
                layingOutHeader = false;
            }
        }

        private void LayoutHeaderCore(Panel header)
        {
            int dpi = (int)DeviceDpiSafe;
            int pad = SoftTheme.ScaleInt(conceptSpacing ? 30 : 20, dpi);
            int gap = SoftTheme.ScaleInt(10, dpi);
            int controlHeight = SoftTheme.ScaleInt(conceptSpacing ? 40 : 36, dpi);
            sortBox.Width = SoftTheme.ScaleInt(184, dpi);
            categoryBox.Width = SoftTheme.ScaleInt(148, dpi);
            densityControl.Width = SoftTheme.ScaleInt(108, dpi);
            int top = SoftTheme.ScaleInt(conceptSpacing ? 26 : 14, dpi);

            bool cataloguePage = pageMode == DiscoverPageMode.Discover;
            bool libraryPage = pageMode == DiscoverPageMode.Library;
            bool updatesPage = pageMode == DiscoverPageMode.Updates;

            titleLabel.Text = cataloguePage
                            ? Properties.Resources.DiscoverTitle
                            : libraryPage
                              ? Properties.Resources.DiscoverLibraryTitle
                              : Properties.Resources.DiscoverUpdatesTitle;
            titleLabel.Location = new Point(pad, top);
            subtitleLabel.Visible = showSubtitle && cataloguePage;
            subtitleLabel.Location = new Point(pad + SoftTheme.ScaleInt(2, dpi),
                                               titleLabel.Bottom + SoftTheme.ScaleInt(3, dpi));

            // The shell owns the stage/titlebar actions. Keep this branch for
            // the standalone embedded view without allowing those actions to
            // leak into the sparse Library and Updates pages.
            int right = Math.Max(pad, header.Width - pad);
            bool showActions = showHeaderActions && cataloguePage;
            playButton.Visible = showActions;
            int playWidth = TextRenderer.MeasureText(playButton.Text, playButton.Font).Width
                            + SoftTheme.ScaleInt(28, dpi);
            if (showActions)
            {
                playButton.SetBounds(Math.Max(pad, right - playWidth), top,
                                     playWidth, controlHeight);
                right = playButton.Left - gap;
            }

            bool showNotes = showActions && header.Width >= SoftTheme.ScaleInt(760, dpi);
            int notesWidth = TextRenderer.MeasureText(notesButton.Text, notesButton.Font).Width
                             + SoftTheme.ScaleInt(24, dpi);
            notesButton.Visible = showNotes;
            if (showNotes)
            {
                notesButton.SetBounds(Math.Max(pad, right - notesWidth), top,
                                      notesWidth, controlHeight);
            }
            backToShelvesButton.Visible = cataloguePage && expandedSection != null;

            filterPills.Visible = cataloguePage;
            sortLabel.Visible = cataloguePage;
            sortBox.Visible = cataloguePage;
            categoryBox.Visible = cataloguePage;
            densityControl.Visible = cataloguePage || libraryPage;
            searchField.Visible = !updatesPage;
            statLabel.Visible = !updatesPage;
            libraryUpdatePanel.Visible = libraryPage && allMods.Any(m => m.HasUpdate);
            updatesQueueAllButton.Visible = updatesPage && allMods.Any(m => m.HasUpdate);
            if (updatesQueueAllButton.Visible)
            {
                int actionWidth = SoftTheme.ScaleInt(166, dpi);
                updatesQueueAllButton.SetBounds(Math.Max(pad, header.Width - pad - actionWidth),
                                                top,
                                                actionWidth,
                                                controlHeight);
            }

            if (updatesPage)
            {
                header.Height = SoftTheme.ScaleInt(conceptSpacing ? 62 : 68, dpi);
                titleLabel.Location = new Point(pad, top);
                statLabel.Visible = false;
                searchField.Visible = false;
                filterPills.Visible = false;
                sortLabel.Visible = false;
                sortBox.Visible = false;
                categoryBox.Visible = false;
                densityControl.Visible = false;
                libraryUpdatePanel.Visible = false;
                backToShelvesButton.Visible = false;
                return;
            }

            int toolbarTop = (subtitleLabel.Visible ? subtitleLabel.Bottom : titleLabel.Bottom)
                             + SoftTheme.ScaleInt(subtitleLabel.Visible
                                                  ? 12
                                                  : conceptSpacing ? 24 : 16, dpi);
            int boxHeight = searchBox.PreferredHeight;
            Action<int, int, int> placeSearch = (left, y, width) =>
            {
                searchField.Size = new Size(Math.Max(1, width), controlHeight);
                searchField.Location = new Point(left, y);
                searchBox.SetBounds(SoftTheme.ScaleInt(34, dpi),
                                    (searchField.Height - boxHeight) / 2 + SoftTheme.ScaleInt(1, dpi),
                                    Math.Max(1, searchField.Width - SoftTheme.ScaleInt(58, dpi)),
                                    boxHeight);
                int iconSize = SoftTheme.ScaleInt(20, dpi);
                searchIcon.SetBounds(SoftTheme.ScaleInt(10, dpi),
                                     (searchField.Height - iconSize) / 2,
                                     iconSize, iconSize);
                searchHint.Location = new Point(searchBox.Left, searchBox.Top + 2);
            };

            if (libraryPage)
            {
                int densityWidth = densityControl.PreferredSize.Width > 0
                                 ? densityControl.PreferredSize.Width
                                 : SoftTheme.ScaleInt(110, dpi);
                int searchWidth = Math.Min(SoftTheme.ScaleInt(430, dpi),
                                           Math.Max(SoftTheme.ScaleInt(260, dpi),
                                                    header.Width - pad * 2 - densityWidth - gap
                                                    - SoftTheme.ScaleInt(158, dpi)));
                placeSearch(pad + SoftTheme.ScaleInt(1, dpi), toolbarTop, searchWidth);
                densityControl.SetBounds(header.Width - pad - densityWidth,
                                         toolbarTop,
                                         densityWidth,
                                         controlHeight);
                statLabel.Location = new Point(searchField.Right + gap,
                                               toolbarTop + SoftTheme.ScaleInt(9, dpi));
                statLabel.BringToFront();
                backToShelvesButton.Visible = false;
                header.Height = Math.Max(
                    SoftTheme.ScaleInt(conceptSpacing ? 122 : 132, dpi),
                    toolbarTop + controlHeight + SoftTheme.ScaleInt(16, dpi));
                return;
            }

            bool compactToolbar = header.Width < SoftTheme.ScaleInt(640, dpi);
            if (compactToolbar)
            {
                int availableWidth = Math.Max(1, header.Width - (pad * 2));
                placeSearch(pad, toolbarTop, availableWidth);

                filterPills.AutoSize = false;
                filterPills.WrapContents = true;
                filterPills.Width = availableWidth;
                int pillHeight = filterPills.Controls.Count == 0
                               ? SoftTheme.ScaleInt(28, dpi)
                               : filterPills.Controls[0].Height;
                int pillRows = 1;
                int pillRowWidth = filterPills.Padding.Horizontal;
                int pillGap = SoftTheme.ScaleInt(4, dpi);
                foreach (Control pill in filterPills.Controls)
                {
                    if (pillRowWidth + pill.Width > availableWidth
                        && pillRowWidth > filterPills.Padding.Horizontal)
                    {
                        pillRows++;
                        pillRowWidth = filterPills.Padding.Horizontal;
                    }
                    pillRowWidth += pill.Width + pillGap;
                }
                filterPills.Height = filterPills.Padding.Vertical
                                     + (pillRows * pillHeight)
                                     + ((pillRows - 1) * pillGap);
                filterPills.Location = new Point(pad, searchField.Bottom + gap);

                int controlsTop = filterPills.Bottom + gap;
                sortLabel.Location = new Point(pad, controlsTop);
                sortBox.SetBounds(pad,
                                  sortLabel.Bottom + SoftTheme.ScaleInt(3, dpi),
                                  availableWidth,
                                  controlHeight);
                categoryBox.SetBounds(pad,
                                      sortBox.Bottom + gap,
                                      availableWidth,
                                      controlHeight);
                densityControl.SetBounds(pad,
                                         categoryBox.Bottom + gap,
                                         Math.Min(densityControl.Width, availableWidth),
                                         controlHeight);

                int compactStatsTop = densityControl.Bottom + gap;
                statLabel.Location = new Point(pad + SoftTheme.ScaleInt(2, dpi),
                                               compactStatsTop);
                statLabel.Visible = true;
                statLabel.BringToFront();
                int compactBottom = compactStatsTop
                                    + Math.Max(statLabel.PreferredHeight,
                                               SoftTheme.ScaleInt(18, dpi))
                                    + SoftTheme.ScaleInt(12, dpi);
                if (backToShelvesButton.Visible)
                {
                    int backHeight = SoftTheme.ScaleInt(28, dpi);
                    backToShelvesButton.SetBounds(pad, compactBottom,
                                                  availableWidth, backHeight);
                    backToShelvesButton.BringToFront();
                    compactBottom += backHeight + gap;
                }
                header.Height = compactBottom;
                bodyPanel.BringToFront();
                return;
            }

            filterPills.AutoSize = true;
            filterPills.WrapContents = false;
            filterPills.AutoScroll = false;
            filterPills.MaximumSize = Size.Empty;

            int minimumSearchWidth = SoftTheme.ScaleInt(260, dpi);
            int singleRowWidth = pad * 2 + minimumSearchWidth
                                 + filterPills.Width + sortLabel.Width + sortBox.Width
                                 + categoryBox.Width + densityControl.Width
                                 + gap * 8 + SoftTheme.ScaleInt(6, dpi);
            bool singleRow = header.Width >= singleRowWidth;
            int secondaryRowWidth = pad * 2 + filterPills.Width + sortLabel.Width
                                    + sortBox.Width + categoryBox.Width + densityControl.Width
                                    + gap * 7 + SoftTheme.ScaleInt(6, dpi);
            bool threeRows = !singleRow && header.Width < secondaryRowWidth;
            int desiredHeight = SoftTheme.ScaleInt(singleRow ? 156 : threeRows ? 236 : 196, dpi);
            if (header.Height != desiredHeight)
            {
                header.Height = desiredHeight;
            }

            int statsTop;
            if (singleRow)
            {
                int fixedWidth = filterPills.Width + sortLabel.Width + sortBox.Width
                                 + categoryBox.Width + densityControl.Width
                                 + gap * 8 + SoftTheme.ScaleInt(6, dpi);
                int fieldWidth = Math.Max(minimumSearchWidth,
                                          Math.Min(SoftTheme.ScaleInt(420, dpi),
                                                   header.Width - (pad * 2) - fixedWidth));
                placeSearch(pad + SoftTheme.ScaleInt(1, dpi), toolbarTop, fieldWidth);

                int x = searchField.Right + gap;
                filterPills.Location = new Point(x, toolbarTop + SoftTheme.ScaleInt(3, dpi));
                x = filterPills.Right + gap;
                sortLabel.Location = new Point(x, toolbarTop + SoftTheme.ScaleInt(9, dpi));
                x = sortLabel.Right + SoftTheme.ScaleInt(6, dpi);
                sortBox.SetBounds(x, toolbarTop, sortBox.Width, controlHeight);
                x = sortBox.Right + gap;
                categoryBox.SetBounds(x, toolbarTop, categoryBox.Width, controlHeight);
                x = categoryBox.Right + gap;
                densityControl.SetBounds(x, toolbarTop, densityControl.Width, controlHeight);
                statsTop = searchField.Bottom + SoftTheme.ScaleInt(8, dpi);
            }
            else
            {
                // At the smaller logical widths used by a scaled Windows
                // display, a second toolbar row keeps every concept-art control
                // reachable instead of silently clipping the right-hand controls.
                int fullWidth = Math.Max(1, header.Width - (pad * 2) - SoftTheme.ScaleInt(1, dpi));
                int searchWidth = Math.Min(SoftTheme.ScaleInt(520, dpi), fullWidth);
                placeSearch(pad + SoftTheme.ScaleInt(1, dpi), toolbarTop, searchWidth);

                int secondTop = searchField.Bottom + SoftTheme.ScaleInt(8, dpi);
                int x = pad;
                filterPills.Location = new Point(x, secondTop + SoftTheme.ScaleInt(3, dpi));
                x = filterPills.Right + gap;
                int controlsTop = threeRows
                                  ? secondTop + controlHeight + SoftTheme.ScaleInt(8, dpi)
                                  : secondTop;
                if (threeRows)
                {
                    x = pad;
                }
                sortLabel.Location = new Point(x, controlsTop + SoftTheme.ScaleInt(9, dpi));
                x = sortLabel.Right + SoftTheme.ScaleInt(6, dpi);
                sortBox.SetBounds(x, controlsTop, sortBox.Width, controlHeight);
                x = sortBox.Right + gap;
                categoryBox.SetBounds(x, controlsTop, categoryBox.Width, controlHeight);
                x = categoryBox.Right + gap;
                densityControl.SetBounds(x, controlsTop, densityControl.Width, controlHeight);
                statsTop = controlsTop + controlHeight + SoftTheme.ScaleInt(8, dpi);
            }

            statLabel.Location = new Point(pad + SoftTheme.ScaleInt(2, dpi), statsTop);
            statLabel.Visible = true;
            statLabel.BringToFront();
            if (backToShelvesButton.Visible)
            {
                int backWidth = TextRenderer.MeasureText(backToShelvesButton.Text,
                                                         backToShelvesButton.Font).Width
                                + SoftTheme.ScaleInt(24, dpi);
                backToShelvesButton.SetBounds(Math.Max(pad,
                                                       header.Width - pad - backWidth),
                                              Math.Max(0, statsTop - SoftTheme.ScaleInt(4, dpi)),
                                              backWidth,
                                              SoftTheme.ScaleInt(28, dpi));
                backToShelvesButton.BringToFront();
            }

            // Keep the stats in the header's own measured area.  The previous
            // fixed heights were shorter than the three-row compact layout,
            // allowing the fill-docked shelves to paint over this last line.
            int contentBottom = statsTop
                                + Math.Max(statLabel.PreferredHeight,
                                           SoftTheme.ScaleInt(18, dpi))
                                + SoftTheme.ScaleInt(conceptSpacing ? 14 : 10, dpi);
            int minimumHeight = SoftTheme.ScaleInt(singleRow ? 156
                                                            : threeRows ? 236 : 196,
                                                   dpi);
            if (header.Height != Math.Max(minimumHeight, contentBottom))
            {
                header.Height = Math.Max(minimumHeight, contentBottom);
            }
            bodyPanel.BringToFront();
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

        private void CategoryBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= categoryBox.Items.Count)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var back = new SolidBrush(selected ? SoftTheme.AccentSoft : SoftTheme.Surface))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics,
                                  categoryBox.Items[e.Index]?.ToString() ?? "",
                                  categoryBox.Font,
                                  new Rectangle(e.Bounds.X + SoftTheme.ScaleInt(8, DeviceDpiSafe),
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
            AddPill(DiscoverFilter.All,       Properties.Resources.DiscoverFilterAll);
            AddPill(DiscoverFilter.Installed, Properties.Resources.DiscoverFilterInstalled);
            AddPill(DiscoverFilter.Updates,   Properties.Resources.DiscoverFilterUpdates);
            AddPill(DiscoverFilter.New,       Properties.Resources.DiscoverFilterNew);
            AddPill(DiscoverFilter.Compatible, Properties.Resources.DiscoverFilterCompatible);
        }

        private void AddPill(DiscoverFilter which, string text)
        {
            var button = new SoftPillButton
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
                pair.Value.IsSelected = active;
                pair.Value.BackColor = active ? SoftTheme.SurfaceRaised
                                              : SoftTheme.SurfaceSunken;
                pair.Value.ForeColor = active ? SoftTheme.TextPrimary
                                              : SoftTheme.TextSecondary;
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
            foreach (var row in catalogueRows)
            {
                row.Status = StatusFor(row.Mod);
            }
        }

        public void Highlight(GUIMod? mod)
        {
            selected = mod;
            foreach (var row in rows)
            {
                row.Highlight(mod);
            }
            foreach (var row in catalogueRows)
            {
                row.IsSelected = mod != null && row.Mod.Identifier == mod.Identifier;
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
                catalogueRowsPanel.SuspendLayout();
                try
                {
                    ClearCardRows();
                    ClearCatalogueRows();

                    var search = searchBox.Text?.Trim() ?? "";
                    var filtered = allMods.Where(m => Matches(m, search)).ToList();

                    UpdateStats(filtered.Count);

                    if (density == DiscoverDensity.List || density == DiscoverDensity.Table)
                    {
                        var catalogue = ApplyPageSort(filtered)
                                         .Take(MaxCatalogueRows)
                                         .ToList();
                        AddCatalogueRows(catalogue, density == DiscoverDensity.Table);
                    }
                    else if (filtered.Count > 0)
                    {
                        if (expandedSection != null)
                        {
                            var expandedMods = ShelfMods(expandedSection, filtered).ToList();
                            if (expandedMods.Count > 0)
                            {
                                AddRow(expandedSection,
                                       ShelfTitle(expandedSection),
                                       ApplySort(expandedMods).ToList(),
                                       false,
                                       true);
                            }
                        }
                        else
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

                        if (filter == DiscoverFilter.All || filter == DiscoverFilter.Installed)
                        {
                            AddRow("library", Properties.Resources.DiscoverRowContinueSetup, installed, true);
                        }
                        if (filter == DiscoverFilter.All || filter == DiscoverFilter.Updates)
                        {
                            AddRow("updates", Properties.Resources.DiscoverRowUpdates, updates, true);
                        }
                        if (filter == DiscoverFilter.All || filter == DiscoverFilter.New)
                        {
                            AddRow("new", Properties.Resources.DiscoverRowNew, fresh, true);
                        }
                        if (filter == DiscoverFilter.All)
                        {
                            AddRow("ranked", RankedRowTitle, ranked, true);
                            AddRow("all", Properties.Resources.DiscoverRowAll, everything, true);
                        }
                        if (filter == DiscoverFilter.Compatible)
                        {
                            AddRow("compatible", Properties.Resources.DiscoverRowCompatible,
                                   everything, true);
                        }
                        }
                    }
                }
                finally
                {
                    catalogueRowsPanel.ResumeLayout(true);
                    rowsPanel.ResumeLayout(true);
                }

                bool showCards = density == DiscoverDensity.Compact && rows.Count > 0;
                bool showCatalogue = density != DiscoverDensity.Compact && catalogueRows.Count > 0;
                bool anything = showCards || showCatalogue;
                rowsPanel.Visible  = showCards;
                catalogueRowsPanel.Visible = showCatalogue;
                catalogueHeader.Visible = showCatalogue && density == DiscoverDensity.Table;
                emptyPanel.Visible = !anything;
                if (anything)
                {
                    if (showCards)
                    {
                        rowsPanel.BringToFront();
                        LayoutRows();
                    }
                    else
                    {
                        catalogueRowsPanel.BringToFront();
                        if (catalogueHeader.Visible)
                        {
                            catalogueHeader.BringToFront();
                        }
                        LayoutCatalogueRows();
                    }
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

        private void ClearCardRows()
        {
            foreach (var row in rows)
            {
                rowsPanel.Controls.Remove(row);
                row.Dispose();
            }
            rows.Clear();
        }

        private void ClearCatalogueRows()
        {
            foreach (var row in catalogueRows)
            {
                catalogueRowsPanel.Controls.Remove(row);
                row.Dispose();
            }
            catalogueRows.Clear();
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

        private IOrderedEnumerable<GUIMod> ApplyPageSort(IEnumerable<GUIMod> mods)
        {
            if (pageMode == DiscoverPageMode.Updates)
            {
                return mods.OrderByDescending(ReleaseDateFor)
                           .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
            }
            return ApplySort(mods);
        }

        private static DateTime ReleaseDateFor(GUIMod mod)
            => mod.LatestCompatibleMod?.release_date
               ?? mod.LatestAvailableMod?.release_date
               ?? mod.Module.release_date
               ?? DateTime.MinValue;

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

        private IEnumerable<GUIMod> ShelfMods(string key, IEnumerable<GUIMod> source)
        {
            switch (key)
            {
                case "library":
                    return source.Where(m => m.IsInstalled && !m.IsAutodetected);
                case "updates":
                    return source.Where(m => m.HasUpdate);
                case "new":
                    return source.Where(m => m.IsNew);
                case "compatible":
                    return source.Where(m => !m.IsIncompatible);
                case "ranked":
                case "all":
                default:
                    return source;
            }
        }

        private string ShelfTitle(string key)
        {
            switch (key)
            {
                case "library":
                    return Properties.Resources.DiscoverRowContinueSetup;
                case "updates":
                    return Properties.Resources.DiscoverRowUpdates;
                case "new":
                    return Properties.Resources.DiscoverRowNew;
                case "compatible":
                    return Properties.Resources.DiscoverRowCompatible;
                case "ranked":
                    return RankedRowTitle;
                default:
                    return Properties.Resources.DiscoverRowAll;
            }
        }

        private void AddRow(string key, string title, IReadOnlyList<GUIMod> mods,
                            bool seeAll, bool expand = false)
        {
            if (mods.Count == 0)
            {
                return;
            }
            var row = new ModCarouselRow(key, title, seeAll);
            row.ShowDisclosure = showShelfDisclosure;
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
            row.SeeAllClicked       += section => SeeAllRequested?.Invoke(section);

            // Rows are rebuilt from scratch on every filter or sort change, so
            // the folded state has to live on the view and be reapplied here;
            // keeping it on the row would spring every shelf back open.
            row.CollapsedChanged += (sectionKey, isCollapsed) =>
            {
                if (isCollapsed)
                {
                    collapsedSections.Add(sectionKey);
                }
                else
                {
                    collapsedSections.Remove(sectionKey);
                }
                rowsPanel.PerformLayout();
            };
            if (!expand)
            {
                row.SetCollapsed(collapsedSections.Contains(key), false);
            }

            row.SetDensity(density);
            row.SetMods(mods);
            row.SetExpanded(expand);
            row.RefreshStatuses(StatusFor);
            row.Highlight(selected);
            rows.Add(row);
            rowsPanel.Controls.Add(row);
        }

        private void AddCatalogueRows(IReadOnlyList<GUIMod> mods, bool tableMode)
        {
            foreach (var mod in mods)
            {
                var row = new ModCatalogueRow(mod, tableMode,
                                              pageMode == DiscoverPageMode.Updates);
                row.Activated += catalogueRow =>
                {
                    selected = catalogueRow.Mod;
                    Highlight(catalogueRow.Mod);
                    ModActivated?.Invoke(catalogueRow.Mod);
                };
                row.ActionClicked += catalogueRow =>
                {
                    selected = catalogueRow.Mod;
                    Highlight(catalogueRow.Mod);
                    ModActionClicked?.Invoke(catalogueRow.Mod);
                };
                row.ContextRequested += catalogueRow => ModContextRequested?.Invoke(
                    catalogueRow.Mod,
                    catalogueRow.PointToScreen(new Point(0, catalogueRow.Height)));
                row.RowFocused += catalogueRow => Highlight(catalogueRow.Mod);
                row.Status = StatusFor(mod);
                row.TabIndex = catalogueRows.Count;
                row.IsSelected = selected != null
                                 && selected.Identifier == mod.Identifier;
                catalogueRows.Add(row);
                catalogueRowsPanel.Controls.Add(row);
            }
        }

        private void LayoutRows()
        {
            if (rows.Count == 0)
            {
                return;
            }
            // Leave a stable gutter for the client-painted scroll thumb.
            int width = rowsPanel.ClientSize.Width - SoftTheme.Px(18);
            foreach (var row in rows)
            {
                row.Width = Math.Max(SoftTheme.ScaleInt(200, DeviceDpiSafe), width);
            }
        }

        private void LayoutCatalogueRows()
        {
            if (catalogueRows.Count == 0)
            {
                return;
            }
            int width = catalogueRowsPanel.ClientSize.Width
                        - catalogueRowsPanel.Padding.Horizontal - SoftTheme.Px(18);
            foreach (var row in catalogueRows)
            {
                row.Width = Math.Max(SoftTheme.ScaleInt(720, DeviceDpiSafe), width);
            }
        }

        private void CatalogueHeader_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SoftTheme.SurfaceSunken);
            int dpi = (int)DeviceDpiSafe;
            int pad = SoftTheme.ScaleInt(14, dpi);
            int actionWidth = SoftTheme.ScaleInt(92, dpi);
            int actionLeft = Math.Max(pad, catalogueHeader.Width - pad - actionWidth);
            int right = actionLeft - SoftTheme.ScaleInt(18, dpi);
            int categoryWidth = SoftTheme.ScaleInt(96, dpi);
            int sizeWidth = SoftTheme.ScaleInt(82, dpi);
            int versionWidth = SoftTheme.ScaleInt(115, dpi);
            int statusWidth = SoftTheme.ScaleInt(86, dpi);
            int columnGap = SoftTheme.ScaleInt(8, dpi);
            int statusLeft = right - statusWidth;
            int versionLeft = statusLeft - columnGap - versionWidth;
            int sizeLeft = versionLeft - columnGap - sizeWidth;
            int categoryLeft = sizeLeft - columnGap - categoryWidth;
            int labelY = SoftTheme.ScaleInt(9, dpi);
            using (var brush = new SolidBrush(SoftTheme.TextMuted))
            {
                TextRenderer.DrawText(g, Properties.Resources.DiscoverTableMod,
                                      SoftTheme.MetaFont,
                                      new Rectangle(pad + SoftTheme.ScaleInt(42, dpi), labelY,
                                                    Math.Max(50, statusLeft - pad), 18),
                                      brush.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Properties.Resources.DiscoverTableStatus,
                                      SoftTheme.MetaFont,
                                      new Rectangle(statusLeft, labelY, statusWidth, 18),
                                      brush.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Properties.Resources.DiscoverTableVersion,
                                      SoftTheme.MetaFont,
                                      new Rectangle(versionLeft, labelY, versionWidth, 18),
                                      brush.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Properties.Resources.DiscoverTableSize,
                                      SoftTheme.MetaFont,
                                      new Rectangle(sizeLeft, labelY, sizeWidth, 18),
                                      brush.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Properties.Resources.DiscoverTableCategory,
                                      SoftTheme.MetaFont,
                                      new Rectangle(categoryLeft, labelY, categoryWidth, 18),
                                      brush.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
            using (var pen = new Pen(SoftTheme.Border, 1f))
            {
                g.DrawLine(pen, 0, catalogueHeader.Height - 1,
                           catalogueHeader.Width, catalogueHeader.Height - 1);
            }
        }

        private void UpdateStats(int matching)
        {
            int available = allMods.Count;
            int installed = allMods.Count(m => m.IsInstalled);
            int updates = allMods.Count(m => m.HasUpdate);
            if (pageMode == DiscoverPageMode.Library)
            {
                long installBytes = allMods.Where(m => m.IsInstalled)
                                            .Sum(m => m.Module.install_size);
                string disk = installBytes > 0 ? CkanModule.FmtSize(installBytes) : "—";
                statLabel.Text = string.Format(Properties.Resources.DiscoverLibraryStats,
                                               matching, installed, disk);
                var updateMods = allMods.Where(m => m.HasUpdate).ToList();
                libraryUpdatePanel.Visible = updateMods.Count != 0;
                libraryUpdateTitle.Text = string.Format(
                    Properties.Resources.DiscoverLibraryUpdatesAvailable,
                    updateMods.Count);
                libraryUpdateNames.Text = string.Join(", ", updateMods.Take(5)
                                                                    .Select(m => m.Name))
                                          + (updateMods.Count > 5 ? " …" : "");
            }
            else if (pageMode == DiscoverPageMode.Updates)
            {
                statLabel.Text = "";
                libraryUpdatePanel.Visible = false;
            }
            else if (searchBox.TextLength > 0 || filter != DiscoverFilter.All
                     || category != DiscoverCategory.All)
            {
                statLabel.Text = string.Format(Properties.Resources.DiscoverStatsMatching,
                                               available, installed, updates, matching);
            }
            else
            {
                statLabel.Text = string.Format(Properties.Resources.DiscoverStats,
                                               available, installed, updates);
            }
            // The label is auto-sized, so its width is only correct once the
            // text has been set. Without re-laying out the header it keeps the
            // width it had when it was empty and drifts under the sort combo.
            if (statLabel.Parent is Panel header)
            {
                LayoutHeader(header);
            }
        }

        private bool Matches(GUIMod mod, string search)
        {
            bool textMatches = search.Length == 0
                               || mod.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
                               || mod.Identifier.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                               || mod.SearchableAbstract.Contains(search.ToLowerInvariant())
                               || mod.Authors.Any(a => a.IndexOf(search,
                                                                 StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (!textMatches)
            {
                return false;
            }

            switch (filter)
            {
                case DiscoverFilter.Installed:
                    if (!mod.IsInstalled)
                    {
                        return false;
                    }
                    break;
                case DiscoverFilter.Updates:
                    if (!mod.HasUpdate)
                    {
                        return false;
                    }
                    break;
                case DiscoverFilter.New:
                    if (!mod.IsNew)
                    {
                        return false;
                    }
                    break;
                case DiscoverFilter.Compatible:
                    if (mod.IsIncompatible)
                    {
                        return false;
                    }
                    break;
            }

            return category == DiscoverCategory.All || CategoryFor(mod) == category;
        }

        /// <summary>
        /// Presentation-only category inference. The underlying GUIMod remains
        /// untouched; this keeps the new toolbar useful with old registries that
        /// do not carry a curated category field.
        /// </summary>
        public static DiscoverCategory CategoryFor(GUIMod mod)
        {
            string text = string.Join(" ", new[]
            {
                mod.Identifier,
                mod.Name,
                mod.Abstract,
                mod.Description,
            }).ToLowerInvariant();

            if (ContainsAny(text, "planet", "galaxy", "kopernicus", "world"))
            {
                return DiscoverCategory.Planets;
            }
            if (ContainsAny(text, "visual", "texture", "shader", "skybox", "graphics", "scatter"))
            {
                return DiscoverCategory.Visuals;
            }
            if (ContainsAny(text, "sound", "audio", "music", "voice"))
            {
                return DiscoverCategory.Audio;
            }
            if (ContainsAny(text, "engine", "tank", "fuel", "wheel", "part", "propulsion"))
            {
                return DiscoverCategory.Parts;
            }
            if (ContainsAny(text, "physics", "aero", "drag", "orbit", "gravity"))
            {
                return DiscoverCategory.Physics;
            }
            if (ContainsAny(text, "tool", "manager", "editor", "alarm", "engineer", "debug", "utility"))
            {
                return DiscoverCategory.Tools;
            }
            if (ContainsAny(text, "core", "framework", "library", "dependency", "module"))
            {
                return DiscoverCategory.Core;
            }
            return DiscoverCategory.Gameplay;
        }

        public static string CategoryLabel(DiscoverCategory value)
        {
            switch (value)
            {
                case DiscoverCategory.Tools:
                    return Properties.Resources.DiscoverCategoryTools;
                case DiscoverCategory.Visuals:
                    return Properties.Resources.DiscoverCategoryVisuals;
                case DiscoverCategory.Parts:
                    return Properties.Resources.DiscoverCategoryParts;
                case DiscoverCategory.Gameplay:
                    return Properties.Resources.DiscoverCategoryGameplay;
                case DiscoverCategory.Core:
                    return Properties.Resources.DiscoverCategoryCore;
                case DiscoverCategory.Planets:
                    return Properties.Resources.DiscoverCategoryPlanets;
                case DiscoverCategory.Physics:
                    return Properties.Resources.DiscoverCategoryPhysics;
                case DiscoverCategory.Audio:
                    return Properties.Resources.DiscoverCategoryAudio;
                default:
                    return Properties.Resources.DiscoverCategoryAll;
            }
        }

        private static bool ContainsAny(string text, params string[] needles)
            => needles.Any(text.Contains);

        #endregion

        #region On-demand artwork

        private float DeviceDpiSafe
        {
            get
            {
                try
                {
                    return SoftTheme.LayoutDpi;
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
            artGeneration++;
            artCancellation?.Cancel();
            artCancellation?.Dispose();
            artCancellation = null;
            pendingArt.Clear();
            artRequested.Clear();
            artInFlight.Clear();
        }

        private void PumpArtLoads()
        {
            if (IsDisposed || visuals == null || !IsHandleCreated || rebuilding)
            {
                return;
            }

            // The same mod can appear on several shelves, so every visible card is
            // offered the cached image rather than only the one that triggered the
            // download. Off-screen cards are left unmarked so a later scroll still
            // gives them a turn.
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
                    if (card.IsDisposed
                        || !viewport.IntersectsWith(card.RectangleToScreen(card.ClientRectangle)))
                    {
                        continue;
                    }

                    string identifier = card.Mod.Identifier;
                    if (artByMod.TryGetValue(identifier, out var cached))
                    {
                        if (card.Cover == null)
                        {
                            card.Cover = (Image)cached.Clone();
                        }
                        continue;
                    }
                    if (artInFlight.Contains(identifier) || artRequested.Contains(identifier))
                    {
                        continue;
                    }
                    artInFlight.Add(identifier);
                    pendingArt.Enqueue(card);
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
            int generation = artGeneration;
            string identifier = card.Mod.Identifier;
            try
            {
                if (visuals == null)
                {
                    return;
                }
                artCancellation ??= new CancellationTokenSource();
                var token = artCancellation.Token;

                if (artByMod.TryGetValue(identifier, out var cached))
                {
                    if (!card.IsDisposed)
                    {
                        card.Cover = (Image)cached.Clone();
                    }
                    return;
                }

                // Ask for the cover at the DPI-scaled size the card actually paints at,
                // otherwise a HiDPI display upscales a 96 DPI image and it looks soft.
                var image = await Task.Run(() => visuals.GetCoverAsync(card.Mod.Module,
                                                        SoftTheme.ScaleInt(ModCard.WidthFor(density), DeviceDpiSafe),
                                                        SoftTheme.ScaleInt(ModCard.CoverHeightFor(density), DeviceDpiSafe),
                                                        token), token).ConfigureAwait(true);
                if (!IsDisposed && !card.IsDisposed && !token.IsCancellationRequested)
                {
                    if (artByMod.TryGetValue(identifier, out var previous)) previous.Dispose();
                    artByMod[identifier] = image;
                    card.Cover = (Image)image.Clone();
                }
                else image.Dispose();
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
                if (generation == artGeneration)
                {
                    artInFlight.Remove(identifier);
                    artRequested.Add(identifier);
                }
                activeArtLoads--;
                if (!IsDisposed && IsHandleCreated)
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
            string identifier = card.Mod.Identifier;
            if (artByMod.TryGetValue(identifier, out var cached))
            {
                if (card.Cover == null)
                {
                    card.Cover = (Image)cached.Clone();
                }
                return;
            }
            if (artInFlight.Contains(identifier) || artRequested.Contains(identifier))
            {
                return;
            }
            // Queue it rather than starting the download here: the pump respects the
            // concurrency cap, and a card that misses the cap would otherwise be marked
            // as requested without ever being loaded.
            artInFlight.Add(identifier);
            pendingArt.Enqueue(card);
            PumpArtLoads();
        }

        #endregion

        private void ToggleDesignNotes()
        {
            designNotesPanel.Visible = !designNotesPanel.Visible;
            notesButton.BackColor = designNotesPanel.Visible
                                    ? SoftTheme.AccentSoft
                                    : SoftTheme.Surface;
            notesButton.ForeColor = designNotesPanel.Visible
                                    ? SoftTheme.AccentDeep
                                    : SoftTheme.TextSecondary;
            if (designNotesPanel.Visible)
            {
                designNotesPanel.BringToFront();
            }
            bodyPanel.PerformLayout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (searchBox.ContainsFocus)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == Keys.V)
            {
                densityControl.SelectedDensity = (DiscoverDensity)(((int)density + 1) % 3);
                return true;
            }
            if (keyData == Keys.N && showHeaderActions)
            {
                ToggleDesignNotes();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                artTimer.Stop();
                searchTimer.Dispose();
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

        #if NET5_0_OR_GREATER
        [SupportedOSPlatform("windows")]
        #endif
        private sealed class PillStripPanel : FlowLayoutPanel
        {
            public PillStripPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(SoftTheme.Backdrop);
                var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                            Math.Max(1, Height - 1));
                SoftTheme.FillRounded(e.Graphics, bounds, SoftTheme.SurfaceSunken,
                                      SoftTheme.RadiusControl);
                SoftTheme.DrawRounded(e.Graphics, bounds, SoftTheme.Border,
                                      SoftTheme.RadiusControl, 1f);
            }
        }

        private sealed class SoftPillButton : Button
        {
            private bool hovered;

            public SoftPillButton()
            {
                SetStyle(ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                TextAlign = ContentAlignment.MiddleCenter;
            }

            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool IsSelected { get; set; }

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
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);
                var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                            Math.Max(1, Height - 1));
                Color fill = hovered && !IsSelected ? SoftTheme.SurfaceHover : BackColor;
                SoftTheme.FillRounded(g, bounds, fill,
                                      Math.Max(3, SoftTheme.RadiusControl - 3));
                if (IsSelected)
                {
                    SoftTheme.DrawRounded(g, bounds, SoftTheme.BorderStrong,
                                          Math.Max(3, SoftTheme.RadiusControl - 3), 1f);
                }
                TextRenderer.DrawText(g, Text, Font, bounds, ForeColor,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
            }
        }

        private sealed class SoftComboBox : Control
        {
            private bool hovered;
            private ContextMenuStrip? dropDown;
            private int selectedIndex = -1;
            public List<string> Items { get; } = new List<string>();
            public event EventHandler? SelectedIndexChanged;
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public int SelectedIndex
            {
                get => selectedIndex;
                set
                {
                    if (selectedIndex == value) return;
                    selectedIndex = value;
                    Invalidate();
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public SoftComboBox()
            {
                SetStyle(ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                TabStop = true;
                AccessibleRole = AccessibleRole.ComboBox;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter
                    || (e.Alt && e.KeyCode == Keys.Down))
                {
                    OnClick(EventArgs.Empty);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                base.OnKeyDown(e);
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
                Invalidate();
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                var menu = dropDown ??= new ContextMenuStrip
                {
                    BackColor = SoftTheme.Surface,
                    ForeColor = SoftTheme.TextPrimary,
                    Font = SoftTheme.SearchFont,
                    ShowImageMargin = false,
                    Renderer = new SoftToolStripRenderer(),
                };
                menu.Items.Clear();
                for (int i = 0; i < Items.Count; ++i)
                {
                    int index = i;
                    var item = new ToolStripMenuItem(Items[i])
                    {
                        Checked = i == SelectedIndex,
                        Padding = new Padding(SoftTheme.Px(10), SoftTheme.Px(6), SoftTheme.Px(10), SoftTheme.Px(6)),
                    };
                    item.Click += (sender, args) => SelectedIndex = index;
                    menu.Items.Add(item);
                }
                menu.Show(this, new Point(0, Height));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) dropDown?.Dispose();
                base.Dispose(disposing);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);
                var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                            Math.Max(1, Height - 1));
                SoftTheme.FillRounded(g, bounds, SoftTheme.Surface,
                                      SoftTheme.RadiusControl);
                SoftTheme.DrawRounded(g, bounds,
                                      hovered ? SoftTheme.BorderStrong : SoftTheme.Border,
                                      SoftTheme.RadiusControl, 1f);

                string text = SelectedIndex >= 0
                            ? Items[SelectedIndex]
                            : Text;
                int arrowWidth = SoftTheme.ScaleInt(24, DeviceDpiSafe);
                TextRenderer.DrawText(g, text, Font,
                                      new Rectangle(SoftTheme.ScaleInt(12, DeviceDpiSafe),
                                                    0,
                                                    Math.Max(1, Width - arrowWidth
                                                               - SoftTheme.ScaleInt(18, DeviceDpiSafe)),
                                                    Height),
                                      ForeColor,
                                      TextFormatFlags.Left
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);

                int centerX = Width - SoftTheme.ScaleInt(15, DeviceDpiSafe);
                int centerY = Height / 2;
                int chevron = SoftTheme.ScaleInt(4, DeviceDpiSafe);
                using (var pen = new Pen(SoftTheme.TextMuted,
                                          SoftTheme.Scale(1.4f, DeviceDpiSafe)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, centerX - chevron, centerY - 1,
                               centerX, centerY + chevron - 1);
                    g.DrawLine(pen, centerX, centerY + chevron - 1,
                               centerX + chevron, centerY - 1);
                }
            }

            private float DeviceDpiSafe
            {
                get
                {
                    try
                    {
                        return SoftTheme.LayoutDpi;
                    }
                    catch
                    {
                        return 96f;
                    }
                }
            }
        }

        private sealed class SearchIconControl : Control
        {
            public SearchIconControl()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint
                         | ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.IBeam;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float dpi = DeviceDpi > 0 ? DeviceDpi : 96f;
                float stroke = SoftTheme.Scale(1.6f, dpi);
                using (var pen = new Pen(SoftTheme.TextMuted, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float size = SoftTheme.Scale(10f, dpi);
                    float left = SoftTheme.Scale(3f, dpi);
                    float top = SoftTheme.Scale(3f, dpi);
                    g.DrawEllipse(pen, left, top, size, size);
                    g.DrawLine(pen,
                               left + size * 0.72f,
                               top + size * 0.72f,
                               SoftTheme.Scale(17f, dpi),
                               SoftTheme.Scale(17f, dpi));
                }
            }
        }

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
