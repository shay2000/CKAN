using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

using Autofac;
using CKAN.IO;

namespace CKAN.GUI
{
    /// <summary>
    /// The native application shell for the Discover experience.
    ///
    /// This is deliberately ordinary WinForms drawing and controls rather than
    /// a browser wrapper. ManageMods remains the owner of the CKAN model,
    /// registry, changeset and launch events; this control only supplies the
    /// presentation and routes the existing actions into that owner.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class ModernShell : UserControl
    {
        private enum ShellSection
        {
            Discover,
            Library,
            Updates,
            Queue,
            Collections,
            Settings,
        }

        private readonly Main                 owner;
        private readonly ManageMods           manageMods;
        private readonly Panel                appSurface;
        private readonly Panel                titlebar;
        private readonly BrandMark            titleMark;
        private readonly Label                appName;
        private readonly InstancePill        instanceLabel;
        private readonly ModernButton         paletteButton;
        private readonly ModernButton         playButton;
        private readonly ModernButton         minimizeButton;
        private readonly ModernButton         maximizeButton;
        private readonly ModernButton         closeButton;
        private readonly Panel                appBody;
        private readonly ModernRail            rail;
        private readonly Panel                contentHost;
        private readonly ModernInspector      inspector;
        private readonly ModernQueueView      queueView;
        private readonly ModernSimpleView     simpleView;
        private readonly ModernStatusBar      statusBar;
        private readonly Panel                overlay;
        private readonly PalettePanel         palette;
        private readonly NotesPanel           notes;
        private readonly ModernProgressSheet  progressSheet;
        private readonly ModernLoadingView loadingView;
        private bool catalogueReady;
        private readonly Timer                 syncTimer;

        private ShellSection activeSection = ShellSection.Discover;
        private bool          inspectorRequested;

        public ModernShell(Main main, ManageMods ownerMods)
        {
            owner      = main;
            manageMods = ownerMods;

            // Every shell rectangle is laid out explicitly. Do not let the
            // inherited WinForms font autoscaler resize that geometry again.
            AutoScaleMode = AutoScaleMode.None;

            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
            Dock      = DockStyle.Fill;
            BackColor = SoftTheme.Backdrop;
            TabStop   = true;

            appSurface = new Panel
            {
                BackColor = SoftTheme.Backdrop,
            };

            titlebar = new Panel
            {
                BackColor = SoftTheme.TitlebarSurface,
                Cursor    = Cursors.SizeAll,
            };
            titlebar.Paint += (sender, e) =>
            {
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    e.Graphics.DrawLine(pen, 0, titlebar.Height - 1,
                                        titlebar.Width, titlebar.Height - 1);
                }
            };
            titleMark = new BrandMark(true);
            appName = MakeLabel("CKAN", SoftTheme.TitleFont, SoftTheme.TextPrimary);
            appName.AutoEllipsis = true;
            instanceLabel = new InstancePill
            {
                Text = Properties.Resources.ModernShellNoInstance,
            };
            paletteButton = new ModernButton
            {
                Text       = Properties.Resources.ModernShellCommandHint,
                Style      = ModernButtonStyle.Search,
                Icon       = ShellIcon.Search,
                KeyHint    = "Ctrl K",
                AccessibleName = Properties.Resources.ModernShellCommandHint,
            };
            playButton = new ModernButton
            {
                Text       = Properties.Resources.ModernShellPlay,
                Style      = ModernButtonStyle.Primary,
                Icon       = ShellIcon.Play,
                AccessibleName = Properties.Resources.ModernShellPlay,
            };
            minimizeButton = new ModernButton
            {
                Style = ModernButtonStyle.Window,
                Icon  = ShellIcon.Minimize,
                AccessibleName = Properties.Resources.ModernShellMinimize,
            };
            maximizeButton = new ModernButton
            {
                Style = ModernButtonStyle.Window,
                Icon  = ShellIcon.Maximize,
                AccessibleName = Properties.Resources.ModernShellMaximize,
            };
            closeButton = new ModernButton
            {
                Style = ModernButtonStyle.Window,
                Icon  = ShellIcon.Close,
                AccessibleName = Properties.Resources.ModernShellClose,
            };

            paletteButton.Click += (sender, e) => TogglePalette();
            playButton.Click    += (sender, e) => manageMods.PlayConfiguredGame();
            minimizeButton.Click += (sender, e) => owner.WindowState = FormWindowState.Minimized;
            maximizeButton.Click += (sender, e) => owner.WindowState = owner.WindowState == FormWindowState.Maximized
                                                               ? FormWindowState.Normal
                                                               : FormWindowState.Maximized;
            closeButton.Click += (sender, e) => owner.Close();
            titlebar.MouseDown += Titlebar_MouseDown;
            foreach (Control control in new Control[]
                     { titleMark, appName, instanceLabel })
            {
                control.MouseDown += Titlebar_MouseDown;
            }
            titlebar.Controls.Add(titleMark);
            titlebar.Controls.Add(appName);
            titlebar.Controls.Add(instanceLabel);
            titlebar.Controls.Add(paletteButton);
            titlebar.Controls.Add(playButton);
            titlebar.Controls.Add(minimizeButton);
            titlebar.Controls.Add(maximizeButton);
            titlebar.Controls.Add(closeButton);

            appBody = new Panel
            {
                BackColor = SoftTheme.Backdrop,
            };
            rail = new ModernRail();
            rail.SectionSelected += Rail_SectionSelected;
            contentHost = new Panel
            {
                BackColor = SoftTheme.Backdrop,
            };

            manageMods.ModernShellMode = true;
            manageMods.Dock      = DockStyle.Fill;
            manageMods.BackColor = SoftTheme.Backdrop;
            if (manageMods.DiscoverView is ModDiscoverView discoverView)
            {
                discoverView.UseConceptSpacing = true;
            }
            // Paint generated art immediately; resolve public thumbnails off-thread.
            manageMods.DiscoverView?.SetArtworkService(new ModVisualMetadataService());
            contentHost.Controls.Add(manageMods);

            var changelog = new Changelog();
            changelog.SetService(new ModChangelogService());
            changelog.SetRegistryProvider(GetCurrentRegistry);
            inspector = new ModernInspector(
                mod => manageMods.ToggleDiscoverMod(mod),
                CloseInspector,
                changelog,
                () => manageMods.MainModList?.Modules?.ToList() ?? new List<GUIMod>(),
                () => owner.CurrentInstance);
            inspector.Visible = false;

            queueView = new ModernQueueView(
                () => owner.ApplyModernChanges(),
                () => manageMods.ClearChangeSet());
            queueView.Visible = false;
            queueView.Dock = DockStyle.Fill;
            contentHost.Controls.Add(queueView);

            simpleView = new ModernSimpleView(
                mods => manageMods.QueueCollection(mods),
                owner.GetModernSetting,
                owner.SetModernSetting,
                owner.ClearModernDownloadCache,
                () => manageMods.DiscoverView?.ClearArtworkCache(),
                owner.RebuildModernRegistry);
            simpleView.Visible = false;
            simpleView.Dock = DockStyle.Fill;
            contentHost.Controls.Add(simpleView);
            loadingView = new ModernLoadingView();
            contentHost.Controls.Add(loadingView);
            loadingView.BringToFront();

            appBody.Controls.Add(contentHost);
            appBody.Controls.Add(inspector);
            appBody.Controls.Add(rail);

            statusBar = new ModernStatusBar(
                () => owner.ApplyModernChanges(),
                () => manageMods.ClearChangeSet(),
                change => manageMods.RemoveChangesetItem(change));

            overlay = new OverlayPanel
            {
                BackColor = Color.Transparent,
                Visible   = false,
                Dock      = DockStyle.Fill,
            };
            palette = new PalettePanel();
            palette.CommandInvoked += Palette_CommandInvoked;
            notes = new NotesPanel(() => ToggleNotes());
            progressSheet = new ModernProgressSheet(() => owner.Wait.CancelCurrentAction());
            overlay.Controls.Add(palette);
            overlay.Controls.Add(notes);
            overlay.Controls.Add(progressSheet);

            appSurface.Controls.Add(titlebar);
            appSurface.Controls.Add(appBody);
            appSurface.Controls.Add(statusBar);
            appSurface.Controls.Add(overlay);

            Controls.Add(appSurface);

            manageMods.DiscoverModActivated += ManageMods_DiscoverModActivated;
            manageMods.OnSelectedModuleChanged += ManageMods_OnSelectedModuleChanged;
            manageMods.OnChangeSetChanged += (changes, conflicts) => SyncState();
            manageMods.OnRegistryChanged += () => { catalogueReady = true; SyncState(); };

            syncTimer = new Timer { Interval = 750 };
            syncTimer.Tick += (sender, e) => SyncState();
            syncTimer.Start();

            Resize += (sender, e) => LayoutShell();
            HandleCreated += (sender, e) => LayoutShell();
            SyncState();
            LayoutShell();
        }

        private static Label MakeLabel(string text, Font font, Color color)
            => new Label
            {
                Text      = text,
                Font      = font,
                ForeColor = color,
                BackColor = Color.Transparent,
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleLeft,
            };

        private float Dpi
        {
            get
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
        }

        private int S(int value) => SoftTheme.ScaleInt(value, Dpi);

        private void LayoutShell()
        {
            appSurface.Bounds = new Rectangle(0, 0,
                                              Math.Max(1, Width),
                                              Math.Max(1, Height));
            LayoutApp();
            Invalidate();
        }

        private void LayoutApp()
        {
            if (appSurface.Width <= 0 || appSurface.Height <= 0)
            {
                return;
            }

            int border = 0;
            int titleHeight = S(52);
            int statusHeight = S(46);
            titlebar.Bounds = new Rectangle(border, border,
                                            Math.Max(1, appSurface.Width - border * 2), titleHeight);
            statusBar.Bounds = new Rectangle(border,
                                             Math.Max(titlebar.Bottom + 1,
                                                      appSurface.Height - statusHeight - border),
                                             Math.Max(1, appSurface.Width - border * 2),
                                             statusHeight);
            appBody.Bounds = new Rectangle(border,
                                           titlebar.Bottom,
                                           Math.Max(1, appSurface.Width - border * 2),
                                           Math.Max(1, statusBar.Top - titlebar.Bottom));

            int railWidth = appBody.Width < S(1050) ? S(64) : S(216);
            rail.Bounds = new Rectangle(0, 0, railWidth, appBody.Height);
            rail.Collapsed = railWidth <= S(64);

            bool showInspector = inspectorRequested
                                 && inspector.Visible
                                 && activeSection != ShellSection.Queue
                                 && activeSection != ShellSection.Collections
                                 && activeSection != ShellSection.Settings;
            int inspectorWidth = showInspector
                               ? Math.Min(S(400), Math.Max(S(300), appBody.Width / 3))
                               : 0;
            bool overlayInspector = showInspector && appBody.Width < S(1150);
            inspector.Visible = showInspector;
            contentHost.Bounds = new Rectangle(rail.Right,
                                               0,
                                               Math.Max(1, appBody.Width - rail.Width - (overlayInspector ? 0 : inspectorWidth)),
                                               appBody.Height);
            if (showInspector)
            {
                inspector.Bounds = new Rectangle(appBody.Width - inspectorWidth,
                                                 0,
                                                 inspectorWidth,
                                                 appBody.Height);
                inspector.BringToFront();
            }
            rail.BringToFront();
            statusBar.BringToFront();
            LayoutTitlebar();
            LayoutOverlay();
        }

        private void LayoutOverlay()
        {
            if (overlay.Width <= 0 || overlay.Height <= 0)
            {
                return;
            }
            palette.Location = new Point(
                Math.Max(S(12), (overlay.Width - palette.Width) / 2),
                S(72));
            progressSheet.Location = new Point(
                Math.Max(S(12), (overlay.Width - progressSheet.Width) / 2),
                Math.Max(S(58), (overlay.Height - progressSheet.Height) / 2));
        }

        private void LayoutTitlebar()
        {
            int h = titlebar.Height;
            int pad = S(14);
            int mark = S(26);
            titleMark.Bounds = new Rectangle(pad, (h - mark) / 2, mark, mark);
            int nameWidth = TextRenderer.MeasureText(appName.Text, appName.Font,
                Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(8);
            appName.Bounds = new Rectangle(titleMark.Right + S(12), 0, nameWidth, h);
            closeButton.Bounds = new Rectangle(titlebar.Width - pad - S(34),
                                                (h - S(30)) / 2, S(34), S(30));
            maximizeButton.Bounds = new Rectangle(closeButton.Left - S(36),
                                                  closeButton.Top, S(34), S(30));
            minimizeButton.Bounds = new Rectangle(maximizeButton.Left - S(36),
                                                  closeButton.Top, S(34), S(30));

            int right = minimizeButton.Left - S(12);
            playButton.Bounds = new Rectangle(Math.Max(S(220), right - S(106)),
                                              (h - S(34)) / 2, S(106), S(34));
            right = playButton.Left - S(10);
            paletteButton.Bounds = new Rectangle(Math.Max(S(350), right - S(292)),
                                                 (h - S(34)) / 2, S(292), S(34));
            int instanceLeft = appName.Right + S(16);
            instanceLabel.Bounds = new Rectangle(instanceLeft,
                                                  (h - S(28)) / 2,
                                                  Math.Max(S(100), Math.Min(S(340),
                                                      TextRenderer.MeasureText(instanceLabel.Text, instanceLabel.Font).Width + S(32))),
                                                  S(28));
            paletteButton.Visible = titlebar.Width >= S(820);
            playButton.Visible = titlebar.Width >= S(650);
        }

        private void Rail_SectionSelected(string key)
        {
            rail.SetActive(key);
            switch (key)
            {
                case "library":
                    activeSection = ShellSection.Library;
                    manageMods.SetDiscoverPage(DiscoverPageMode.Library);
                    manageMods.SetDiscoverDensity(DiscoverDensity.List);
                    break;
                case "updates":
                    activeSection = ShellSection.Updates;
                    manageMods.SetDiscoverPage(DiscoverPageMode.Updates);
                    break;
                case "queue":
                    activeSection = ShellSection.Queue;
                    break;
                case "collections":
                    activeSection = ShellSection.Collections;
                    break;
                case "settings":
                    activeSection = ShellSection.Settings;
                    break;
                default:
                    activeSection = ShellSection.Discover;
                    manageMods.SetDiscoverPage(DiscoverPageMode.Discover);
                    manageMods.SetDiscoverDensity(DiscoverDensity.Compact);
                    break;
            }

            if (activeSection == ShellSection.Queue)
            {
                simpleView.Visible = false;
                queueView.Visible = true;
                manageMods.Visible = false;
            }
            else if (activeSection == ShellSection.Collections)
            {
                manageMods.Visible = false;
                queueView.Visible = false;
                simpleView.Visible = true;
                simpleView.SetContent(Properties.Resources.ModernShellCollectionsTitle,
                                      Properties.Resources.ModernShellCollectionsSubtitle,
                                      Properties.Resources.ModernShellCollectionsEmpty,
                                      null, null);
            }
            else if (activeSection == ShellSection.Settings)
            {
                manageMods.Visible = false;
                queueView.Visible = false;
                simpleView.Visible = true;
                simpleView.SetContent(Properties.Resources.ModernShellSettingsTitle,
                                      Properties.Resources.ModernShellSettingsSubtitle,
                                      Properties.Resources.ModernShellSettingsBody,
                                      null,
                                      null);
                simpleView.SetSecondary(Properties.Resources.ModernShellRefreshCatalogue,
                                        () => owner.RefreshModernCatalogue());
            }
            else
            {
                queueView.Visible = false;
                simpleView.Visible = false;
                manageMods.Visible = true;
                manageMods.BringToFront();
                if (inspectorRequested)
                {
                    inspector.BringToFront();
                }
            }

            inspector.Visible = activeSection == ShellSection.Discover
                                || activeSection == ShellSection.Library
                                || activeSection == ShellSection.Updates;
            LayoutApp();
            SyncState();
        }

        private void ManageMods_DiscoverModActivated(GUIMod mod)
        {
            inspectorRequested = true;
            inspector.Visible = true;
            LayoutApp();
            inspector.SetMod(mod, manageMods.PendingChanges);
        }

        private void CloseInspector()
        {
            inspectorRequested = false;
            inspector.Visible = false;
            LayoutApp();
        }

        private void ManageMods_OnSelectedModuleChanged(GUIMod? mod)
        {
            // Registry/grid refreshes also change selection. Only an explicit
            // card activation should open the details pane.
            if (inspectorRequested && mod != null && activeSection != ShellSection.Queue
                && activeSection != ShellSection.Collections
                && activeSection != ShellSection.Settings)
            {
                inspectorRequested = true;
                inspector.Visible = true;
                LayoutApp();
                inspector.SetMod(mod, manageMods.PendingChanges);
            }
        }

        private IRegistryQuerier? GetCurrentRegistry()
        {
            if (owner.CurrentInstance is GameInstance instance)
            {
                var repoData = ServiceLocator.Container.Resolve<RepositoryDataManager>();
                return RegistryManager.Instance(instance, repoData).registry;
            }
            return null;
        }

        private void SyncState()
        {
            var modules = manageMods.MainModList?.Modules?.ToList()
                          ?? new List<GUIMod>();
            var instance = owner.CurrentInstance;
            string instanceText = instance == null
                                ? Properties.Resources.ModernShellNoInstance
                                : string.Format("{0} {1} · {2}",
                                                instance.Game.ShortName,
                                                instance.Version()?.ToString() ?? "",
                                                instance.Name);
            if (instanceLabel.Text != instanceText)
            {
                instanceLabel.Text = instanceText;
                LayoutTitlebar();
            }
            int installed = modules.Count(m => m.IsInstalled);
            long gameDataBytes = modules.Where(m => m.IsInstalled)
                                        .Sum(m => m.Module.install_size);
            string gameData = gameDataBytes > 0 ? CkanModule.FmtSize(gameDataBytes) : "—";
            rail.SetCounts(modules.Count,
                           installed,
                           modules.Count(m => m.HasUpdate),
                           manageMods.PendingChanges.Count,
                           gameData);
            statusBar.SetData(modules, instance, manageMods.PendingChanges,
                              owner.Waiting);
            simpleView.SetData(modules, instance);
            queueView.SetChanges(manageMods.PendingChanges);
            inspector.SetModules(modules);
            inspector.SetChanges(manageMods.PendingChanges);
            progressSheet.SetChanges(manageMods.PendingChanges);
            bool busy = owner.Waiting;
            catalogueReady |= modules.Count > 0;
            bool loading = !catalogueReady || (busy && manageMods.PendingChanges.Count == 0);
            loadingView.Visible = loading;
            if (loading)
            {
                loadingView.UpdateProgress(owner.Wait.CurrentProgress,
                    !catalogueReady || owner.Wait.CurrentProgressIndeterminate,
                    owner.Wait.CurrentProgressText);
                loadingView.BringToFront();
            }
            progressSheet.Visible = busy && !loading;
            if (progressSheet.Visible)
            {
                progressSheet.SetData(owner.Wait.CurrentProgress,
                                      owner.Wait.CurrentProgressIndeterminate,
                                      owner.Wait.CurrentProgressText);
                progressSheet.BringToFront();
            }
            overlay.Visible = progressSheet.Visible || notes.Visible || palette.Visible;
        }

        private void ToggleNotes()
        {
            bool visible = !notes.Visible;
            notes.Visible = visible;
            if (visible)
            {
                notes.ResetSelection();
            }
            overlay.Visible = visible || palette.Visible || progressSheet.Visible;
            if (visible)
            {
                notes.BringToFront();
                notes.Focus();
            }
            Invalidate(true);
        }

        private void TogglePalette()
        {
            bool visible = !palette.Visible;
            if (visible && notes.Visible)
            {
                notes.Visible = false;
                notes.ResetSelection();
            }
            palette.Visible = visible;
            overlay.Visible = visible || notes.Visible || progressSheet.Visible;
            if (visible)
            {
                palette.BringToFront();
                palette.Open(BuildCommands());
            }
            else
            {
                palette.Close();
            }
            Invalidate(true);
        }

        private IEnumerable<PaletteCommand> BuildCommands()
        {
            var commands = new List<PaletteCommand>
            {
                new PaletteCommand(Properties.Resources.ModernShellCommandDiscover,
                                   Properties.Resources.ModernShellCatalogue,
                                   () => Rail_SectionSelected("discover"),
                                   ShellIcon.Discover),
                new PaletteCommand(Properties.Resources.ModernShellCommandLibrary,
                                   Properties.Resources.ModernShellLibrary,
                                   () => Rail_SectionSelected("library"),
                                   ShellIcon.Library),
                new PaletteCommand(Properties.Resources.ModernShellCommandUpdates,
                                   Properties.Resources.ModernShellUpdates,
                                   () => Rail_SectionSelected("updates"),
                                   ShellIcon.Updates),
                new PaletteCommand(Properties.Resources.ModernShellCommandQueue,
                                   Properties.Resources.ModernShellQueue,
                                   () => Rail_SectionSelected("queue"),
                                   ShellIcon.Queue),
                new PaletteCommand(Properties.Resources.ModernShellCollections,
                                   Properties.Resources.ModernShellCollections,
                                   () => Rail_SectionSelected("collections"),
                                   ShellIcon.Collections),
                new PaletteCommand(Properties.Resources.ModernShellCommandSettings,
                                   Properties.Resources.ModernShellSettings,
                                   () => Rail_SectionSelected("settings"),
                                   ShellIcon.Settings),
                new PaletteCommand(Properties.Resources.ModernShellCommandRefresh,
                                   Properties.Resources.ModernShellCatalogue,
                                   () => owner.RefreshModernCatalogue(),
                                   ShellIcon.Search),
                new PaletteCommand(Properties.Resources.ModernShellCommandPlay,
                                   Properties.Resources.ModernShellPlay,
                                   () => manageMods.PlayConfiguredGame(),
                                   ShellIcon.Play),
                new PaletteCommand(string.Format(Properties.Resources.ModernShellApplyChanges,
                                                 manageMods.PendingChanges.Count),
                                   Properties.Resources.ModernShellQueue,
                                   () => owner.ApplyModernChanges(),
                                   ShellIcon.Check),
                new PaletteCommand(Properties.Resources.ModernShellDiscard,
                                   Properties.Resources.ModernShellQueue,
                                   () => manageMods.ClearChangeSet(),
                                   ShellIcon.Close),
                new PaletteCommand(Properties.Resources.DiscoverQueueAllUpdates,
                                   Properties.Resources.ModernShellUpdates,
                                   () => manageMods.QueueAllUpdates(),
                                   ShellIcon.Updates),
                new PaletteCommand(Properties.Resources.DiscoverDensityCompact,
                                   Properties.Resources.DiscoverDensityCompact,
                                   () => SetPaletteDensity(DiscoverDensity.Compact),
                                   ShellIcon.Discover),
                new PaletteCommand(Properties.Resources.DiscoverDensityList,
                                   Properties.Resources.DiscoverDensityList,
                                   () => SetPaletteDensity(DiscoverDensity.List),
                                   ShellIcon.Queue),
                new PaletteCommand(Properties.Resources.DiscoverDensityTable,
                                   Properties.Resources.DiscoverDensityTable,
                                   () => SetPaletteDensity(DiscoverDensity.Table),
                                   ShellIcon.Collections),
                new PaletteCommand(Properties.Resources.ModernShellToggleTheme,
                                   Properties.Resources.ModernShellThemeLightDark,
                                   () => ToggleTheme(),
                                   ShellIcon.Sun),
            };

            foreach (var item in (manageMods.MainModList?.Modules
                                  ?? Enumerable.Empty<GUIMod>())
                                 .OrderBy(mod => mod.Name,
                                          StringComparer.CurrentCultureIgnoreCase)
                                 .Take(12))
            {
                var mod = item;
                commands.Add(new PaletteCommand(mod.Name,
                    mod.IsInstalled
                        ? Properties.Resources.DiscoverFilterInstalled
                        : ModDiscoverView.CategoryLabel(ModDiscoverView.CategoryFor(mod)),
                    () =>
                    {
                        Rail_SectionSelected("discover");
                        manageMods.SelectDiscoverMod(mod);
                    },
                    ShellIcon.Discover));
            }

            return commands;
        }

        private void SetPaletteDensity(DiscoverDensity density)
        {
            Rail_SectionSelected("discover");
            manageMods.SetDiscoverDensity(density);
        }

        private void Palette_CommandInvoked(PaletteCommand command)
        {
            palette.Close();
            palette.Visible = false;
            overlay.Visible = notes.Visible || progressSheet.Visible;
            command.Action();
        }

        private void ToggleTheme()
        {
            SoftTheme.SetDark(!SoftTheme.IsDark);
            manageMods.DiscoverView?.RefreshTheme();
            appName.ForeColor = SoftTheme.TextPrimary;
            instanceLabel.ForeColor = SoftTheme.TextSecondary;
            appSurface.BackColor = SoftTheme.Backdrop;
            titlebar.BackColor = SoftTheme.TitlebarSurface;
            appBody.BackColor = SoftTheme.Backdrop;
            contentHost.BackColor = SoftTheme.Backdrop;
            rail.RefreshTheme();
            inspector.RefreshTheme();
            queueView.RefreshTheme();
            simpleView.RefreshTheme();
            statusBar.RefreshTheme();
            progressSheet.RefreshTheme();
            notes.RefreshTheme();
            palette.RefreshTheme();
            SyncState();
            Invalidate(true);
        }

        private void Titlebar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < titlebar.Height
                && !(sender is ModernButton))
            {
                ReleaseCapture();
                SendMessage(owner.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.K))
            {
                TogglePalette();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                if (palette.Visible)
                {
                    TogglePalette();
                    return true;
                }
                if (notes.Visible)
                {
                    ToggleNotes();
                    return true;
                }
                if (inspectorRequested)
                {
                    inspectorRequested = false;
                    inspector.Visible = false;
                    LayoutApp();
                    return true;
                }
            }
            Control? focused = owner.ActiveControl;
            while (focused is ContainerControl container && container.ActiveControl != null)
            {
                focused = container.ActiveControl;
            }
            if (focused is TextBoxBase || focused is ComboBox)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyData == Keys.T)
            {
                ToggleTheme();
                return true;
            }
            if (keyData == Keys.V)
            {
                DiscoverDensity next = (DiscoverDensity)(((int)(manageMods.DiscoverView?.Density
                                                         ?? DiscoverDensity.Compact) + 1) % 3);
                manageMods.SetDiscoverDensity(next);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.Clear(SoftTheme.Backdrop);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                syncTimer.Stop();
                syncTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION       = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private enum ShellIcon
        {
            None,
            Search,
            Play,
            Info,
            Sun,
            Minimize,
            Maximize,
            Close,
            Discover,
            Library,
            Updates,
            Queue,
            Collections,
            Settings,
            Check,
        }

        private enum ModernButtonStyle
        {
            Ghost,
            Primary,
            Search,
            Window,
            Soft,
            Tab,
        }

        private sealed class BrandMark : Control
        {
            private readonly bool small;

            public BrandMark(bool smallMark)
            {
                small = smallMark;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
                AccessibleName = "CKAN";
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                using (var path = SoftTheme.RoundedPath(rect,
                                                        small ? SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi)
                                                              : SoftTheme.ScaleInt(15, SoftTheme.LayoutDpi)))
                using (var brush = new LinearGradientBrush(rect,
                           SoftTheme.Accent, Color.FromArgb(122, 94, 246), 145f))
                {
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, small ? "CK" : "CKAN",
                                      small ? SoftTheme.MetaFont : SoftTheme.PillFont,
                                      rect, Color.White,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPadding);
            }
        }

        private sealed class InstancePill : Control
        {
            public InstancePill()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);
                Font = SoftTheme.MetaFont;
                ForeColor = SoftTheme.TextSecondary;
                BackColor = Color.Transparent;
                AccessibleName = Properties.Resources.ModernShellInstance;
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentBack = Parent?.BackColor ?? SoftTheme.Surface;
                if (parentBack == Color.Transparent)
                {
                    parentBack = SoftTheme.Surface;
                }
                g.Clear(parentBack);
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                         Math.Max(1, Height - 1));
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceSunken, 999);
                SoftTheme.DrawRounded(g, rect, SoftTheme.Border, 999, 1f);
                TextRenderer.DrawText(g, Text ?? "", Font,
                                      new Rectangle(14, 0, Math.Max(1, Width - 22), Height),
                                      ForeColor,
                                      TextFormatFlags.Left
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
            }
        }

        private sealed class ModernButton : Control
        {
            private bool hovered;
            private bool pressed;

            public ModernButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw
                         | ControlStyles.Selectable, true);
                Font = SoftTheme.PillButtonFont;
                ForeColor = SoftTheme.TextSecondary;
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                TabStop = true;
                Height = SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi);
            }

            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public ModernButtonStyle Style { get; set; }
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public ShellIcon Icon { get; set; }
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public string KeyHint { get; set; } = "";
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public string MetaText { get; set; } = "";
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Glass { get; set; }
            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Active { get; set; }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Color parentBack = Parent?.BackColor ?? SoftTheme.Backdrop;
                if (parentBack == Color.Transparent)
                {
                    parentBack = SoftTheme.Backdrop;
                }
                g.Clear(parentBack);
                int radius = SoftTheme.RadiusControl;
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                Color back;
                Color fore;
                Color border = SoftTheme.Border;
                switch (Style)
                {
                    case ModernButtonStyle.Primary:
                        back = pressed ? SoftTheme.AccentDeep
                             : hovered ? SoftTheme.Mix(SoftTheme.Accent,
                                                       SoftTheme.TextPrimary, 0.08f)
                                      : SoftTheme.Accent;
                        fore = SoftTheme.OnAccent;
                        border = back;
                        break;
                    case ModernButtonStyle.Search:
                        back = hovered ? SoftTheme.SurfaceHover : SoftTheme.SurfaceSunken;
                        fore = SoftTheme.TextMuted;
                        break;
                    case ModernButtonStyle.Soft:
                        back = hovered ? SoftTheme.SurfaceHover : SoftTheme.SurfaceSunken;
                        fore = SoftTheme.TextPrimary;
                        break;
                    case ModernButtonStyle.Window:
                        back = hovered ? (Icon == ShellIcon.Close ? SoftTheme.Danger
                                                                    : SoftTheme.SurfaceHover)
                                       : Color.Transparent;
                        fore = hovered && Icon == ShellIcon.Close
                             ? Color.White : SoftTheme.TextMuted;
                        radius = 8;
                        break;
                    case ModernButtonStyle.Tab:
                        back = Color.Transparent;
                        fore = Active ? SoftTheme.TextPrimary : SoftTheme.TextMuted;
                        border = Color.Transparent;
                        radius = 0;
                        break;
                    default:
                        back = hovered ? SoftTheme.SurfaceHover
                                       : Glass
                                         ? SoftTheme.Mix(SoftTheme.Backdrop,
                                                         SoftTheme.Surface, 0.70f)
                                         : Color.Transparent;
                        fore = SoftTheme.TextSecondary;
                        break;
                }
                if (back != Color.Transparent)
                {
                    SoftTheme.FillRounded(g, rect, back, radius);
                }
                if (Style != ModernButtonStyle.Window
                    && Style != ModernButtonStyle.Tab)
                {
                    SoftTheme.DrawRounded(g, rect, border, radius, 1f);
                }

                if (Style == ModernButtonStyle.Window)
                {
                    DrawIcon(g, rect, Icon, fore);
                    return;
                }

                int iconWidth = Icon == ShellIcon.None ? 0 : SoftTheme.ScaleInt(16, SoftTheme.LayoutDpi);
                int left = SoftTheme.ScaleInt(11, SoftTheme.LayoutDpi);
                if (Icon != ShellIcon.None)
                {
                    DrawIcon(g, new Rectangle(left, 0, iconWidth, Height), Icon, fore);
                    left += iconWidth + SoftTheme.ScaleInt(7, SoftTheme.LayoutDpi);
                }
                int right = Width - SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi);
                if (!string.IsNullOrEmpty(KeyHint))
                {
                    int keyWidth = TextRenderer.MeasureText(KeyHint, SoftTheme.MetaFont).Width
                                   + SoftTheme.ScaleInt(9, SoftTheme.LayoutDpi);
                    var keyRect = new Rectangle(Math.Max(left, right - keyWidth),
                                                SoftTheme.ScaleInt(7, SoftTheme.LayoutDpi), keyWidth,
                                                Math.Max(1, Height - SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi)));
                    SoftTheme.FillRounded(g, keyRect, SoftTheme.SurfaceSunken, 5);
                    TextRenderer.DrawText(g, KeyHint, SoftTheme.MetaFont, keyRect,
                                          SoftTheme.TextMuted,
                                          TextFormatFlags.HorizontalCenter
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                    right = keyRect.Left - SoftTheme.ScaleInt(7, SoftTheme.LayoutDpi);
                }
                if (!string.IsNullOrEmpty(MetaText))
                {
                    int metaWidth = TextRenderer.MeasureText(MetaText, SoftTheme.MetaFont).Width;
                    var metaRect = new Rectangle(Math.Max(left, right - metaWidth),
                                                 0, Math.Max(1, metaWidth), Height);
                    TextRenderer.DrawText(g, MetaText, SoftTheme.MetaFont, metaRect,
                                          SoftTheme.TextMuted,
                                          TextFormatFlags.Right
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.EndEllipsis
                                          | TextFormatFlags.NoPadding);
                    right = metaRect.Left - SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi);
                }
                var textRect = new Rectangle(left, 0, Math.Max(1, right - left), Height);
                TextRenderer.DrawText(g, Text ?? "", Font, textRect, fore,
                                      TextFormatFlags.Left
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
                if (Style == ModernButtonStyle.Tab && Active)
                {
                    using (var pen = new Pen(SoftTheme.Accent, Math.Max(1f,
                                                                         SoftTheme.Scale(2f, SoftTheme.LayoutDpi))))
                    {
                        g.DrawLine(pen, SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi), Height - 2,
                                   Math.Max(SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi),
                                            Width - SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi)), Height - 2);
                    }
                }
            }

            private static void DrawIcon(Graphics g, Rectangle bounds, ShellIcon icon, Color color)
            {
                float scale = Math.Max(0.75f, bounds.Height / 30f);
                int cx = bounds.Left + bounds.Width / 2;
                int cy = bounds.Top + bounds.Height / 2;
                using (var pen = new Pen(color, Math.Max(1f, scale * 1.35f)))
                using (var brush = new SolidBrush(color))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    switch (icon)
                    {
                        case ShellIcon.Search:
                            g.DrawEllipse(pen, cx - 6, cy - 7, 11, 11);
                            g.DrawLine(pen, cx + 3, cy + 3, cx + 8, cy + 8);
                            break;
                        case ShellIcon.Play:
                            g.FillPolygon(brush, new[]
                            {
                                new Point(cx - 3, cy - 7), new Point(cx + 7, cy),
                                new Point(cx - 3, cy + 7),
                            });
                            break;
                        case ShellIcon.Info:
                            g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                            TextRenderer.DrawText(g, "i", SoftTheme.PillFont,
                                                  new Rectangle(cx - 4, cy - 6, 8, 14), color,
                                                  TextFormatFlags.HorizontalCenter
                                                  | TextFormatFlags.VerticalCenter
                                                  | TextFormatFlags.NoPadding);
                            break;
                        case ShellIcon.Sun:
                            g.DrawEllipse(pen, cx - 4, cy - 4, 8, 8);
                            for (int i = 0; i < 8; ++i)
                            {
                                double angle = i * Math.PI / 4.0;
                                int x1 = cx + (int)(Math.Cos(angle) * 8);
                                int y1 = cy + (int)(Math.Sin(angle) * 8);
                                int x2 = cx + (int)(Math.Cos(angle) * 11);
                                int y2 = cy + (int)(Math.Sin(angle) * 11);
                                g.DrawLine(pen, x1, y1, x2, y2);
                            }
                            break;
                        case ShellIcon.Minimize:
                            g.DrawLine(pen, cx - 6, cy + 4, cx + 6, cy + 4);
                            break;
                        case ShellIcon.Maximize:
                            g.DrawRectangle(pen, cx - 6, cy - 6, 12, 12);
                            break;
                        case ShellIcon.Close:
                            g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                            g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                            break;
                        case ShellIcon.Check:
                            g.DrawLines(pen, new[]
                            {
                                new Point(cx - 6, cy), new Point(cx - 2, cy + 4),
                                new Point(cx + 7, cy - 5),
                            });
                            break;
                        case ShellIcon.Discover:
                            g.DrawLine(pen, cx - 6, cy - 6, cx + 2, cy - 6);
                            g.DrawLine(pen, cx - 8, cy - 2, cx + 1, cy - 2);
                            g.DrawLine(pen, cx - 6, cy + 2, cx + 3, cy + 2);
                            g.DrawLine(pen, cx - 4, cy + 6, cx + 5, cy + 6);
                            break;
                        case ShellIcon.Library:
                            for (int y = 0; y < 2; ++y)
                            {
                                for (int x = 0; x < 2; ++x)
                                {
                                    g.DrawRoundedRectangle(pen,
                                        new Rectangle(cx - 8 + x * 9, cy - 8 + y * 9, 6, 6), 2);
                                }
                            }
                            break;
                        case ShellIcon.Updates:
                            g.DrawLine(pen, cx, cy + 7, cx, cy - 7);
                            g.DrawLines(pen, new[]
                            {
                                new Point(cx - 4, cy - 3), new Point(cx, cy - 7),
                                new Point(cx + 4, cy - 3),
                            });
                            g.DrawLine(pen, cx - 5, cy + 7, cx + 5, cy + 7);
                            break;
                        case ShellIcon.Queue:
                            g.DrawLine(pen, cx - 7, cy - 5, cx + 7, cy - 5);
                            g.DrawLine(pen, cx - 7, cy, cx + 4, cy);
                            g.DrawLine(pen, cx - 7, cy + 5, cx + 1, cy + 5);
                            break;
                        case ShellIcon.Collections:
                            g.DrawRectangle(pen, cx - 7, cy - 6, 11, 12);
                            g.DrawLine(pen, cx - 4, cy - 9, cx + 7, cy - 9);
                            break;
                        case ShellIcon.Settings:
                            g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                            g.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                            for (int i = 0; i < 8; ++i)
                            {
                                double angle = i * Math.PI / 4.0;
                                int x1 = cx + (int)(Math.Cos(angle) * 7);
                                int y1 = cy + (int)(Math.Sin(angle) * 7);
                                int x2 = cx + (int)(Math.Cos(angle) * 10);
                                int y2 = cy + (int)(Math.Sin(angle) * 10);
                                g.DrawLine(pen, x1, y1, x2, y2);
                            }
                            break;
                    }
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                hovered = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovered = false;
                pressed = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    pressed = true;
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    pressed = false;
                    Invalidate();
                }
                base.OnMouseUp(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    OnClick(EventArgs.Empty);
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }
        }

        private sealed class ModernRail : Panel
        {
            private readonly Label catalogueLabel;
            private readonly Label instanceGroupLabel;
            private readonly Label footerLabel;
            private readonly Dictionary<string, RailButton> buttons =
                new Dictionary<string, RailButton>(StringComparer.Ordinal);
            private bool collapsed;

            public ModernRail()
            {
                BackColor = SoftTheme.RailSurface;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw, true);
                catalogueLabel = MakeGroupLabel(Properties.Resources.ModernShellCatalogue);
                instanceGroupLabel = MakeGroupLabel(Properties.Resources.ModernShellInstance);
                footerLabel = new Label
                {
                    Font = SoftTheme.MetaFont,
                    ForeColor = SoftTheme.TextMuted,
                    BackColor = Color.Transparent,
                    AutoSize = false,
                    TextAlign = ContentAlignment.TopLeft,
                };
                AddButton("discover", Properties.Resources.DiscoverTitle, ShellIcon.Discover);
                AddButton("library", Properties.Resources.ModernShellLibrary, ShellIcon.Library);
                AddButton("updates", Properties.Resources.DiscoverFilterUpdates, ShellIcon.Updates);
                AddButton("queue", Properties.Resources.ModernShellQueue, ShellIcon.Queue);
                AddButton("collections", Properties.Resources.ModernShellCollections, ShellIcon.Collections);
                AddButton("settings", Properties.Resources.ModernShellSettings, ShellIcon.Settings);
                Controls.Add(catalogueLabel);
                Controls.Add(instanceGroupLabel);
                Controls.Add(footerLabel);
                SetActive("discover");
            }

            public event Action<string>? SectionSelected;

            [System.ComponentModel.DesignerSerializationVisibility(
                System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public bool Collapsed
            {
                get => collapsed;
                set
                {
                    if (collapsed != value)
                    {
                        collapsed = value;
                        foreach (var button in buttons.Values)
                        {
                            button.Collapsed = collapsed;
                        }
                        catalogueLabel.Visible = !collapsed;
                        instanceGroupLabel.Visible = !collapsed;
                        footerLabel.Visible = !collapsed;
                        PerformLayout();
                    }
                }
            }

            private static Label MakeGroupLabel(string text)
                => new Label
                {
                    Text = text.ToUpperInvariant(),
                    Font = SoftTheme.MetaFont,
                    ForeColor = SoftTheme.TextMuted,
                    BackColor = Color.Transparent,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                };

            private void AddButton(string key, string text, ShellIcon icon)
            {
                var button = new RailButton(text, icon)
                {
                    Dock = DockStyle.None,
                    AccessibleName = text,
                };
                button.Clicked += (sender, e) =>
                {
                    SetActive(key);
                    SectionSelected?.Invoke(key);
                };
                buttons[key] = button;
                Controls.Add(button);
            }

            public void SetActive(string key)
            {
                foreach (var pair in buttons)
                {
                    pair.Value.Active = string.Equals(pair.Key, key,
                                                      StringComparison.Ordinal);
                    pair.Value.Invalidate();
                }
            }

            public void SetCounts(int total, int installed, int updates, int queued,
                                  string gameData)
            {
                string key = installed + ":" + updates + ":" + queued + ":" + gameData;
                if (countsKey == key) return;
                countsKey = key;
                buttons["library"].Count = installed;
                buttons["library"].ShowCount = installed > 0;
                buttons["updates"].Count = updates;
                buttons["updates"].ShowCount = updates > 0;
                buttons["updates"].HotCount = updates > 0;
                buttons["queue"].Count = queued;
                buttons["queue"].ShowCount = queued > 0;
                string updated = DateTime.Now.ToString("h tt");
                footerLabel.Text = string.Format(Properties.Resources.ModernShellModsInstalled,
                                                  installed)
                                   + Environment.NewLine
                                   + string.Format(Properties.Resources.ModernShellGameData,
                                                   gameData)
                                   + Environment.NewLine
                                   + string.Format(Properties.Resources.ModernShellRegistryUpdated,
                                                   updated);
                Invalidate(true);
            }

            private string countsKey = "";

            public void RefreshTheme()
            {
                BackColor = SoftTheme.RailSurface;
                catalogueLabel.ForeColor = SoftTheme.TextMuted;
                instanceGroupLabel.ForeColor = SoftTheme.TextMuted;
                footerLabel.ForeColor = SoftTheme.TextMuted;
                foreach (var button in buttons.Values)
                {
                    button.ForeColor = SoftTheme.TextSecondary;
                    button.Invalidate();
                }
                Invalidate(true);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int pad = collapsed ? 0 : SoftTheme.ScaleInt(12, SoftTheme.LayoutDpi);
                int top = collapsed ? SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi)
                                    : SoftTheme.ScaleInt(26, SoftTheme.LayoutDpi);
                if (!collapsed)
                {
                    catalogueLabel.Bounds = new Rectangle(pad + 10, top, Width - pad * 2 - 20, 20);
                    top = catalogueLabel.Bottom + 6;
                }
                foreach (string key in new[] { "discover", "library", "updates", "queue", "collections" })
                {
                    if (!buttons.TryGetValue(key, out var button))
                    {
                        continue;
                    }
                    button.Bounds = new Rectangle(collapsed ? 0 : pad,
                                                  top,
                                                  Math.Max(1, Width - (collapsed ? 0 : pad * 2)),
                                                  SoftTheme.ScaleInt(40, SoftTheme.LayoutDpi));
                    top = button.Bottom + 3;
                }
                if (!collapsed)
                {
                    instanceGroupLabel.Bounds = new Rectangle(pad + 10, top + 10,
                                                               Width - pad * 2 - 20, 20);
                    top = instanceGroupLabel.Bottom + 3;
                }
                if (!buttons.TryGetValue("settings", out var settings))
                {
                    return;
                }
                settings.Bounds = new Rectangle(collapsed ? 0 : pad,
                                                top,
                                                Math.Max(1, Width - (collapsed ? 0 : pad * 2)),
                                                SoftTheme.ScaleInt(40, SoftTheme.LayoutDpi));
                int footerHeight = SoftTheme.ScaleInt(88, SoftTheme.LayoutDpi);
                footerLabel.Bounds = new Rectangle(pad + 10,
                                                   Math.Max(settings.Bottom + 10,
                                                            Height - footerHeight),
                                                   Math.Max(1, Width - pad * 2 - 20),
                                                   footerHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                }
                if (!collapsed)
                {
                    int y = footerLabel.Top - 8;
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        e.Graphics.DrawLine(pen, 12, y, Width - 12, y);
                    }
                }
            }

            private sealed class RailButton : Control
            {
                private readonly string label;
                private readonly ShellIcon icon;
                private bool hovered;

                public RailButton(string text, ShellIcon glyph)
                {
                    label = text;
                    icon  = glyph;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    Cursor = Cursors.Hand;
                    Font = SoftTheme.SearchFont;
                    ForeColor = SoftTheme.TextSecondary;
                    BackColor = Color.Transparent;
                }

                [System.ComponentModel.DesignerSerializationVisibility(
                    System.ComponentModel.DesignerSerializationVisibility.Hidden)]
                public bool Active { get; set; }
                [System.ComponentModel.DesignerSerializationVisibility(
                    System.ComponentModel.DesignerSerializationVisibility.Hidden)]
                public int Count { get; set; }
                [System.ComponentModel.DesignerSerializationVisibility(
                    System.ComponentModel.DesignerSerializationVisibility.Hidden)]
                public bool ShowCount { get; set; }
                [System.ComponentModel.DesignerSerializationVisibility(
                    System.ComponentModel.DesignerSerializationVisibility.Hidden)]
                public bool HotCount { get; set; }
                [System.ComponentModel.DesignerSerializationVisibility(
                    System.ComponentModel.DesignerSerializationVisibility.Hidden)]
                public bool Collapsed { get; set; }
                public event EventHandler? Clicked;

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Parent?.BackColor ?? SoftTheme.Surface);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                             Math.Max(1, Height - 1));
                    Color background = Active ? SoftTheme.AccentSoft
                                     : hovered ? SoftTheme.SurfaceHover
                                               : Color.Transparent;
                    if (background != Color.Transparent)
                    {
                        SoftTheme.FillRounded(g, rect, background, 10);
                    }
                    Color foreground = Active ? SoftTheme.TextPrimary : SoftTheme.TextSecondary;
                    DrawRailIcon(g, new Rectangle(Collapsed ? (Width - 20) / 2 : 12,
                                                   (Height - 20) / 2, 20, 20), icon,
                                 Active ? SoftTheme.Accent : foreground);
                    if (!Collapsed)
                    {
                        TextRenderer.DrawText(g, label, Font,
                                              new Rectangle(43, 0,
                                                            Math.Max(1, Width - 94), Height),
                                              foreground,
                                              TextFormatFlags.Left
                                              | TextFormatFlags.VerticalCenter
                                              | TextFormatFlags.EndEllipsis
                                              | TextFormatFlags.NoPadding);
                        if (ShowCount)
                        {
                            int width = Math.Max(22, TextRenderer.MeasureText(Count.ToString(),
                                                                                 SoftTheme.MetaFont).Width + 12);
                            var badge = new Rectangle(Width - width - 10,
                                                      (Height - 22) / 2, width, 22);
                            SoftTheme.FillRounded(g, badge,
                                HotCount ? SoftTheme.WarningSoft : SoftTheme.SurfaceSunken, 11);
                            TextRenderer.DrawText(g, Count.ToString(), SoftTheme.MetaFont,
                                                  badge,
                                                  HotCount ? SoftTheme.Warning : SoftTheme.TextMuted,
                                                  TextFormatFlags.HorizontalCenter
                                                  | TextFormatFlags.VerticalCenter
                                                  | TextFormatFlags.NoPadding);
                        }
                    }
                }

                private static void DrawRailIcon(Graphics g, Rectangle b, ShellIcon icon, Color color)
                {
                    using (var pen = new Pen(color, 1.5f))
                    using (var brush = new SolidBrush(color))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        int cx = b.Left + b.Width / 2;
                        int cy = b.Top + b.Height / 2;
                        switch (icon)
                        {
                            case ShellIcon.Discover:
                                g.DrawLine(pen, cx - 6, cy - 6, cx + 2, cy - 6);
                                g.DrawLine(pen, cx - 8, cy - 2, cx + 1, cy - 2);
                                g.DrawLine(pen, cx - 6, cy + 2, cx + 3, cy + 2);
                                g.DrawLine(pen, cx - 4, cy + 6, cx + 5, cy + 6);
                                break;
                            case ShellIcon.Library:
                                for (int y = 0; y < 2; ++y)
                                {
                                    for (int x = 0; x < 2; ++x)
                                    {
                                        g.DrawRoundedRectangle(pen,
                                            new Rectangle(cx - 8 + x * 9, cy - 8 + y * 9, 6, 6), 2);
                                    }
                                }
                                break;
                            case ShellIcon.Updates:
                                g.DrawLine(pen, cx, cy + 7, cx, cy - 7);
                                g.DrawLines(pen, new[]
                                {
                                    new Point(cx - 4, cy - 3), new Point(cx, cy - 7),
                                    new Point(cx + 4, cy - 3),
                                });
                                g.DrawLine(pen, cx - 5, cy + 7, cx + 5, cy + 7);
                                break;
                            case ShellIcon.Queue:
                                g.DrawLine(pen, cx - 7, cy - 5, cx + 7, cy - 5);
                                g.DrawLine(pen, cx - 7, cy, cx + 4, cy);
                                g.DrawLine(pen, cx - 7, cy + 5, cx + 1, cy + 5);
                                break;
                            case ShellIcon.Collections:
                                g.DrawRectangle(pen, cx - 7, cy - 6, 11, 12);
                                g.DrawLine(pen, cx - 4, cy - 9, cx + 7, cy - 9);
                                break;
                            case ShellIcon.Settings:
                                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                                g.FillEllipse(brush, cx - 2, cy - 2, 4, 4);
                                for (int i = 0; i < 8; ++i)
                                {
                                    double angle = i * Math.PI / 4.0;
                                    int x1 = cx + (int)(Math.Cos(angle) * 7);
                                    int y1 = cy + (int)(Math.Sin(angle) * 7);
                                    int x2 = cx + (int)(Math.Cos(angle) * 10);
                                    int y2 = cy + (int)(Math.Sin(angle) * 10);
                                    g.DrawLine(pen, x1, y1, x2, y2);
                                }
                                break;
                        }
                    }
                }

                protected override void OnMouseEnter(EventArgs e)
                {
                    hovered = true;
                    Invalidate();
                    base.OnMouseEnter(e);
                }

                protected override void OnMouseLeave(EventArgs e)
                {
                    hovered = false;
                    Invalidate();
                    base.OnMouseLeave(e);
                }

                protected override void OnMouseDown(MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        Clicked?.Invoke(this, EventArgs.Empty);
                    }
                    base.OnMouseDown(e);
                }
            }
        }

        private sealed class LegacyModernInspector : Panel
        {
            private enum InspectorTab
            {
                Overview,
                Changelog,
                Relationships,
                Files,
            }

            private readonly Action<GUIMod> toggleAction;
            private readonly Action closeAction;
            private readonly Changelog changelog;
            private Image? cover;
            private GUIMod? mod;
            private IReadOnlyList<ModChange> changes = Array.Empty<ModChange>();
            private readonly FlowLayoutPanel flow;
            private readonly ModernButton closeButton;
            private string lastChangeKey = "";
            private InspectorTab selectedTab = InspectorTab.Overview;

            public LegacyModernInspector(Action<GUIMod> toggle, Action close,
                                         Changelog changelogControl)
            {
                toggleAction = toggle;
                closeAction = close;
                changelog = changelogControl;
                BackColor = SoftTheme.Surface;
                Padding = new Padding(SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi));
                flow = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    Dock = DockStyle.Fill,
                    BackColor = SoftTheme.Surface,
                    Padding = new Padding(0, SoftTheme.ScaleInt(44, SoftTheme.LayoutDpi),
                                           SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi), SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi)),
                    Margin = new Padding(0),
                };
                Controls.Add(flow);
                closeButton = new ModernButton
                {
                    Style = ModernButtonStyle.Window,
                    Icon = ShellIcon.Close,
                    AccessibleName = Properties.Resources.ModernShellClose,
                };
                closeButton.Click += (sender, e) => closeAction();
                Controls.Add(closeButton);
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer, true);
            }

            public void SetMod(GUIMod value, IReadOnlyList<ModChange> pending)
            {
                mod = value;
                changes = pending ?? Array.Empty<ModChange>();
                selectedTab = InspectorTab.Overview;
                changelog.SelectedModule = value;
                lastChangeKey = "";
                Render();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.Surface;
                flow.BackColor = SoftTheme.Surface;
                if (mod != null)
                {
                    Render();
                }
                Invalidate(true);
            }

            public void SetChanges(IReadOnlyList<ModChange> pending)
            {
                var safeChanges = pending ?? Array.Empty<ModChange>();
                string key = string.Join("|", safeChanges.Select(ch =>
                    ch.Mod.identifier + ":" + ch.ChangeType));
                if (key == lastChangeKey)
                {
                    return;
                }
                lastChangeKey = key;
                changes = safeChanges;
                if (mod != null)
                {
                    Render();
                }
            }

            private void Render()
            {
                if (mod == null)
                {
                    return;
                }
                foreach (Control control in Controls.OfType<ModernButton>()
                                                       .Where(control => !ReferenceEquals(control,
                                                                                           closeButton))
                                                       .ToArray())
                {
                    control.Dispose();
                }
                foreach (Control control in flow.Controls.OfType<Control>().ToArray())
                {
                    flow.Controls.Remove(control);
                    if (!ReferenceEquals(control, changelog))
                    {
                        control.Dispose();
                    }
                }
                flow.Controls.Clear();

                var heading = MakeLabel(Properties.Resources.ModernShellInspector,
                                        SoftTheme.SectionFont, SoftTheme.TextPrimary);
                heading.Width = Math.Max(1, Width - Padding.Horizontal);
                heading.Height = 30;
                flow.Controls.Add(heading);

                cover?.Dispose();
                cover = ModArtGenerator.CreateFallbackCover(mod.Identifier, mod.Name,
                    Math.Max(160, SoftTheme.ScaleInt(360, SoftTheme.LayoutDpi)),
                    Math.Max(100, SoftTheme.ScaleInt(170, SoftTheme.LayoutDpi)));
                var picture = new PictureBox
                {
                    Image = cover,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = SoftTheme.SurfaceSunken,
                    Width = Math.Max(1, Width - Padding.Horizontal),
                    Height = SoftTheme.ScaleInt(170, SoftTheme.LayoutDpi),
                    Margin = new Padding(0, 0, 0, SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi)),
                };
                flow.Controls.Add(picture);

                var title = MakeLabel(mod.Name, SoftTheme.TitleFont, SoftTheme.TextPrimary);
                title.Width = picture.Width;
                title.Height = 26;
                flow.Controls.Add(title);
                var authors = MakeLabel(string.Join(", ", mod.Authors),
                                       SoftTheme.CardAuthorFont, SoftTheme.TextSecondary);
                authors.Width = picture.Width;
                authors.Height = 22;
                flow.Controls.Add(authors);

                var status = MakeLabel(StatusText(), SoftTheme.PillFont, StatusColor());
                status.Width = picture.Width;
                status.Height = 22;
                flow.Controls.Add(status);

                var action = new ModernButton
                {
                    Text = ActionText(),
                    Style = ModernButtonStyle.Primary,
                    Icon = ActionText() == Properties.Resources.ModernShellInQueue
                         ? ShellIcon.Check : ShellIcon.None,
                    Width = picture.Width,
                    Height = SoftTheme.ScaleInt(36, SoftTheme.LayoutDpi),
                    Enabled = !mod.IsAutodetected && mod.IsInstallable(),
                };
                action.Click += (sender, e) => toggleAction(mod);
                flow.Controls.Add(action);

                var tabs = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = SoftTheme.Surface,
                    Width = picture.Width,
                    Height = SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi),
                    Margin = new Padding(0, SoftTheme.ScaleInt(12, SoftTheme.LayoutDpi),
                                         0, SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi)),
                    Padding = new Padding(0),
                };
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorOverview,
                    InspectorTab.Overview, 82));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModInfoChangelogTab,
                    InspectorTab.Changelog, 96));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorRelationships,
                    InspectorTab.Relationships, 116));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorFiles,
                    InspectorTab.Files, 70));
                flow.Controls.Add(tabs);

                switch (selectedTab)
                {
                    case InspectorTab.Changelog:
                        changelog.Width = picture.Width;
                        changelog.Height = SoftTheme.ScaleInt(420, SoftTheme.LayoutDpi);
                        changelog.Margin = new Padding(0);
                        flow.Controls.Add(changelog);
                        break;
                    case InspectorTab.Relationships:
                        AddRelationshipContent(picture.Width);
                        break;
                    case InspectorTab.Files:
                        AddFilesContent(picture.Width);
                        break;
                    default:
                        AddOverviewContent(picture.Width);
                        break;
                }
            }

            private ModernButton MakeInspectorTab(string text, InspectorTab tab, int width)
            {
                var button = new ModernButton
                {
                    Text = text,
                    Style = selectedTab == tab ? ModernButtonStyle.Soft
                                                : ModernButtonStyle.Ghost,
                    Width = SoftTheme.ScaleInt(width, SoftTheme.LayoutDpi),
                    Height = SoftTheme.ScaleInt(30, SoftTheme.LayoutDpi),
                    Margin = new Padding(0, 0, SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi), 0),
                };
                button.Click += (sender, e) =>
                {
                    if (selectedTab != tab)
                    {
                        selectedTab = tab;
                        Render();
                    }
                };
                return button;
            }

            private void AddOverviewContent(int width)
            {
                if (mod == null)
                {
                    return;
                }

                var abstractLabel = MakeLabel(string.IsNullOrWhiteSpace(mod.Abstract)
                                              ? Properties.Resources.ModernShellNoDescription
                                              : mod.Abstract,
                                              SoftTheme.CardBodyFont, SoftTheme.TextSecondary);
                abstractLabel.Width = width;
                abstractLabel.Height = SoftTheme.ScaleInt(82, SoftTheme.LayoutDpi);
                abstractLabel.AutoEllipsis = true;
                abstractLabel.TextAlign = ContentAlignment.TopLeft;
                flow.Controls.Add(abstractLabel);

                var detailsHeading = MakeLabel(Properties.Resources.ModernShellInspectorDetails,
                                               SoftTheme.CardTitleFont,
                                               SoftTheme.TextPrimary);
                detailsHeading.Width = width;
                detailsHeading.Height = 24;
                detailsHeading.Margin = new Padding(0, SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi), 0, 0);
                flow.Controls.Add(detailsHeading);

                string released = mod.Module.release_date?.ToLocalTime().ToString("d")
                                  ?? Properties.Resources.GUIModUnknown;
                string onDisk = mod.IsInstalled ? mod.InstallSize
                                                 : Properties.Resources.GUIModUnknown;
                string homepage = mod.Module.resources?.homepage?.ToString()
                                   ?? Properties.Resources.GUIModUnknown;
                string detailText = string.Join(Environment.NewLine, new[]
                {
                    Properties.Resources.ModernShellInspectorIdentifier + ": " + mod.Identifier,
                    Properties.Resources.ModernShellInspectorVersion + ": " + mod.Version,
                    Properties.Resources.ModernShellInspectorReleased + ": " + released,
                    Properties.Resources.ModernShellInspectorDownloadSize + ": " + mod.DownloadSize,
                    Properties.Resources.ModernShellInspectorOnDisk + ": " + onDisk,
                    Properties.Resources.ModernShellInspectorHomepage + ": " + homepage,
                });
                var details = MakeLabel(detailText, SoftTheme.MetaFont, SoftTheme.TextSecondary);
                details.Width = width;
                details.Height = SoftTheme.ScaleInt(112, SoftTheme.LayoutDpi);
                details.AutoEllipsis = true;
                details.TextAlign = ContentAlignment.TopLeft;
                flow.Controls.Add(details);
            }

            private void AddRelationshipContent(int width)
            {
                if (mod == null)
                {
                    return;
                }
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorRequires,
                                     mod.Module.depends, width);
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorRecommends,
                                     mod.Module.recommends, width);
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorConflicts,
                                     mod.Module.conflicts, width);
            }

            private void AddRelationshipGroup(string title,
                                               IEnumerable<RelationshipDescriptor>? relationships,
                                               int width)
            {
                var values = (relationships ?? Enumerable.Empty<RelationshipDescriptor>())
                             .Select(rel => rel.ToString() ?? "")
                             .Where(value => value.Length > 0)
                             .Take(24)
                             .ToList();
                var heading = MakeLabel(string.Format("{0} ({1})", title, values.Count),
                                        SoftTheme.CardTitleFont, SoftTheme.TextPrimary);
                heading.Width = width;
                heading.Height = 24;
                heading.Margin = new Padding(0, SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi), 0, 0);
                flow.Controls.Add(heading);
                if (values.Count == 0)
                {
                    var empty = MakeLabel(Properties.Resources.ModernShellInspectorNoRelationships,
                                           SoftTheme.MetaFont, SoftTheme.TextMuted);
                    empty.Width = width;
                    empty.Height = 22;
                    flow.Controls.Add(empty);
                    return;
                }
                foreach (var value in values)
                {
                    var relationship = MakeLabel("• " + value,
                                                 SoftTheme.MetaFont,
                                                 SoftTheme.TextSecondary);
                    relationship.Width = width;
                    relationship.Height = 22;
                    relationship.AutoEllipsis = true;
                    flow.Controls.Add(relationship);
                }
            }

            private void AddFilesContent(int width)
            {
                var heading = MakeLabel(Properties.Resources.ModernShellInspectorContents,
                                        SoftTheme.CardTitleFont, SoftTheme.TextPrimary);
                heading.Width = width;
                heading.Height = 24;
                flow.Controls.Add(heading);
                string packageState = mod?.IsCached == true
                                    ? Properties.Resources.ModernShellInspectorPackageCached
                                    : Properties.Resources.ModernShellInspectorPackageNotCached;
                var contents = MakeLabel(packageState, SoftTheme.CardBodyFont,
                                          SoftTheme.TextSecondary);
                contents.Width = width;
                contents.Height = SoftTheme.ScaleInt(72, SoftTheme.LayoutDpi);
                contents.AutoEllipsis = true;
                contents.TextAlign = ContentAlignment.TopLeft;
                flow.Controls.Add(contents);
            }

            private string StatusText()
            {
                var pending = changes.FirstOrDefault(ch => ch.Mod.identifier == mod?.Identifier);
                if (pending != null)
                {
                    return Properties.Resources.ModernShellInQueue;
                }
                if (mod?.HasUpdate == true)
                {
                    return Properties.Resources.DiscoverFilterUpdates;
                }
                return mod?.IsInstalled == true
                     ? Properties.Resources.DiscoverFilterInstalled
                     : Properties.Resources.DiscoverFilterNew;
            }

            private Color StatusColor()
                => StatusText() == Properties.Resources.ModernShellInQueue
                   ? SoftTheme.AccentDeep
                   : mod?.IsInstalled == true ? SoftTheme.Success : SoftTheme.TextMuted;

            private string ActionText()
            {
                if (changes.Any(ch => ch.Mod.identifier == mod?.Identifier))
                {
                    return Properties.Resources.ModernShellInQueue;
                }
                if (mod?.HasUpdate == true)
                {
                    return Properties.Resources.ModernShellUpdate;
                }
                return mod?.IsInstalled == true
                     ? Properties.Resources.ModernShellRemove
                     : Properties.Resources.ModernShellInstall;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    e.Graphics.DrawLine(pen, 0, 0, 0, Height);
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                foreach (Control control in Controls.OfType<ModernButton>())
                {
                    control.Bounds = new Rectangle(Math.Max(0, Width - Padding.Right - control.Width),
                                                   8, control.Width, 30);
                }
                foreach (Control control in flow.Controls)
                {
                    control.Width = Math.Max(1, flow.ClientSize.Width - flow.Padding.Horizontal);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    cover?.Dispose();
                    changelog.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class ModernInspector : Panel
        {
            private enum InspectorTab
            {
                Overview,
                Changelog,
                Relationships,
                Files,
            }

            private readonly Action<GUIMod> toggleAction;
            private readonly Action closeAction;
            private readonly Changelog changelog;
            private readonly Func<IReadOnlyList<GUIMod>> modulesProvider;
            private readonly Func<GameInstance?> instanceProvider;
            private readonly Panel header;
            private readonly Panel coverHost;
            private readonly PictureBox coverPicture;
            private readonly InspectorBadge statusBadge;
            private readonly Label titleLabel;
            private readonly Label byLabel;
            private readonly FlowLayoutPanel actions;
            private readonly ModernButton actionButton;
            private readonly ModernButton pinButton;
            private readonly ModernButton closeButton;
            private readonly FlowLayoutPanel tabs;
            private readonly Panel bodyScroll;
            private readonly FlowLayoutPanel bodyFlow;
            private Image? cover;
            private GUIMod? mod;
            private IReadOnlyList<GUIMod> catalogue = Array.Empty<GUIMod>();
            private IReadOnlyList<ModChange> changes = Array.Empty<ModChange>();
            private string lastChangeKey = "";
            private string lastCatalogueKey = "";
            private InspectorTab selectedTab = InspectorTab.Overview;
            private bool pinned;

            public ModernInspector(Action<GUIMod> toggle, Action close,
                                   Changelog changelogControl,
                                   Func<IReadOnlyList<GUIMod>> getModules,
                                   Func<GameInstance?> getInstance)
            {
                toggleAction = toggle;
                closeAction = close;
                changelog = changelogControl;
                modulesProvider = getModules;
                instanceProvider = getInstance;
                BackColor = SoftTheme.Surface;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw, true);

                header = new Panel { BackColor = SoftTheme.Surface, Dock = DockStyle.Top };
                coverHost = new Panel { BackColor = SoftTheme.SurfaceSunken };
                coverPicture = new PictureBox
                {
                    BackColor = SoftTheme.SurfaceSunken,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Dock = DockStyle.Fill,
                };
                coverHost.Controls.Add(coverPicture);
                statusBadge = new InspectorBadge();
                coverHost.Controls.Add(statusBadge);
                closeButton = new ModernButton
                {
                    Style = ModernButtonStyle.Window,
                    Icon = ShellIcon.Close,
                    AccessibleName = Properties.Resources.ModernShellClose,
                };
                closeButton.Click += (sender, e) => closeAction();
                coverHost.Controls.Add(closeButton);

                titleLabel = MakeLabel("", SoftTheme.InspectorTitleFont, SoftTheme.TextPrimary);
                titleLabel.AutoEllipsis = true;
                byLabel = MakeLabel("", SoftTheme.InspectorByFont, SoftTheme.TextMuted);
                byLabel.AutoEllipsis = true;
                actions = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                };
                actionButton = new ModernButton
                {
                    Style = ModernButtonStyle.Primary,
                    Height = SoftTheme.ScaleInt(36, SoftTheme.LayoutDpi),
                    Margin = new Padding(0, 0, 8, 0),
                };
                actionButton.Click += (sender, e) =>
                {
                    if (mod != null)
                    {
                        toggleAction(mod);
                    }
                };
                pinButton = new ModernButton
                {
                    Text = "Pin",
                    Style = ModernButtonStyle.Soft,
                    Width = SoftTheme.ScaleInt(70, SoftTheme.LayoutDpi),
                    Height = SoftTheme.ScaleInt(36, SoftTheme.LayoutDpi),
                    Margin = Padding.Empty,
                };
                pinButton.Click += (sender, e) =>
                {
                    pinned = !pinned;
                    pinButton.Text = pinned ? "Pinned" : "Pin";
                    pinButton.Style = pinned ? ModernButtonStyle.Primary
                                              : ModernButtonStyle.Soft;
                    pinButton.Invalidate();
                };
                actions.Controls.Add(actionButton);
                actions.Controls.Add(pinButton);

                header.Controls.Add(actions);
                header.Controls.Add(byLabel);
                header.Controls.Add(titleLabel);
                header.Controls.Add(coverHost);

                tabs = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoScroll = false,
                    BackColor = SoftTheme.Surface,
                    Padding = new Padding(SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi), 0,
                                          SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi), 0),
                    Margin = new Padding(0),
                    Height = SoftTheme.ScaleInt(44, SoftTheme.LayoutDpi),
                    Dock = DockStyle.Top,
                };
                tabs.Paint += (sender, e) =>
                {
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        e.Graphics.DrawLine(pen, 0, tabs.Height - 1,
                                            tabs.Width, tabs.Height - 1);
                    }
                };
                tabs.Resize += (sender, e) => LayoutTabs();

                bodyScroll = new SoftScrollPanel
                {
                    BackColor = SoftTheme.Surface,
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    Padding = new Padding(SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi),
                                          SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi),
                                          SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi),
                                          SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi)),
                };
                bodyFlow = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = SoftTheme.Surface,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                };
                bodyScroll.Controls.Add(bodyFlow);
                bodyScroll.Resize += (sender, e) => LayoutBody();

                // Add in reverse visual order for WinForms docking.
                Controls.Add(bodyScroll);
                Controls.Add(tabs);
                Controls.Add(header);
                Resize += (sender, e) => LayoutInspector();
            }

            public void SetMod(GUIMod value, IReadOnlyList<ModChange> pending)
            {
                mod = value;
                changes = pending ?? Array.Empty<ModChange>();
                catalogue = modulesProvider() ?? Array.Empty<GUIMod>();
                selectedTab = InspectorTab.Overview;
                pinned = false;
                changelog.SelectedModule = value;
                lastChangeKey = "";
                Render();
            }

            public void SetModules(IReadOnlyList<GUIMod> modules)
            {
                var safe = modules ?? Array.Empty<GUIMod>();
                string key = string.Join("|", safe.Select(item =>
                    item.Identifier + ":" + item.Version + ":" + item.IsInstalled + ":" + item.HasUpdate));
                if (key == lastCatalogueKey)
                {
                    return;
                }
                lastCatalogueKey = key;
                catalogue = safe;
                if (mod != null)
                {
                    Render();
                }
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.Surface;
                header.BackColor = SoftTheme.Surface;
                coverHost.BackColor = SoftTheme.SurfaceSunken;
                coverPicture.BackColor = SoftTheme.SurfaceSunken;
                tabs.BackColor = SoftTheme.Surface;
                bodyScroll.BackColor = SoftTheme.Surface;
                bodyFlow.BackColor = SoftTheme.Surface;
                changelog.RefreshTheme();
                if (mod != null)
                {
                    Render();
                }
                Invalidate(true);
            }

            public void SetChanges(IReadOnlyList<ModChange> pending)
            {
                var safeChanges = pending ?? Array.Empty<ModChange>();
                string key = string.Join("|", safeChanges.Select(ch =>
                    ch.Mod.identifier + ":" + ch.ChangeType));
                if (key == lastChangeKey)
                {
                    return;
                }
                lastChangeKey = key;
                changes = safeChanges;
                if (mod != null)
                {
                    Render();
                }
            }

            private void Render()
            {
                if (mod == null)
                {
                    return;
                }

                coverPicture.Image = null;
                cover?.Dispose();
                cover = ModArtGenerator.CreateFallbackCover(mod.Identifier, mod.Name,
                    Math.Max(180, Width - SoftTheme.ScaleInt(40, SoftTheme.LayoutDpi)),
                    SoftTheme.ScaleInt(150, SoftTheme.LayoutDpi));
                coverPicture.Image = cover;

                titleLabel.Text = mod.Name;
                string author = string.Join(", ", mod.Authors);
                string category = ModDiscoverView.CategoryLabel(ModDiscoverView.CategoryFor(mod));
                byLabel.Text = string.Format("by {0} - {1}",
                                             string.IsNullOrWhiteSpace(author) ? "Unknown" : author,
                                             category);
                statusBadge.SetAppearance(StatusText(), StatusColor(), StatusFill());
                actionButton.Text = ActionText();
                actionButton.Icon = ActionText() == Properties.Resources.ModernShellInQueue
                                  ? ShellIcon.Check : ShellIcon.None;
                actionButton.Enabled = !mod.IsAutodetected && mod.IsInstallable();
                pinButton.Text = pinned ? "Pinned" : "Pin";
                pinButton.Style = pinned ? ModernButtonStyle.Primary
                                         : ModernButtonStyle.Soft;

                foreach (Control control in tabs.Controls.OfType<Control>().ToArray())
                {
                    control.Dispose();
                }
                tabs.Controls.Clear();
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorOverview,
                    InspectorTab.Overview));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModInfoChangelogTab,
                    InspectorTab.Changelog));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorRelationships,
                    InspectorTab.Relationships));
                tabs.Controls.Add(MakeInspectorTab(
                    Properties.Resources.ModernShellInspectorFiles,
                    InspectorTab.Files));

                foreach (Control control in bodyFlow.Controls.OfType<Control>().ToArray())
                {
                    bodyFlow.Controls.Remove(control);
                    if (!ReferenceEquals(control, changelog))
                    {
                        control.Dispose();
                    }
                }
                bodyFlow.Controls.Clear();
                int width = Math.Max(180, Width - SoftTheme.ScaleInt(40, SoftTheme.LayoutDpi));
                switch (selectedTab)
                {
                    case InspectorTab.Changelog:
                        changelog.Width = width;
                        changelog.Height = SoftTheme.ScaleInt(480, SoftTheme.LayoutDpi);
                        changelog.Margin = new Padding(0);
                        bodyFlow.Controls.Add(changelog);
                        break;
                    case InspectorTab.Relationships:
                        AddRelationshipContent(width);
                        break;
                    case InspectorTab.Files:
                        AddFilesContent(width);
                        break;
                    default:
                        AddOverviewContent(width);
                        break;
                }
                LayoutInspector();
            }

            private ModernButton MakeInspectorTab(string text, InspectorTab tab)
            {
                var button = new ModernButton
                {
                    Text = text,
                    Style = ModernButtonStyle.Tab,
                    Active = selectedTab == tab,
                    Height = SoftTheme.ScaleInt(44, SoftTheme.LayoutDpi),
                    Margin = new Padding(0),
                    TabStop = true,
                };
                button.Click += (sender, e) =>
                {
                    if (selectedTab != tab)
                    {
                        selectedTab = tab;
                        Render();
                    }
                };
                return button;
            }

            private void AddOverviewContent(int width)
            {
                if (mod == null)
                {
                    return;
                }
                string description = string.IsNullOrWhiteSpace(mod.Abstract)
                                   ? Properties.Resources.ModernShellNoDescription
                                   : mod.Abstract;
                AddBody(MakeParagraph(description, width, SoftTheme.CardBodyFont,
                                      SoftTheme.TextSecondary, 96));

                var tags = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Width = width,
                    Margin = new Padding(0, SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi), 0, 0),
                    Padding = new Padding(0),
                };
                AddChip(tags, ModDiscoverView.CategoryLabel(ModDiscoverView.CategoryFor(mod)));
                AddChip(tags, mod.Version);
                if (!string.IsNullOrWhiteSpace(mod.DownloadSize))
                {
                    AddChip(tags, mod.DownloadSize);
                }
                if (mod.DownloadCount is int downloads && downloads > 0)
                {
                    AddChip(tags, downloads.ToString("N0") + " downloads");
                }
                GameInstance? instance = instanceProvider();
                string compatibility = mod.IsIncompatible
                                     ? "Needs " + (mod.GameCompatibility ?? Properties.Resources.GUIModUnknown)
                                     : "KSP " + (instance?.Version()?.ToString() ?? Properties.Resources.GUIModUnknown)
                                       + " compatible";
                AddChip(tags, compatibility, mod.IsIncompatible);
                foreach (string tag in (mod.Module.Tags ?? new HashSet<string>())
                                       .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                                       .Take(8))
                {
                    AddChip(tags, tag);
                }
                AddBody(tags);
                AddBody(MakeSection(Properties.Resources.ModernShellInspectorDetails, width));

                InstalledModule? installed = mod.InstalledMod;
                string released = mod.Module.release_date?.ToLocalTime().ToString("d")
                                  ?? Properties.Resources.GUIModUnknown;
                string onDisk = installed != null
                              ? SafeInstallSize(installed, instance)
                              : Properties.Resources.GUIModUnknown;
                string licence = mod.Module.license is { Count: > 0 }
                               ? string.Join(", ", mod.Module.license.Select(value => value.ToString()))
                               : Properties.Resources.GUIModUnknown;
                string homepage = mod.Module.resources?.homepage?.ToString()
                                ?? Properties.Resources.GUIModUnknown;
                AddKeyValue(Properties.Resources.ModernShellInspectorIdentifier, mod.Identifier, width);
                AddKeyValue(Properties.Resources.ModernShellInspectorVersion,
                            mod.HasUpdate && mod.InstalledVersion != null
                                ? mod.Version + " (installed " + mod.InstalledVersion + ")"
                                : mod.Version, width);
                AddKeyValue(Properties.Resources.ModernShellInspectorReleased, released, width);
                AddKeyValue(Properties.Resources.ModernShellInspectorDownloadSize,
                            string.IsNullOrWhiteSpace(mod.DownloadSize)
                                ? Properties.Resources.GUIModUnknown : mod.DownloadSize, width);
                AddKeyValue(Properties.Resources.ModernShellInspectorOnDisk, onDisk, width);
                AddKeyValue("Licence", licence, width);
                AddKeyValue(Properties.Resources.ModernShellInspectorHomepage, homepage, width);
            }

            private void AddRelationshipContent(int width)
            {
                if (mod == null)
                {
                    return;
                }
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorRequires,
                                     mod.Module.depends, width);
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorRecommends,
                                     mod.Module.recommends, width);
                AddRelationshipGroup(Properties.Resources.ModernShellInspectorConflicts,
                                     mod.Module.conflicts, width);
                AddBody(MakeSection("Required by", width));
                var requiredBy = catalogue.Where(candidate => !ReferenceEquals(candidate, mod)
                    && (candidate.Module.depends ?? Enumerable.Empty<RelationshipDescriptor>())
                       .Any(relationship => relationship.ContainsAny(mod.Module.ProvidesList)))
                    .OrderBy(candidate => candidate.Name,
                             StringComparer.CurrentCultureIgnoreCase)
                    .Take(16)
                    .Select(candidate => candidate.Name)
                    .ToList();
                AddBody(MakeParagraph(requiredBy.Count == 0
                                        ? "Nothing in the current setup requires this mod."
                                        : string.Join(", ", requiredBy),
                                      width, SoftTheme.MetaFont, SoftTheme.TextSecondary, 54));
            }

            private void AddRelationshipGroup(string title,
                                               IEnumerable<RelationshipDescriptor>? relationships,
                                               int width)
            {
                var items = (relationships ?? Enumerable.Empty<RelationshipDescriptor>())
                    .Select(ToRelationshipItem)
                    .Where(item => item != null)
                    .Cast<RelationshipItem>()
                    .Take(24)
                    .ToList();
                AddBody(MakeSection(string.Format("{0} ({1})", title, items.Count), width));
                if (items.Count == 0)
                {
                    AddBody(MakeParagraph(Properties.Resources.ModernShellInspectorNoRelationships,
                                          width, SoftTheme.MetaFont, SoftTheme.TextMuted, 30));
                    return;
                }
                foreach (var item in items)
                {
                    GUIMod? related = item.Mod;
                    string state;
                    Color stateColor;
                    if (related == null)
                    {
                        state = "external";
                        stateColor = SoftTheme.Warning;
                    }
                    else if (changes.Any(change => change.Mod.identifier == related.Identifier))
                    {
                        state = Properties.Resources.ModernShellInQueue;
                        stateColor = SoftTheme.Accent;
                    }
                    else if (related.IsInstalled)
                    {
                        state = "installed";
                        stateColor = SoftTheme.Success;
                    }
                    else
                    {
                        state = "not installed";
                        stateColor = SoftTheme.TextMuted;
                    }
                    ModernButton? install = related != null && !related.IsInstalled
                                               && related.IsInstallable()
                        ? new ModernButton
                        {
                            Text = Properties.Resources.ModernShellInstall,
                            Style = ModernButtonStyle.Soft,
                            Width = SoftTheme.ScaleInt(70, SoftTheme.LayoutDpi),
                            Height = SoftTheme.ScaleInt(28, SoftTheme.LayoutDpi),
                        }
                        : null;
                    if (install != null && related != null)
                    {
                        GUIMod relatedMod = related;
                        install.Click += (sender, e) => toggleAction(relatedMod);
                    }
                    AddBody(new InspectorRelationRow(item.Label, state, stateColor, install,
                                                     width));
                }
            }

            private RelationshipItem? ToRelationshipItem(RelationshipDescriptor relationship)
            {
                string label = relationship.ToString() ?? "";
                if (label.Length == 0)
                {
                    return null;
                }
                GUIMod? related = relationship is ModuleRelationshipDescriptor moduleRelationship
                    ? catalogue.FirstOrDefault(candidate => candidate.Identifier
                                                          == moduleRelationship.name)
                    : null;
                return new RelationshipItem(label, related);
            }

            private void AddFilesContent(int width)
            {
                if (mod == null)
                {
                    return;
                }
                AddBody(MakeSection(Properties.Resources.ModernShellInspectorContents, width));
                InstalledModule? installed = mod.InstalledMod;
                GameInstance? instance = instanceProvider();
                if (installed == null)
                {
                    AddBody(MakeParagraph(mod.IsCached
                                            ? Properties.Resources.ModernShellInspectorPackageCached
                                            : Properties.Resources.ModernShellInspectorPackageNotCached,
                                          width, SoftTheme.CardBodyFont,
                                          SoftTheme.TextSecondary, 64));
                    AddBody(MakeSection("Totals", width));
                    AddKeyValue("Download", mod.DownloadSize, width);
                    AddKeyValue("Installed", Properties.Resources.GUIModUnknown, width);
                    AddKeyValue("Files", Properties.Resources.GUIModUnknown, width);
                    return;
                }

                var files = installed.Files.OrderBy(path => path,
                                                     StringComparer.CurrentCultureIgnoreCase)
                                           .ToList();
                if (files.Count == 0)
                {
                    AddBody(MakeParagraph("CKAN has no file entries for this installed module.",
                                          width, SoftTheme.CardBodyFont,
                                          SoftTheme.TextSecondary, 54));
                }
                else
                {
                    foreach (string path in files.Take(180))
                    {
                        string size = "";
                        if (instance != null)
                        {
                            try
                            {
                                var info = new System.IO.FileInfo(instance.ToAbsoluteGameDir(path));
                                if (info.Exists)
                                {
                                    size = CkanModule.FmtSize(info.Length);
                                }
                            }
                            catch
                            {
                                // A missing file is still useful information in the tree.
                            }
                        }
                        AddBody(new InspectorFileRow(path, size, width));
                    }
                    if (files.Count > 180)
                    {
                        AddBody(MakeParagraph(string.Format("{0} more files...",
                                                            files.Count - 180),
                                              width, SoftTheme.MetaFont,
                                              SoftTheme.TextMuted, 28));
                    }
                }
                AddBody(MakeSection("Totals", width));
                AddKeyValue("Download", mod.DownloadSize, width);
                AddKeyValue("Installed", SafeInstallSize(installed, instance), width);
                AddKeyValue("Files", files.Count.ToString("N0"), width);
            }

            private static string SafeInstallSize(InstalledModule installed, GameInstance? instance)
            {
                if (instance == null)
                {
                    return Properties.Resources.GUIModUnknown;
                }
                try
                {
                    return CkanModule.FmtSize(installed.ActualInstallSize(instance));
                }
                catch
                {
                    return installed.Module.install_size > 0
                         ? CkanModule.FmtSize(installed.Module.install_size)
                         : Properties.Resources.GUIModUnknown;
                }
            }

            private void AddBody(Control control)
            {
                bodyFlow.Controls.Add(control);
            }

            private static void AddChip(FlowLayoutPanel host, string text, bool warning = false)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    host.Controls.Add(new InspectorChip(text, warning));
                }
            }

            private static Label MakeSection(string text, int width)
            {
                var label = MakeLabel(text.ToUpperInvariant(), SoftTheme.MetaFont,
                                      SoftTheme.TextMuted);
                label.Width = width;
                label.Height = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                label.Margin = new Padding(0, SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi), 0,
                                            SoftTheme.ScaleInt(2, SoftTheme.LayoutDpi));
                return label;
            }

            private static Label MakeParagraph(string text, int width, Font font,
                                               Color color, int maximumHeight)
            {
                var label = MakeLabel(text, font, color);
                label.Width = width;
                label.MaximumSize = new Size(width, maximumHeight);
                label.Height = Math.Min(maximumHeight,
                    Math.Max(26, TextRenderer.MeasureText(text, font,
                        new Size(Math.Max(1, width), maximumHeight * 2),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4));
                label.TextAlign = ContentAlignment.TopLeft;
                label.AutoEllipsis = true;
                label.Margin = new Padding(0, 0, 0, SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi));
                return label;
            }

            private void AddKeyValue(string key, string value, int width)
            {
                var row = new Panel
                {
                    Width = width,
                    Height = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi),
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                };
                var keyLabel = MakeLabel(key, SoftTheme.MetaFont, SoftTheme.TextMuted);
                var valueLabel = MakeLabel(value ?? Properties.Resources.GUIModUnknown,
                                           SoftTheme.MetaFont, SoftTheme.TextPrimary);
                valueLabel.AutoEllipsis = true;
                row.Controls.Add(valueLabel);
                row.Controls.Add(keyLabel);
                row.Layout += (sender, e) =>
                {
                    int left = SoftTheme.ScaleInt(100, SoftTheme.LayoutDpi);
                    keyLabel.Bounds = new Rectangle(0, 0, left, row.Height);
                    valueLabel.Bounds = new Rectangle(left + SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi),
                                                      0,
                                                      Math.Max(1, row.Width - left
                                                               - SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi)),
                                                      row.Height);
                };
                row.PerformLayout();
                bodyFlow.Controls.Add(row);
            }

            private string StatusText()
            {
                var pending = changes.FirstOrDefault(ch => ch.Mod.identifier == mod?.Identifier);
                if (pending != null)
                {
                    return Properties.Resources.ModernShellInQueue;
                }
                if (mod?.HasUpdate == true)
                {
                    return Properties.Resources.DiscoverFilterUpdates;
                }
                return mod?.IsInstalled == true
                     ? Properties.Resources.DiscoverFilterInstalled
                     : Properties.Resources.DiscoverFilterNew;
            }

            private Color StatusColor()
                => StatusText() == Properties.Resources.ModernShellInQueue
                   ? SoftTheme.Accent
                   : mod?.IsInstalled == true ? SoftTheme.Success : SoftTheme.TextMuted;

            private Color StatusFill()
                => StatusText() == Properties.Resources.ModernShellInQueue
                   ? SoftTheme.AccentSoft
                   : mod?.IsInstalled == true ? SoftTheme.SuccessSoft : SoftTheme.SurfaceSunken;

            private string ActionText()
            {
                if (changes.Any(ch => ch.Mod.identifier == mod?.Identifier))
                {
                    return Properties.Resources.ModernShellInQueue;
                }
                if (mod?.HasUpdate == true)
                {
                    return Properties.Resources.ModernShellUpdate;
                }
                return mod?.IsInstalled == true
                     ? Properties.Resources.ModernShellRemove
                     : Properties.Resources.ModernShellInstall;
            }

            private void LayoutInspector()
            {
                int pad = SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi);
                int coverHeight = SoftTheme.ScaleInt(150, SoftTheme.LayoutDpi);
                coverHost.Bounds = new Rectangle(pad, pad,
                                                  Math.Max(1, Width - pad * 2), coverHeight);
                closeButton.Bounds = new Rectangle(Math.Max(0, coverHost.Width - SoftTheme.Px(42)),
                    SoftTheme.Px(8), SoftTheme.Px(34), SoftTheme.Px(30));
                closeButton.BringToFront();
                statusBadge.BringToFront();
                statusBadge.Location = new Point(10,
                                                  Math.Max(4, coverHost.Height - statusBadge.Height - 10));
                titleLabel.Bounds = new Rectangle(pad, coverHost.Bottom + SoftTheme.Px(12),
                                                  Math.Max(1, Width - pad * 2), SoftTheme.Px(28));
                byLabel.Bounds = new Rectangle(pad, titleLabel.Bottom + SoftTheme.Px(2),
                                               Math.Max(1, Width - pad * 2), SoftTheme.Px(20));
                actions.Bounds = new Rectangle(pad, byLabel.Bottom + SoftTheme.Px(8),
                                               Math.Max(1, Width - pad * 2),
                                               SoftTheme.ScaleInt(36, SoftTheme.LayoutDpi));
                actionButton.Width = Math.Max(80, actions.ClientSize.Width
                                                  - pinButton.Width - SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi));
                header.Height = actions.Bottom + SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi);
                LayoutTabs();
                LayoutBody();
            }

            private void LayoutTabs()
            {
                int gap = SoftTheme.ScaleInt(2, SoftTheme.LayoutDpi);
                int usable = Math.Max(1, tabs.ClientSize.Width - tabs.Padding.Horizontal
                                         - gap * 3);
                int[] widths = { 70, 88, 112, 58 };
                int wanted = widths.Sum(value => SoftTheme.ScaleInt(value, SoftTheme.LayoutDpi));
                if (wanted > usable)
                {
                    int each = Math.Max(46, usable / 4);
                    foreach (Control control in tabs.Controls)
                    {
                        control.Width = each;
                        control.Height = tabs.ClientSize.Height;
                    }
                }
                else
                {
                    for (int i = 0; i < tabs.Controls.Count; ++i)
                    {
                        tabs.Controls[i].Width = SoftTheme.ScaleInt(widths[Math.Min(i, 3)], SoftTheme.LayoutDpi);
                        tabs.Controls[i].Height = tabs.ClientSize.Height;
                    }
                }
            }

            private void LayoutBody()
            {
                int width = Math.Max(1, bodyScroll.ClientSize.Width
                                        - bodyScroll.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                bodyFlow.Location = new Point(bodyScroll.Padding.Left, bodyScroll.Padding.Top);
                bodyFlow.Width = width;
                foreach (Control control in bodyFlow.Controls)
                {
                    if (!(control is InspectorChip))
                    {
                        control.MaximumSize = new Size(width, 0);
                        control.Width = width;
                    }
                }
                bodyFlow.PerformLayout();
                bodyScroll.AutoScrollMinSize = new Size(
                    0,
                    Math.Max(bodyScroll.ClientSize.Height,
                             bodyFlow.PreferredSize.Height + bodyScroll.Padding.Vertical));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    e.Graphics.DrawLine(pen, 0, 0, 0, Height);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    coverPicture.Image = null;
                    cover?.Dispose();
                    changelog.Dispose();
                }
                base.Dispose(disposing);
            }

            private sealed class RelationshipItem
            {
                public RelationshipItem(string relationshipLabel, GUIMod? relatedMod)
                {
                    Label = relationshipLabel;
                    Mod = relatedMod;
                }

                public string Label { get; }
                public GUIMod? Mod { get; }
            }

            private sealed class InspectorBadge : Control
            {
                private Color fill = SoftTheme.SurfaceSunken;
                private Color textColor = SoftTheme.TextMuted;

                public InspectorBadge()
                {
                    Height = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                    Font = SoftTheme.PillFont;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = Color.Transparent;
                }

                public void SetAppearance(string text, Color foreground, Color background)
                {
                    Text = text;
                    textColor = foreground;
                    fill = background;
                    Width = Math.Max(58, TextRenderer.MeasureText(text ?? "", Font).Width
                                         + SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi));
                    Invalidate();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Height - 1);
                    SoftTheme.FillRounded(e.Graphics, rect, fill, SoftTheme.RadiusPill);
                    TextRenderer.DrawText(e.Graphics, Text ?? "", Font, rect, textColor,
                                          TextFormatFlags.HorizontalCenter
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                }
            }

            private sealed class InspectorChip : Control
            {
                private readonly bool warning;

                public InspectorChip(string text, bool isWarning)
                {
                    Text = text;
                    warning = isWarning;
                    Font = SoftTheme.MetaFont;
                    Height = SoftTheme.ScaleInt(26, SoftTheme.LayoutDpi);
                    Width = Math.Max(42, TextRenderer.MeasureText(text, Font).Width
                                         + SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi));
                    Margin = new Padding(0, 0, SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi),
                                          SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi));
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Height - 1);
                    SoftTheme.FillRounded(e.Graphics, rect,
                        warning ? SoftTheme.WarningSoft : SoftTheme.SurfaceSunken,
                        SoftTheme.RadiusPill);
                    SoftTheme.DrawRounded(e.Graphics, rect,
                        warning ? SoftTheme.Warning : SoftTheme.Border,
                        SoftTheme.RadiusPill, 1f);
                    TextRenderer.DrawText(e.Graphics, Text ?? "", Font, rect,
                        warning ? SoftTheme.Warning : SoftTheme.TextSecondary,
                        TextFormatFlags.HorizontalCenter
                        | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.NoPadding);
                }
            }

            private sealed class InspectorRelationRow : Panel
            {
                private readonly Label name;
                private readonly Label state;
                private readonly ModernButton? action;

                public InspectorRelationRow(string relationshipLabel, string status,
                                            Color statusColor, ModernButton? install,
                                            int width)
                {
                    name = MakeLabel(relationshipLabel, SoftTheme.MetaFont,
                                     SoftTheme.TextPrimary);
                    name.AutoEllipsis = true;
                    state = MakeLabel(status, SoftTheme.MetaFont, statusColor);
                    state.TextAlign = ContentAlignment.MiddleRight;
                    action = install;
                    Height = SoftTheme.ScaleInt(42, SoftTheme.LayoutDpi);
                    Width = width;
                    BackColor = Color.Transparent;
                    Controls.Add(name);
                    Controls.Add(state);
                    if (action != null)
                    {
                        Controls.Add(action);
                    }
                    Resize += (sender, e) => LayoutRow();
                    LayoutRow();
                }

                private void LayoutRow()
                {
                    int right = Width;
                    if (action != null)
                    {
                        action.Bounds = new Rectangle(Math.Max(0, right - action.Width),
                                                      7, action.Width, action.Height);
                        right = action.Left - SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi);
                    }
                    state.Bounds = new Rectangle(Math.Max(80, right - SoftTheme.ScaleInt(82, SoftTheme.LayoutDpi)),
                                                 0, SoftTheme.ScaleInt(82, SoftTheme.LayoutDpi), Height);
                    name.Bounds = new Rectangle(0, 0,
                                                Math.Max(40, state.Left - SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi)),
                                                Height);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        e.Graphics.DrawLine(pen, 0, Height - 1,
                                            Math.Max(0, Width - 1), Height - 1);
                    }
                }
            }

            private sealed class InspectorFileRow : Panel
            {
                private readonly Label path;
                private readonly Label size;

                public InspectorFileRow(string filePath, string fileSize, int width)
                {
                    path = MakeLabel(filePath, SoftTheme.MetaFont, SoftTheme.TextSecondary);
                    path.AutoEllipsis = true;
                    size = MakeLabel(fileSize, SoftTheme.MetaFont, SoftTheme.TextMuted);
                    size.TextAlign = ContentAlignment.MiddleRight;
                    Width = width;
                    Height = SoftTheme.ScaleInt(28, SoftTheme.LayoutDpi);
                    BackColor = Color.Transparent;
                    Controls.Add(path);
                    Controls.Add(size);
                    Layout += (sender, e) =>
                    {
                        int sizeWidth = SoftTheme.ScaleInt(62, SoftTheme.LayoutDpi);
                        size.Bounds = new Rectangle(Math.Max(0, Width - sizeWidth), 0,
                                                    sizeWidth, Height);
                        path.Bounds = new Rectangle(0, 0,
                                                    Math.Max(30, size.Left - SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi)),
                                                    Height);
                    };
                    PerformLayout();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        e.Graphics.DrawLine(pen, 0, Height - 1,
                                            Math.Max(0, Width - 1), Height - 1);
                    }
                }
            }
        }

        private sealed class ModernQueueView : Panel
        {
            private readonly Action applyAction;
            private readonly Action clearAction;
            private readonly Label title;
            private readonly Label subtitle;
            private readonly FlowLayoutPanel list;
            private readonly ModernButton apply;
            private readonly ModernButton clear;
            private string changeKey = "\0";

            public ModernQueueView(Action applyChanges, Action clearChanges)
            {
                applyAction = applyChanges;
                clearAction = clearChanges;
                BackColor = SoftTheme.Backdrop;
                title = MakeLabel(Properties.Resources.ModernShellQueueTitle,
                                  SoftTheme.DisplayFont, SoftTheme.TextPrimary);
                subtitle = MakeLabel(Properties.Resources.ModernShellQueueSubtitle,
                                     SoftTheme.SubtitleFont, SoftTheme.TextSecondary);
                list = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = SoftTheme.Backdrop,
                    Padding = new Padding(0, 0, SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi),
                                           SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi)),
                    Margin = new Padding(0),
                };
                apply = new ModernButton
                {
                    Text = Properties.Resources.ModernShellApplyChanges,
                    Style = ModernButtonStyle.Primary,
                    Icon = ShellIcon.Check,
                    Visible = false,
                };
                clear = new ModernButton
                {
                    Text = Properties.Resources.ModernShellDiscard,
                    Style = ModernButtonStyle.Ghost,
                    Visible = false,
                };
                apply.Click += (sender, e) => applyAction();
                clear.Click += (sender, e) => clearAction();
                Controls.Add(list);
                Controls.Add(title);
                Controls.Add(subtitle);
                Controls.Add(apply);
                Controls.Add(clear);
                Resize += (sender, e) => LayoutQueue();
            }

            public void SetChanges(IReadOnlyList<ModChange> changes)
            {
                var safeChanges = changes ?? Array.Empty<ModChange>();
                string nextKey = string.Join("|", safeChanges.Select(change =>
                    change.Mod.identifier + ":" + change.ChangeType));
                if (nextKey == changeKey)
                {
                    return;
                }
                changeKey = nextKey;
                foreach (Control control in list.Controls.OfType<Control>().ToArray())
                {
                    control.Dispose();
                }
                list.Controls.Clear();
                bool any = safeChanges.Count != 0;
                apply.Visible = any;
                clear.Visible = any;
                list.Controls.Add(MakeQueueHeading(Properties.Resources.ModernShellQueueStaged));
                if (!any)
                {
                    var empty = MakeLabel(Properties.Resources.ModernShellNothingStaged,
                                          SoftTheme.CardBodyFont, SoftTheme.TextSecondary);
                    empty.Width = Math.Max(1, list.ClientSize.Width - 4);
                    empty.Height = 42;
                    empty.Margin = new Padding(0, 0, 0, SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi));
                    list.Controls.Add(empty);
                }
                else
                {
                    apply.Text = string.Format(Properties.Resources.ModernShellApplyChanges,
                                               safeChanges.Count);
                    foreach (var change in safeChanges)
                    {
                        list.Controls.Add(new QueueRow(change));
                    }
                }
                list.Controls.Add(MakeQueueHeading(Properties.Resources.ModernShellQueueHistory));
                var historyEmpty = MakeLabel(Properties.Resources.ModernShellQueueNothingYet,
                                              SoftTheme.CardBodyFont, SoftTheme.TextSecondary);
                historyEmpty.Width = Math.Max(1, list.ClientSize.Width - 4);
                historyEmpty.Height = 42;
                list.Controls.Add(historyEmpty);
                LayoutQueue();
            }

            private static Label MakeQueueHeading(string text)
            {
                var heading = MakeLabel(text, SoftTheme.SectionFont, SoftTheme.TextPrimary);
                heading.Height = 30;
                heading.Tag = "heading";
                heading.Margin = new Padding(0, SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi), 0,
                                              SoftTheme.ScaleInt(4, SoftTheme.LayoutDpi));
                return heading;
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.Backdrop;
                list.BackColor = SoftTheme.Backdrop;
                title.ForeColor = SoftTheme.TextPrimary;
                subtitle.ForeColor = SoftTheme.TextSecondary;
                foreach (var label in list.Controls.OfType<Label>())
                {
                    label.ForeColor = Equals(label.Tag, "heading")
                                    ? SoftTheme.TextPrimary
                                    : SoftTheme.TextSecondary;
                }
                Invalidate(true);
            }

            private void LayoutQueue()
            {
                int pad = SoftTheme.ScaleInt(28, SoftTheme.LayoutDpi);
                title.Bounds = new Rectangle(pad, SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi),
                                             Math.Max(1, Width - pad * 2 - 280), Math.Max(38, title.Font.Height + 8));
                subtitle.Bounds = new Rectangle(pad, title.Bottom + 2,
                                                Math.Max(1, Width - pad * 2 - 280), 24);
                int buttonHeight = SoftTheme.Px(36);
                int clearWidth = Math.Max(SoftTheme.Px(92), TextRenderer.MeasureText(clear.Text, clear.Font).Width + SoftTheme.Px(24));
                int applyWidth = Math.Max(SoftTheme.Px(158), TextRenderer.MeasureText(apply.Text, apply.Font).Width + SoftTheme.Px(48));
                clear.Bounds = new Rectangle(Math.Max(pad, Width - pad - clearWidth),
                                             title.Top, clearWidth, buttonHeight);
                apply.Bounds = new Rectangle(Math.Max(pad, clear.Left - SoftTheme.Px(8) - applyWidth),
                                             title.Top, applyWidth, buttonHeight);
                title.Width = Math.Max(1, (apply.Visible ? apply.Left - SoftTheme.Px(16) : Width - pad) - pad);
                subtitle.Width = title.Width;
                apply.BringToFront();
                clear.BringToFront();
                list.Bounds = new Rectangle(pad, subtitle.Bottom + 24,
                                            Math.Max(1, Width - pad * 2),
                                            Math.Max(1, Height - subtitle.Bottom - 28));
                foreach (Control control in list.Controls)
                {
                    control.Width = Math.Max(1, list.ClientSize.Width - list.Padding.Horizontal);
                }
            }

            private sealed class QueueRow : Control
            {
                private readonly ModChange change;

                public QueueRow(ModChange pending)
                {
                    change = pending;
                    Height = SoftTheme.ScaleInt(66, SoftTheme.LayoutDpi);
                    Margin = new Padding(0, 0, 0, SoftTheme.ScaleInt(6, SoftTheme.LayoutDpi));
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Height - 1);
                    SoftTheme.FillRounded(g, rect, SoftTheme.Surface, SoftTheme.RadiusControl);
                    SoftTheme.DrawRounded(g, rect, SoftTheme.Border, SoftTheme.RadiusControl, 1f);
                    var mod = change.Mod;
                    var tile = new Rectangle(14, 14, 38, 38);
                    SoftTheme.FillRounded(g, tile, ModArtGenerator.SeedColor(mod.identifier), 10);
                    TextRenderer.DrawText(g, ModArtGenerator.InitialsForName(mod.name),
                                          SoftTheme.PillFont, tile, Color.White,
                                          TextFormatFlags.HorizontalCenter
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, mod.name, SoftTheme.CardTitleFont,
                                          new Rectangle(66, 10, Math.Max(80, Width - 240), 24),
                                          SoftTheme.TextPrimary,
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    string verb = change.ChangeType == GUIModChangeType.Remove
                                ? Properties.Resources.ModernShellRemove
                                : change.ChangeType == GUIModChangeType.Update
                                    || change.ChangeType == GUIModChangeType.Replace
                                  ? Properties.Resources.ModernShellUpdate
                                  : Properties.Resources.ModernShellInstall;
                    TextRenderer.DrawText(g, verb + "  ·  " + mod.version,
                                          SoftTheme.MetaFont,
                                          new Rectangle(66, 34, Math.Max(80, Width - 240), 20),
                                          SoftTheme.TextSecondary,
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Properties.Resources.ModernShellInQueue,
                                          SoftTheme.PillFont,
                                          new Rectangle(Math.Max(80, Width - 142), 0, 126, Height),
                                          SoftTheme.AccentDeep,
                                          TextFormatFlags.Right
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                }
            }
        }

        private enum SimplePageMode
        {
            Empty,
            Collections,
            Settings,
        }

        private sealed class ModernSimpleView : Panel
        {
            private readonly Action<IReadOnlyList<GUIMod>> queueCollection;
            private readonly Func<int, bool> readSetting;
            private readonly Action<int, bool> writeSetting;
            private readonly Action clearDownloadCache;
            private readonly Action clearArtworkCache;
            private readonly Action rebuildRegistry;
            private readonly Label title;
            private readonly Label subtitle;
            private readonly Panel body;
            private SimplePageMode mode;
            private string message = "";
            private string? primaryText;
            private Action? primaryAction;
            private string? secondaryText;
            private Action? secondaryAction;
            private IReadOnlyList<GUIMod> modules = Array.Empty<GUIMod>();
            private GameInstance? instance;
            private string dataKey = "";
            // Live configuration-backed values, reloaded whenever the page is
            // rebuilt for a theme switch or a registry refresh.
            private readonly bool[] settingToggles =
            {
                false, true, false,
            };

            public ModernSimpleView(Action<IReadOnlyList<GUIMod>> setupCollection,
                                    Func<int, bool> readModernSetting,
                                    Action<int, bool> writeModernSetting,
                                    Action clearDownloads,
                                    Action clearArtwork,
                                    Action rebuild)
            {
                queueCollection = setupCollection;
                readSetting = readModernSetting;
                writeSetting = writeModernSetting;
                clearDownloadCache = clearDownloads;
                clearArtworkCache = clearArtwork;
                rebuildRegistry = rebuild;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw, true);
                BackColor = SoftTheme.Backdrop;
                title = MakeLabel("", SoftTheme.DisplayFont, SoftTheme.TextPrimary);
                subtitle = MakeLabel("", SoftTheme.SubtitleFont, SoftTheme.TextSecondary);
                body = new Panel
                {
                    BackColor = SoftTheme.Backdrop,
                };
                Controls.Add(body);
                Controls.Add(title);
                Controls.Add(subtitle);
                Resize += (sender, e) => LayoutSimple();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.Backdrop;
                body.BackColor = SoftTheme.Backdrop;
                title.ForeColor = SoftTheme.TextPrimary;
                subtitle.ForeColor = SoftTheme.TextSecondary;
                RebuildBody();
                Invalidate(true);
            }

            public void SetContent(string heading, string copy, string messageText,
                                   string? actionText, Action? action)
            {
                title.Text = heading;
                subtitle.Text = copy;
                message = messageText;
                primaryText = actionText;
                primaryAction = action;
                secondaryText = null;
                secondaryAction = null;
                mode = heading == Properties.Resources.ModernShellCollectionsTitle
                     ? SimplePageMode.Collections
                     : heading == Properties.Resources.ModernShellSettingsTitle
                       ? SimplePageMode.Settings
                       : SimplePageMode.Empty;
                RebuildBody();
            }

            public void SetSecondary(string text, Action action)
            {
                secondaryText = text;
                secondaryAction = action;
                RebuildBody();
            }

            public void SetData(IReadOnlyList<GUIMod> currentModules,
                                GameInstance? currentInstance)
            {
                int installed = currentModules.Count(mod => mod.IsInstalled);
                int updates = currentModules.Count(mod => mod.HasUpdate);
                long bytes = currentModules.Where(mod => mod.IsInstalled)
                                           .Sum(mod => mod.Module.install_size);
                string key = string.Format("{0}:{1}:{2}:{3}:{4}",
                                           currentModules.Count, installed, updates,
                                           bytes, currentInstance?.Name ?? "");
                modules = currentModules;
                instance = currentInstance;
                if (key == dataKey)
                {
                    return;
                }
                dataKey = key;
                if (mode != SimplePageMode.Empty)
                {
                    RebuildBody();
                }
            }

            private void RebuildBody()
            {
                foreach (Control control in body.Controls.OfType<Control>().ToArray())
                {
                    control.Dispose();
                }
                body.Controls.Clear();

                if (mode == SimplePageMode.Collections)
                {
                    BuildCollections();
                }
                else if (mode == SimplePageMode.Settings)
                {
                    BuildSettings();
                }
                else
                {
                    BuildEmpty();
                }
                LayoutSimple();
            }

            private void BuildEmpty()
            {
                var content = new Label
                {
                    Text = message,
                    Font = SoftTheme.CardBodyFont,
                    ForeColor = SoftTheme.TextSecondary,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                };
                body.Controls.Add(content);
                var actionBar = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                };
                if (primaryText != null && primaryAction != null)
                {
                    actionBar.Controls.Add(MakeAction(primaryText, primaryAction,
                                                       ModernButtonStyle.Primary, 164));
                }
                if (secondaryText != null && secondaryAction != null)
                {
                    actionBar.Controls.Add(MakeAction(secondaryText, secondaryAction,
                                                       ModernButtonStyle.Ghost, 146));
                }
                if (actionBar.Controls.Count != 0)
                {
                    body.Controls.Add(actionBar);
                    actionBar.BringToFront();
                    actionBar.Resize += (sender, e) => LayoutEmptyActions(actionBar);
                }
            }

            private ModernButton MakeAction(string text, Action action,
                                            ModernButtonStyle style, int width)
            {
                var button = new ModernButton
                {
                    Text = text,
                    Style = style,
                    AutoSize = false,
                    Width = width,
                    Height = 36,
                };
                button.Click += (sender, e) => action();
                return button;
            }

            private void BuildCollections()
            {
                var cards = new SoftFlowPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoScroll = true,
                    BackColor = Color.Transparent,
                    Padding = new Padding(0),
                    Margin = new Padding(0),
                };
                var specs = new[]
                {
                    new CollectionSpec(Properties.Resources.ModernShellCollectionStockCareer,
                                       Properties.Resources.ModernShellCollectionStockCareerMeta,
                                       206,
                                       new[]
                                       {
                                           "KerbalEngineer",
                                           "KerbalAlarmClock",
                                           "Restock",
                                           "Waterfall",
                                           "DistantObjectEnhancement",
                                       },
                                       mod => mod.IsInstalled || ModDiscoverView.CategoryFor(mod)
                                                                == DiscoverCategory.Tools),
                    new CollectionSpec(Properties.Resources.ModernShellCollectionJnsq,
                                       Properties.Resources.ModernShellCollectionJnsqMeta,
                                       282,
                                       new[]
                                       {
                                           "Kopernicus",
                                           "Kerbalism",
                                           "OuterPlanetsMod",
                                           "BluedogDB",
                                           "HabTech2",
                                       },
                                       mod => ModDiscoverView.CategoryFor(mod)
                                              == DiscoverCategory.Planets),
                    new CollectionSpec(Properties.Resources.ModernShellCollectionCinematic,
                                       Properties.Resources.ModernShellCollectionCinematicMeta,
                                       330,
                                       new[]
                                       {
                                           "Scatterer",
                                           "EnvironmentalVisualEnhancements",
                                           "Parallax",
                                           "AstronomersVisualPack",
                                       },
                                       mod => ModDiscoverView.CategoryFor(mod)
                                              == DiscoverCategory.Visuals),
                    new CollectionSpec(Properties.Resources.ModernShellCollectionPhysics,
                                       Properties.Resources.ModernShellCollectionPhysicsMeta,
                                       150,
                                       new[]
                                       {
                                           "FerramAerospaceResearch",
                                           "SystemHeat",
                                           "FarFutureTechnologies",
                                           "TexturesUnlimited",
                                       },
                                       mod => ModDiscoverView.CategoryFor(mod)
                                              == DiscoverCategory.Physics
                                              || ModDiscoverView.CategoryFor(mod)
                                                 == DiscoverCategory.Gameplay),
                };
                foreach (var spec in specs)
                {
                    var selected = spec.Identifiers
                                      .Select(identifier => modules.FirstOrDefault(mod =>
                                          string.Equals(mod.Identifier, identifier,
                                                        StringComparison.OrdinalIgnoreCase)))
                                      .Where(mod => mod != null)
                                      .Cast<GUIMod>()
                                      .ToList();
                    if (selected.Count == 0)
                    {
                        selected = modules.Where(spec.Predicate).Take(5).ToList();
                    }
                    if (selected.Count == 0)
                    {
                        selected = modules.Take(5).ToList();
                    }
                    cards.Controls.Add(new CollectionCard(spec.Title, spec.Meta,
                                                           spec.Hue, selected,
                                                           queueCollection));
                }
                cards.Resize += (sender, e) => LayoutCards(cards);
                body.Controls.Add(cards);
                LayoutCards(cards);
            }

            private void BuildSettings()
            {
                for (int i = 0; i < settingToggles.Length; ++i)
                {
                    settingToggles[i] = readSetting(i);
                }
                var groups = new SoftFlowPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoScroll = true,
                    BackColor = Color.Transparent,
                    Padding = new Padding(0),
                    Margin = new Padding(0),
                };
                string version = instance?.Version()?.ToString() ?? "—";
                string path = instance?.GameDir ?? Properties.Resources.ModernShellNoInstance;
                string updated = string.Format(Properties.Resources.ModernShellSettingsUpdated,
                                               DateTime.Now.ToString("h:mm tt"), modules.Count);
                var instanceGroup = new SettingsGroup(
                    Properties.Resources.ModernShellSettingsInstanceGroup);
                instanceGroup.AddRow(Properties.Resources.ModernShellSettingsGameVersion,
                                      version,
                                      MakeValue("KSP " + version, 112));
                instanceGroup.AddRow(Properties.Resources.ModernShellSettingsInstallPath,
                                      path,
                                      MakeValue(Properties.Resources.ModernShellSettingsChange,
                                                86));
                instanceGroup.AddRow(Properties.Resources.ModernShellSettingsRepository,
                                      updated,
                                      MakeAction(Properties.Resources.ModernShellSettingsRefresh,
                                                 () => secondaryAction?.Invoke(),
                                                 ModernButtonStyle.Soft, 86));

                var behaviourGroup = new SettingsGroup(
                    Properties.Resources.ModernShellSettingsBehaviourGroup);
                behaviourGroup.AddRow(Properties.Resources.ModernShellSettingsCheckUpdates,
                                      Properties.Resources.ModernShellSettingsOffWhileDeveloping,
                                      MakeSettingToggle(0));
                behaviourGroup.AddRow(Properties.Resources.ModernShellSettingsAutoApply,
                                      Properties.Resources.ModernShellSettingsQueueWithMod,
                                      MakeSettingToggle(1));
                behaviourGroup.AddRow(Properties.Resources.ModernShellSettingsDevBuilds,
                                      Properties.Resources.ModernShellSettingsPrereleases,
                                      MakeSettingToggle(2));
                // Verification is enforced by the download pipeline and artwork
                // caching is intrinsic to ModVisualMetadataService. Neither has
                // a supported user switch, so do not offer inert preferences.

                long installBytes = modules.Where(mod => mod.IsInstalled)
                                           .Sum(mod => mod.Module.install_size);
                string gameData = installBytes > 0 ? CkanModule.FmtSize(installBytes) : "—";
                var storageGroup = new SettingsGroup(
                    Properties.Resources.ModernShellSettingsStorageGroup);
                storageGroup.AddRow(Properties.Resources.ModernShellSettingsGameData,
                                     string.Format(Properties.Resources.ModernShellSettingsMods,
                                                   modules.Count(mod => mod.IsInstalled)),
                                     MakeValue(gameData, 92));
                storageGroup.AddRow(Properties.Resources.ModernShellSettingsDownloadCache,
                                     Properties.Resources.ModernShellSettingsArtworkDetail,
                                     MakeAction(Properties.Resources.ModernShellSettingsClear,
                                                clearDownloadCache,
                                                ModernButtonStyle.Soft, 76));
                storageGroup.AddRow(Properties.Resources.ModernShellSettingsArtworkCache,
                                     Properties.Resources.ModernShellSettingsArtworkDetail,
                                     MakeAction(Properties.Resources.ModernShellSettingsClear,
                                                clearArtworkCache,
                                                ModernButtonStyle.Soft, 76));
                storageGroup.AddRow(Properties.Resources.ModernShellSettingsRegistry,
                                     string.Format(Properties.Resources.ModernShellRegistryUpdated,
                                                   DateTime.Now.ToString("h:mm tt")),
                                     MakeAction(Properties.Resources.ModernShellSettingsRebuild,
                                                rebuildRegistry,
                                                ModernButtonStyle.Soft, 92));

                groups.Controls.Add(instanceGroup);
                groups.Controls.Add(behaviourGroup);
                groups.Controls.Add(storageGroup);
                groups.Resize += (sender, e) => LayoutGroups(groups);
                body.Controls.Add(groups);
                LayoutGroups(groups);
            }

            private ModernToggle MakeSettingToggle(int index)
            {
                var toggle = new ModernToggle(settingToggles[index]);
                toggle.Changed += value =>
                {
                    settingToggles[index] = value;
                    writeSetting(index, value);
                };
                return toggle;
            }

            private static Control MakeValue(string text, int width)
                => new Label
                {
                    Text = text,
                    Font = SoftTheme.MetaFont,
                    ForeColor = SoftTheme.TextSecondary,
                    BackColor = Color.Transparent,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Width = width,
                    Height = 34,
                };

            private void LayoutEmptyActions(FlowLayoutPanel actions)
            {
                actions.Bounds = new Rectangle(Math.Max(0, (body.Width - actions.PreferredSize.Width) / 2),
                                               Math.Max(0, body.Height - 54),
                                               actions.PreferredSize.Width, 40);
            }

            private void LayoutCards(FlowLayoutPanel cards)
            {
                int gap = SoftTheme.ScaleInt(16, SoftTheme.LayoutDpi);
                int columns = cards.ClientSize.Width >= SoftTheme.ScaleInt(860, SoftTheme.LayoutDpi) ? 2 : 1;
                int width = Math.Max(SoftTheme.ScaleInt(280, SoftTheme.LayoutDpi),
                                     (cards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth
                                      - gap * columns) / columns);
                foreach (Control control in cards.Controls)
                {
                    control.Width = width;
                    control.Height = SoftTheme.ScaleInt(258, SoftTheme.LayoutDpi);
                    control.Margin = new Padding(0, 0, gap, gap);
                }
            }

            private void LayoutGroups(FlowLayoutPanel groups)
            {
                int gap = SoftTheme.ScaleInt(16, SoftTheme.LayoutDpi);
                int columns = groups.ClientSize.Width >= SoftTheme.ScaleInt(1080, SoftTheme.LayoutDpi) ? 3
                            : groups.ClientSize.Width >= SoftTheme.ScaleInt(700, SoftTheme.LayoutDpi) ? 2 : 1;
                int width = Math.Max(SoftTheme.ScaleInt(280, SoftTheme.LayoutDpi),
                                     (groups.ClientSize.Width - SystemInformation.VerticalScrollBarWidth
                                      - gap * columns) / columns);
                foreach (Control control in groups.Controls)
                {
                    control.Width = width;
                    control.Height = control is SettingsGroup group ? group.ContentHeight : 380;
                    control.Margin = new Padding(0, 0, gap, gap);
                }
            }

            private void LayoutSimple()
            {
                int pad = SoftTheme.ScaleInt(28, SoftTheme.LayoutDpi);
                title.Bounds = new Rectangle(pad, SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi),
                                             Math.Max(1, Width - pad * 2), Math.Max(38, title.Font.Height + 8));
                subtitle.Bounds = new Rectangle(pad, title.Bottom + 2,
                                                Math.Max(1, Width - pad * 2), 24);
                body.Bounds = new Rectangle(pad, subtitle.Bottom + 22,
                                            Math.Max(1, Width - pad * 2),
                                            Math.Max(1, Height - subtitle.Bottom - 24));
                foreach (FlowLayoutPanel flow in body.Controls.OfType<FlowLayoutPanel>())
                {
                    if (mode == SimplePageMode.Collections)
                    {
                        LayoutCards(flow);
                    }
                    else if (mode == SimplePageMode.Settings)
                    {
                        LayoutGroups(flow);
                    }
                }
                foreach (FlowLayoutPanel actions in body.Controls.OfType<FlowLayoutPanel>()
                                                            .Where(flow => flow.Controls.Count > 0
                                                                           && flow.Controls[0] is ModernButton))
                {
                    LayoutEmptyActions(actions);
                }
            }

            private sealed class CollectionSpec
            {
                public CollectionSpec(string title, string meta, int hue,
                                      IReadOnlyList<string> identifiers,
                                      Func<GUIMod, bool> predicate)
                {
                    Title = title;
                    Meta = meta;
                    Hue = hue;
                    Identifiers = identifiers;
                    Predicate = predicate;
                }

                public string Title { get; }
                public string Meta { get; }
                public int Hue { get; }
                public IReadOnlyList<string> Identifiers { get; }
                public Func<GUIMod, bool> Predicate { get; }
            }

            private sealed class CollectionCard : Control
            {
                private readonly string title;
                private readonly string meta;
                private readonly int hue;
                private readonly IReadOnlyList<GUIMod> mods;
                private readonly Action<IReadOnlyList<GUIMod>> setup;
                private readonly ModernButton setupButton;
                private bool hovered;

                public CollectionCard(string cardTitle, string cardMeta, int cardHue,
                                      IReadOnlyList<GUIMod> cardMods,
                                      Action<IReadOnlyList<GUIMod>> setupAction)
                {
                    title = cardTitle;
                    meta = cardMeta;
                    hue = cardHue;
                    mods = cardMods;
                    setup = setupAction;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = SoftTheme.Surface;
                    setupButton = new ModernButton
                    {
                        Text = Properties.Resources.ModernShellMakeSetup,
                        Style = ModernButtonStyle.Soft,
                        AutoSize = false,
                        Height = 34,
                    };
                    setupButton.Click += (sender, e) =>
                    {
                        setup(mods);
                        setupButton.Text = Properties.Resources.ModernShellSetupStaged;
                        Invalidate();
                    };
                    Controls.Add(setupButton);
                    MouseEnter += (sender, e) => { hovered = true; Invalidate(); };
                    MouseLeave += (sender, e) => { hovered = false; Invalidate(); };
                    Resize += (sender, e) => LayoutCard();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Parent?.BackColor ?? SoftTheme.Backdrop);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                             Math.Max(1, Height - 1));
                    SoftTheme.FillRounded(g, rect,
                                          hovered ? SoftTheme.SurfaceHover : SoftTheme.Surface,
                                          SoftTheme.RadiusCard);
                    SoftTheme.DrawRounded(g, rect, SoftTheme.Border,
                                          SoftTheme.RadiusCard, 1f);
                    var hero = new Rectangle(0, 0, Width, SoftTheme.ScaleInt(118, SoftTheme.LayoutDpi));
                    using (var path = SoftTheme.RoundedPath(rect, SoftTheme.RadiusCard))
                    {
                        var old = g.Save();
                        g.SetClip(path);
                        using (var brush = new LinearGradientBrush(
                            hero,
                            ColorFromHsl(hue, 0.62f, 0.42f),
                            ColorFromHsl((hue + 40) % 360, 0.55f, 0.22f),
                            150f))
                        {
                            g.FillRectangle(brush, hero);
                        }
                        using (var overlay = new LinearGradientBrush(
                            hero,
                            Color.FromArgb(0, 0, 0, 0),
                            Color.FromArgb(130, 10, 14, 22),
                            90f))
                        {
                            g.FillRectangle(overlay, hero);
                        }
                        g.Restore(old);
                    }
                    TextRenderer.DrawText(g, title, SoftTheme.CardTitleFont,
                                          new Rectangle(16, hero.Bottom - 48,
                                                        Math.Max(80, Width - 32), 24),
                                          Color.White,
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, meta, SoftTheme.MetaFont,
                                          new Rectangle(16, hero.Bottom - 25,
                                                        Math.Max(80, Width - 32), 18),
                                          Color.FromArgb(224, 255, 255, 255),
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                    int left = 16;
                    int stackTop = hero.Bottom + 18;
                    foreach (var mod in mods.Take(5))
                    {
                        var tile = new Rectangle(left, stackTop, 28, 28);
                        SoftTheme.FillRounded(g, tile, ModArtGenerator.SeedColor(mod.Identifier), 8);
                        SoftTheme.DrawRounded(g, tile, SoftTheme.Surface, 8, 2f);
                        TextRenderer.DrawText(g, ModArtGenerator.InitialsForName(mod.Name),
                                              SoftTheme.PillFont, tile, Color.White,
                                              TextFormatFlags.HorizontalCenter
                                              | TextFormatFlags.VerticalCenter
                                              | TextFormatFlags.NoPadding);
                        left += 23;
                    }
                }

                private void LayoutCard()
                {
                    setupButton.Bounds = new Rectangle(16,
                                                        Math.Max(0, Height - 52),
                                                        Math.Max(1, Width - 32), 34);
                }

                private static Color ColorFromHsl(int h, float saturation, float lightness)
                {
                    double s = Math.Max(0, Math.Min(1, saturation));
                    double l = Math.Max(0, Math.Min(1, lightness));
                    double c = (1 - Math.Abs(2 * l - 1)) * s;
                    double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
                    double m = l - c / 2;
                    double r = 0, g = 0, b = 0;
                    if (h < 60) { r = c; g = x; }
                    else if (h < 120) { r = x; g = c; }
                    else if (h < 180) { g = c; b = x; }
                    else if (h < 240) { g = x; b = c; }
                    else if (h < 300) { r = x; b = c; }
                    else { r = c; b = x; }
                    return Color.FromArgb((int)Math.Round((r + m) * 255),
                                          (int)Math.Round((g + m) * 255),
                                          (int)Math.Round((b + m) * 255));
                }
            }

            private sealed class SettingsGroup : Panel
            {
                private readonly Label heading;
                private readonly FlowLayoutPanel rows;

                public SettingsGroup(string text)
                {
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = SoftTheme.Surface;
                    heading = MakeLabel(text, SoftTheme.CardTitleFont,
                                        SoftTheme.TextPrimary);
                    rows = new SoftFlowPanel
                    {
                        FlowDirection = FlowDirection.TopDown,
                        WrapContents = false,
                        BackColor = Color.Transparent,
                        Padding = new Padding(0),
                        Margin = new Padding(0),
                    };
                    Controls.Add(rows);
                    Controls.Add(heading);
                }

                public void AddRow(string text, string detail, Control accessory)
                {
                    rows.Controls.Add(new SettingsRow(text, detail, accessory));
                }

                public int ContentHeight => SoftTheme.Px(72 + rows.Controls.Count * 62);

                protected override void OnLayout(LayoutEventArgs e)
                {
                    base.OnLayout(e);
                    if (heading == null || rows == null)
                    {
                        return;
                    }
                    int pad = SoftTheme.ScaleInt(18, SoftTheme.LayoutDpi);
                    heading.Bounds = new Rectangle(pad, pad, Math.Max(1, Width - pad * 2), SoftTheme.Px(24));
                    rows.Bounds = new Rectangle(pad, heading.Bottom + SoftTheme.Px(8),
                                                Math.Max(1, Width - pad * 2),
                                                Math.Max(1, Height - heading.Bottom - pad - SoftTheme.Px(8)));
                    foreach (Control row in rows.Controls)
                    {
                        row.Width = Math.Max(1, rows.ClientSize.Width);
                        row.Height = SoftTheme.Px(62);
                        row.Margin = Padding.Empty;
                    }
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.Clear(SoftTheme.Backdrop);
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                             Math.Max(1, Height - 1));
                    SoftTheme.FillRounded(e.Graphics, rect, SoftTheme.Surface,
                                          SoftTheme.RadiusCard);
                    SoftTheme.DrawRounded(e.Graphics, rect, SoftTheme.Border,
                                          SoftTheme.RadiusCard, 1f);
                }
            }

            private sealed class SettingsRow : Panel
            {
                private readonly Label main;
                private readonly Label sub;
                private readonly Control accessory;

                public SettingsRow(string text, string detail, Control accessory)
                {
                    this.accessory = accessory;
                    Height = SoftTheme.Px(62);
                    BackColor = Color.Transparent;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.ResizeRedraw, true);
                    main = MakeLabel(text, SoftTheme.SearchFont,
                                         SoftTheme.TextPrimary);
                    main.AutoEllipsis = true;
                    sub = MakeLabel(detail, SoftTheme.MetaFont,
                                        SoftTheme.TextMuted);
                    sub.AutoEllipsis = true;
                    accessory.Dock = DockStyle.None;
                    accessory.Margin = new Padding(0);
                    accessory.AccessibleName = text;
                    Controls.Add(accessory);
                    Controls.Add(main);
                    Controls.Add(sub);
                }

                protected override void OnLayout(LayoutEventArgs e)
                {
                    base.OnLayout(e);
                    if (accessory == null || main == null || sub == null)
                    {
                        return;
                    }
                    int accessoryHeight = SoftTheme.Px(accessory is ModernToggle ? 24 : 34);
                    int accessoryWidth = accessory is ModernToggle ? SoftTheme.Px(44)
                        : Math.Max(SoftTheme.Px(74), TextRenderer.MeasureText(accessory.Text, accessory.Font).Width + SoftTheme.Px(24));
                    accessory.Bounds = new Rectangle(Math.Max(0, Width - accessoryWidth),
                        Math.Max(0, (Height - accessoryHeight) / 2), accessoryWidth, accessoryHeight);
                    int textWidth = Math.Max(1, accessory.Left - SoftTheme.Px(14));
                    main.Bounds = new Rectangle(0, SoftTheme.Px(8), textWidth, SoftTheme.Px(22));
                    sub.Bounds = new Rectangle(0, SoftTheme.Px(31), textWidth, SoftTheme.Px(20));
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        e.Graphics.DrawLine(pen, 0, Height - 1,
                                            Math.Max(0, Width - 1), Height - 1);
                    }
                }
            }

            private sealed class ModernToggle : Control
            {
                private bool on;
                private bool hovered;

                public ModernToggle(bool initial)
                {
                    on = initial;
                    Width = 44;
                    Height = 26;
                    Cursor = Cursors.Hand;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    AccessibleName = "Toggle";
                }

                public event Action<bool>? Changed;

                public bool Value => on;

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(SoftTheme.Surface);
                    var track = new Rectangle(0, 0, Width - 1, Height - 1);
                    SoftTheme.FillRounded(g, track,
                                          on ? SoftTheme.Accent
                                             : hovered ? SoftTheme.SurfaceHover
                                                       : SoftTheme.SurfaceSunken,
                                          Height / 2);
                    SoftTheme.DrawRounded(g, track,
                                          on ? SoftTheme.Accent : SoftTheme.BorderStrong,
                                          Height / 2, 1f);
                    int diameter = Height - 6;
                    int left = on ? Width - diameter - 3 : 3;
                    using (var brush = new SolidBrush(on ? Color.White : SoftTheme.TextMuted))
                    {
                        g.FillEllipse(brush, left, 3, diameter, diameter);
                    }
                }

                protected override void OnMouseEnter(EventArgs e)
                {
                    hovered = true;
                    Invalidate();
                    base.OnMouseEnter(e);
                }

                protected override void OnMouseLeave(EventArgs e)
                {
                    hovered = false;
                    Invalidate();
                    base.OnMouseLeave(e);
                }

                protected override void OnMouseDown(MouseEventArgs e)
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        on = !on;
                        Invalidate();
                        Changed?.Invoke(on);
                    }
                    base.OnMouseDown(e);
                }
            }
        }

        private sealed class ModernStatusBar : Panel
        {
            private readonly Action applyAction;
            private readonly Action clearAction;
            private readonly Action<ModChange> removeAction;
            private readonly ModernButton apply;
            private readonly ModernButton clear;
            private readonly FlowLayoutPanel staged;
            private string status = Properties.Resources.ModernShellNoPending;
            private int pendingCount;
            private string dataKey = "";

            public ModernStatusBar(Action applyChanges, Action clearChanges,
                                   Action<ModChange> removeChange)
            {
                applyAction = applyChanges;
                clearAction = clearChanges;
                removeAction = removeChange;
                BackColor = SoftTheme.StatusSurface;
                staged = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoScroll = false,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    Visible = false,
                };
                apply = new ModernButton
                {
                    Style = ModernButtonStyle.Primary,
                    Icon = ShellIcon.Check,
                    Visible = false,
                };
                clear = new ModernButton
                {
                    Text = Properties.Resources.ModernShellDiscard,
                    Style = ModernButtonStyle.Ghost,
                    Visible = false,
                };
                apply.Click += (sender, e) => applyAction();
                clear.Click += (sender, e) => clearAction();
                Controls.Add(staged);
                Controls.Add(apply);
                Controls.Add(clear);
                Resize += (sender, e) => LayoutStatus();
            }

            public void SetData(IReadOnlyList<GUIMod> modules,
                                GameInstance? instance,
                                IReadOnlyList<ModChange> changes,
                                bool busy)
            {
                int updates = modules.Count(m => m.HasUpdate);
                string version = instance == null
                               ? "KSP"
                               : string.Format("{0} {1}", instance.Game.ShortName,
                                               instance.Version()?.ToString() ?? "");
                string nextKey = updates + ":" + modules.Count + ":" + version + ":" + busy
                    + ":" + string.Join("|", changes.Select(c => c.Mod.identifier + ":" + c.ChangeType));
                if (dataKey == nextKey) return;
                dataKey = nextKey;
                status = string.Format(Properties.Resources.ModernShellStatus,
                                       updates, modules.Count, version);
                pendingCount = changes.Count;
                foreach (Control control in staged.Controls.OfType<Control>().ToArray())
                {
                    control.Dispose();
                }
                staged.Controls.Clear();
                foreach (var change in changes.Take(3))
                {
                    staged.Controls.Add(new StatusChip(change, removeAction));
                }
                if (changes.Count > 3)
                {
                    staged.Controls.Add(new StatusChip(
                        "+" + (changes.Count - 3) + " more"));
                }
                staged.Visible = pendingCount > 0;
                apply.Visible = pendingCount > 0;
                clear.Visible = pendingCount > 0;
                apply.Enabled = !busy;
                clear.Enabled = !busy;
                apply.Text = string.Format(Properties.Resources.ModernShellApplyChanges,
                                           pendingCount);
                Invalidate();
                LayoutStatus();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.StatusSurface;
                Invalidate(true);
            }

            private void LayoutStatus()
            {
                int pad = SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi);
                int buttonHeight = SoftTheme.Px(32);
                int clearWidth = Math.Max(SoftTheme.Px(86), TextRenderer.MeasureText(clear.Text, clear.Font).Width + SoftTheme.Px(24));
                int applyWidth = Math.Max(SoftTheme.Px(144), TextRenderer.MeasureText(apply.Text, apply.Font).Width + SoftTheme.Px(28));
                clear.Bounds = new Rectangle(Math.Max(pad, Width - pad - clearWidth),
                                             (Height - buttonHeight) / 2, clearWidth, buttonHeight);
                apply.Bounds = new Rectangle(Math.Max(pad, clear.Left - SoftTheme.Px(8) - applyWidth),
                                             (Height - buttonHeight) / 2, applyWidth, buttonHeight);
                if (staged.Visible)
                {
                    int left = SoftTheme.ScaleInt(180, SoftTheme.LayoutDpi);
                    int right = Math.Max(left, apply.Left - SoftTheme.ScaleInt(12, SoftTheme.LayoutDpi));
                    int width = Math.Max(1, right - left);
                    staged.Bounds = new Rectangle(left, (Height - buttonHeight) / 2,
                                                  width, buttonHeight);
                    staged.PerformLayout();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    g.DrawLine(pen, 0, 0, Width, 0);
                }
                int left = SoftTheme.ScaleInt(16, SoftTheme.LayoutDpi);
                using (var dot = new SolidBrush(pendingCount > 0 ? SoftTheme.Warning : SoftTheme.Success))
                {
                    g.FillEllipse(dot, left, (Height - 7) / 2, 7, 7);
                }
                int statusRight = pendingCount > 0 && staged.Visible
                                ? staged.Left - SoftTheme.ScaleInt(12, SoftTheme.LayoutDpi)
                                : Width / 2;
                TextRenderer.DrawText(g, status, SoftTheme.MetaFont,
                                      new Rectangle(left + 14, 0,
                                                    Math.Max(80, statusRight - left - 14), Height),
                                      SoftTheme.TextMuted,
                                      TextFormatFlags.Left
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPadding);
                if (pendingCount == 0)
                {
                    TextRenderer.DrawText(g, Properties.Resources.ModernShellNoPending,
                                          SoftTheme.MetaFont,
                                          new Rectangle(Math.Max(left + 120, Width - 170), 0,
                                                        150, Height),
                                          SoftTheme.TextMuted,
                                          TextFormatFlags.Right
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                }
            }

            private sealed class StatusChip : Control
            {
                private readonly string kind;
                private readonly string name;
                private readonly ModChange? change;
                private readonly Action<ModChange>? remove;
                private bool hovered;

                public StatusChip(ModChange change, Action<ModChange>? removeChange)
                {
                    kind = change.ChangeType == GUIModChangeType.Remove ? "Remove"
                         : change.ChangeType == GUIModChangeType.Update ? "Update"
                         : change.ChangeType == GUIModChangeType.Replace ? "Replace"
                         : "Install";
                    name = change.Mod.name;
                    this.change = change;
                    remove = removeChange;
                    Width = Math.Min(168, Math.Max(92,
                        TextRenderer.MeasureText(name, SoftTheme.MetaFont).Width + 74));
                    Height = 28;
                    Margin = new Padding(0, 2, SoftTheme.ScaleInt(6, SoftTheme.LayoutDpi), 2);
                    Font = SoftTheme.MetaFont;
                    Cursor = remove == null ? Cursors.Default : Cursors.Hand;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = Color.Transparent;
                }

                public StatusChip(string label)
                {
                    kind = "";
                    name = label;
                    change = null;
                    remove = null;
                    Width = Math.Min(126, Math.Max(76,
                        TextRenderer.MeasureText(label, SoftTheme.MetaFont).Width + 22));
                    Height = 28;
                    Margin = new Padding(0, 2, SoftTheme.ScaleInt(6, SoftTheme.LayoutDpi), 2);
                    Font = SoftTheme.MetaFont;
                    Cursor = Cursors.Default;
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = Color.Transparent;
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                             Math.Max(1, Height - 1));
                    SoftTheme.FillRounded(g, rect,
                        hovered ? SoftTheme.SurfaceHover : SoftTheme.SurfaceRaised,
                        SoftTheme.RadiusPill);
                    SoftTheme.DrawRounded(g, rect, SoftTheme.Border,
                                          SoftTheme.RadiusPill, 1f);
                    int left = 9;
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        int kindWidth = TextRenderer.MeasureText(kind, SoftTheme.MetaFont).Width + 8;
                        var kindRect = new Rectangle(left, 5, kindWidth, Height - 10);
                        SoftTheme.FillRounded(g, kindRect,
                            kind == "Remove" ? SoftTheme.DangerSoft : SoftTheme.AccentSoft,
                            SoftTheme.ScaleInt(5, SoftTheme.LayoutDpi));
                        TextRenderer.DrawText(g, kind, SoftTheme.MetaFont, kindRect,
                            kind == "Remove" ? SoftTheme.Danger : SoftTheme.Accent,
                            TextFormatFlags.HorizontalCenter
                            | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        left = kindRect.Right + 6;
                    }
                    int right = remove == null ? Width - 8 : Width - 24;
                    TextRenderer.DrawText(g, name, SoftTheme.MetaFont,
                        new Rectangle(left, 0, Math.Max(1, right - left), Height),
                        SoftTheme.TextPrimary,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    if (remove != null)
                    {
                        using (var pen = new Pen(SoftTheme.TextMuted, 1.25f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            int cx = Width - 14;
                            int cy = Height / 2;
                            g.DrawLine(pen, cx - 3, cy - 3, cx + 3, cy + 3);
                            g.DrawLine(pen, cx + 3, cy - 3, cx - 3, cy + 3);
                        }
                    }
                }

                protected override void OnMouseEnter(EventArgs e)
                {
                    hovered = true;
                    Invalidate();
                    base.OnMouseEnter(e);
                }

                protected override void OnMouseLeave(EventArgs e)
                {
                    hovered = false;
                    Invalidate();
                    base.OnMouseLeave(e);
                }

                protected override void OnMouseUp(MouseEventArgs e)
                {
                    if (remove != null && change != null
                        && e.Button == MouseButtons.Left
                        && ClientRectangle.Contains(e.Location))
                    {
                        remove(change);
                    }
                    base.OnMouseUp(e);
                }
            }
        }

        private sealed class LegacyModernProgressSheet : Panel
        {
            private readonly Action cancelAction;
            private readonly Label title;
            private readonly Label body;
            private readonly Label detail;
            private readonly ModernButton cancel;
            private int progress;
            private bool indeterminate;

            public LegacyModernProgressSheet(Action cancelWork)
            {
                cancelAction = cancelWork;
                Size = new Size(SoftTheme.ScaleInt(560, SoftTheme.LayoutDpi),
                                SoftTheme.ScaleInt(248, SoftTheme.LayoutDpi));
                BackColor = SoftTheme.SurfaceRaised;
                Visible = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);
                title = MakeLabel(Properties.Resources.ModernShellApplyingChanges,
                                   SoftTheme.SectionFont, SoftTheme.TextPrimary);
                body = MakeLabel(Properties.Resources.ModernShellApplyingChangesBody,
                                 SoftTheme.CardBodyFont, SoftTheme.TextSecondary);
                detail = MakeLabel("", SoftTheme.MetaFont, SoftTheme.TextMuted);
                cancel = new ModernButton
                {
                    Text = Properties.Resources.ModernShellCancel,
                    Style = ModernButtonStyle.Ghost,
                    AutoSize = false,
                    Width = 92,
                    Height = 34,
                };
                cancel.Click += (sender, e) => cancelAction();
                Controls.Add(title);
                Controls.Add(body);
                Controls.Add(detail);
                Controls.Add(cancel);
                Resize += (sender, e) => LayoutSheet();
            }

            public void SetData(int value, bool marquee, string text)
            {
                progress = Math.Max(0, Math.Min(100, value));
                indeterminate = marquee;
                detail.Text = string.IsNullOrWhiteSpace(text) ? "" : text;
                Invalidate();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.SurfaceRaised;
                title.ForeColor = SoftTheme.TextPrimary;
                body.ForeColor = SoftTheme.TextSecondary;
                detail.ForeColor = SoftTheme.TextMuted;
                Invalidate(true);
            }

            private void LayoutSheet()
            {
                int pad = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                title.Bounds = new Rectangle(pad, pad, Math.Max(1, Width - pad * 2), 28);
                body.Bounds = new Rectangle(pad, title.Bottom + 4,
                                             Math.Max(1, Width - pad * 2), 38);
                detail.Bounds = new Rectangle(pad, body.Bottom + 16,
                                              Math.Max(1, Width - pad * 2), 22);
                cancel.Bounds = new Rectangle(Math.Max(pad, Width - pad - 92),
                                              Height - pad - 34, 92, 34);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                         Math.Max(1, Height - 1));
                SoftTheme.DrawSoftShadow(g, rect, SoftTheme.RadiusCard, 6, 48);
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceRaised,
                                      SoftTheme.RadiusCard);
                SoftTheme.DrawRounded(g, rect, SoftTheme.BorderStrong,
                                      SoftTheme.RadiusCard, 1f);
                int pad = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                var bar = new Rectangle(pad, detail.Bottom + 12,
                                        Math.Max(1, Width - pad * 2), 6);
                SoftTheme.FillRounded(g, bar, SoftTheme.SurfaceSunken, 3);
                if (indeterminate)
                {
                    int segment = Math.Max(24, bar.Width / 3);
                    int left = pad + (Environment.TickCount / 8 %
                                      Math.Max(1, bar.Width + segment)) - segment;
                    var active = new Rectangle(left, bar.Top, segment, bar.Height);
                    SoftTheme.FillRounded(g, active, SoftTheme.Accent, 3);
                }
                else
                {
                    var active = new Rectangle(bar.Left, bar.Top,
                                               Math.Max(1, bar.Width * progress / 100),
                                               bar.Height);
                    SoftTheme.FillRounded(g, active, SoftTheme.Accent, 3);
                }
                TextRenderer.DrawText(g,
                                      indeterminate ? "" : progress + "%",
                                      SoftTheme.MetaFont,
                                      new Rectangle(pad, bar.Bottom + 6,
                                                    bar.Width, 18),
                                      SoftTheme.TextMuted,
                                      TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
        }

        private sealed class ModernProgressSheet : Panel
        {
            private readonly Action cancelAction;
            private readonly Label title;
            private readonly Label body;
            private readonly Label detail;
            private readonly FlowLayoutPanel checks;
            private readonly ProgressSummary summary;
            private readonly FlowLayoutPanel operations;
            private readonly ModernButton cancel;
            private readonly List<ProgressOperationRow> operationRows =
                new List<ProgressOperationRow>();
            private string changeKey = "";
            private int progress;
            private bool indeterminate;

            public ModernProgressSheet(Action cancelWork)
            {
                cancelAction = cancelWork;
                Size = new Size(SoftTheme.ScaleInt(580, SoftTheme.LayoutDpi),
                                SoftTheme.ScaleInt(520, SoftTheme.LayoutDpi));
                BackColor = SoftTheme.SurfaceRaised;
                Visible = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);

                title = MakeLabel(Properties.Resources.ModernShellApplyingChanges,
                                  SoftTheme.SectionFont, SoftTheme.TextPrimary);
                body = MakeLabel(Properties.Resources.ModernShellApplyingChangesBody,
                                 SoftTheme.CardBodyFont, SoftTheme.TextSecondary);
                detail = MakeLabel(Properties.Resources.MainWaitPleaseWait,
                                   SoftTheme.MetaFont, SoftTheme.TextMuted);
                checks = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = false,
                    BackColor = Color.Transparent,
                    Padding = new Padding(0),
                    Margin = new Padding(0),
                };
                summary = new ProgressSummary();
                operations = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = Color.Transparent,
                    Padding = new Padding(0, 0, SoftTheme.ScaleInt(8, SoftTheme.LayoutDpi), 0),
                    Margin = new Padding(0),
                };
                cancel = new ModernButton
                {
                    Text = Properties.Resources.ModernShellCancel,
                    Style = ModernButtonStyle.Ghost,
                    Width = SoftTheme.ScaleInt(92, SoftTheme.LayoutDpi),
                    Height = SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi),
                };
                cancel.Click += (sender, e) => cancelAction();

                Controls.Add(title);
                Controls.Add(body);
                Controls.Add(detail);
                Controls.Add(checks);
                Controls.Add(summary);
                Controls.Add(operations);
                Controls.Add(cancel);
                Resize += (sender, e) => LayoutSheet();
                LayoutSheet();
            }

            public void SetChanges(IReadOnlyList<ModChange> changes)
            {
                var safe = changes ?? Array.Empty<ModChange>();
                string nextKey = string.Join("|", safe.Select(change =>
                    change.Mod.identifier + ":" + change.ChangeType));
                if (nextKey == changeKey)
                {
                    return;
                }
                changeKey = nextKey;
                foreach (Control control in operations.Controls.OfType<Control>().ToArray())
                {
                    control.Dispose();
                }
                operations.Controls.Clear();
                operationRows.Clear();
                checks.Controls.Clear();
                AddCheck("Current instance is ready", SoftTheme.Success);
                int cached = safe.Count(change => change.Mod.download != null
                                                  && change.Mod.download.Count > 0);
                AddCheck(cached > 0
                            ? string.Format("{0} package{1} available to CKAN",
                                            cached, cached == 1 ? " is" : "s are")
                            : "Packages will be resolved from the configured repositories",
                         SoftTheme.TextSecondary);
                AddCheck(safe.Count > 0
                            ? string.Format("{0} operation{1} in this transaction",
                                            safe.Count, safe.Count == 1 ? "" : "s")
                            : "No operations are currently staged",
                         SoftTheme.TextSecondary);
                foreach (var change in safe)
                {
                    var row = new ProgressOperationRow(change);
                    operationRows.Add(row);
                    operations.Controls.Add(row);
                }
                LayoutSheet();
            }

            private void AddCheck(string text, Color color)
            {
                var check = MakeLabel("✓  " + text, SoftTheme.MetaFont, color);
                check.Height = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                check.Margin = new Padding(0);
                checks.Controls.Add(check);
            }

            public void SetData(int value, bool marquee, string text)
            {
                progress = Math.Max(0, Math.Min(100, value));
                indeterminate = marquee;
                detail.Text = string.IsNullOrWhiteSpace(text)
                            ? Properties.Resources.MainWaitPleaseWait : text;
                summary.SetData(progress, indeterminate);
                int active = -1;
                for (int i = 0; i < operationRows.Count; ++i)
                {
                    if (!string.IsNullOrWhiteSpace(text)
                        && text.IndexOf(operationRows[i].ModuleName,
                                       StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        active = i;
                        break;
                    }
                }
                if (active < 0 && operationRows.Count > 0 && progress < 100)
                {
                    active = Math.Min(operationRows.Count - 1,
                                      progress * operationRows.Count / 100);
                }
                for (int i = 0; i < operationRows.Count; ++i)
                {
                    operationRows[i].SetState(progress >= 100 || i < active
                                                  ? ProgressOperationState.Done
                                                  : i == active
                                                    ? ProgressOperationState.Working
                                                    : ProgressOperationState.Queued,
                                              progress >= 100 ? 100
                                              : i == active ? progress : i < active ? 100 : 0);
                }
                Invalidate();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.SurfaceRaised;
                title.ForeColor = SoftTheme.TextPrimary;
                body.ForeColor = SoftTheme.TextSecondary;
                detail.ForeColor = SoftTheme.TextMuted;
                summary.Invalidate();
                operations.Invalidate(true);
                Invalidate(true);
            }

            private void LayoutSheet()
            {
                int pad = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                int inner = Math.Max(1, Width - pad * 2);
                title.Bounds = new Rectangle(pad, pad, inner, 28);
                body.Bounds = new Rectangle(pad, title.Bottom + 4, inner, 34);
                detail.Bounds = new Rectangle(pad, body.Bottom + 6, inner, 22);
                checks.Bounds = new Rectangle(pad, detail.Bottom + 8, inner,
                                               SoftTheme.ScaleInt(74, SoftTheme.LayoutDpi));
                summary.Bounds = new Rectangle(pad, checks.Bottom + 6, inner,
                                               SoftTheme.ScaleInt(76, SoftTheme.LayoutDpi));
                int actionHeight = SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi);
                int actionTop = Height - pad - actionHeight;
                operations.Bounds = new Rectangle(pad, summary.Bottom + 10, inner,
                                                  Math.Max(70, actionTop - summary.Bottom - 20));
                cancel.Bounds = new Rectangle(Math.Max(pad, Width - pad - cancel.Width),
                                              actionTop, cancel.Width, actionHeight);
                foreach (Control control in checks.Controls)
                {
                    control.Width = Math.Max(1, checks.ClientSize.Width);
                }
                foreach (Control control in operations.Controls)
                {
                    control.Width = Math.Max(1, operations.ClientSize.Width
                                                 - operations.Padding.Horizontal);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                         Math.Max(1, Height - 1));
                SoftTheme.DrawSoftShadow(g, rect, SoftTheme.RadiusCard, 8, 60);
                SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceRaised,
                                      SoftTheme.RadiusCard);
                SoftTheme.DrawRounded(g, rect, SoftTheme.BorderStrong,
                                      SoftTheme.RadiusCard, 1f);
            }

            private enum ProgressOperationState
            {
                Queued,
                Working,
                Done,
            }

            private sealed class ProgressOperationRow : Control
            {
                private readonly ModChange change;
                private ProgressOperationState state = ProgressOperationState.Queued;
                private int progress;

                public ProgressOperationRow(ModChange pending)
                {
                    change = pending;
                    Height = SoftTheme.ScaleInt(54, SoftTheme.LayoutDpi);
                    Margin = new Padding(0);
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                }

                public string ModuleName => change.Mod.name;

                public void SetState(ProgressOperationState value, int percent)
                {
                    state = value;
                    progress = Math.Max(0, Math.Min(100, percent));
                    Invalidate();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Parent?.BackColor ?? SoftTheme.SurfaceRaised);
                    var mod = change.Mod;
                    string verb = change.ChangeType == GUIModChangeType.Remove
                                ? Properties.Resources.ModernShellRemove
                                : change.ChangeType == GUIModChangeType.Update
                                  || change.ChangeType == GUIModChangeType.Replace
                                  ? Properties.Resources.ModernShellUpdate
                                  : Properties.Resources.ModernShellInstall;
                    string status = state == ProgressOperationState.Done
                                  ? "Done"
                                  : state == ProgressOperationState.Working
                                    ? "Working..."
                                    : "Queued";
                    TextRenderer.DrawText(g, mod.name, SoftTheme.CardTitleFont,
                                          new Rectangle(0, 3, Math.Max(70, Width - 180), 20),
                                          SoftTheme.TextPrimary,
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, verb + " - " + mod.version,
                                          SoftTheme.MetaFont,
                                          new Rectangle(0, 24, Math.Max(70, Width - 180), 18),
                                          SoftTheme.TextSecondary,
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, status, SoftTheme.MetaFont,
                                          new Rectangle(Math.Max(70, Width - 170), 3, 80, 18),
                                          state == ProgressOperationState.Done
                                            ? SoftTheme.Success : SoftTheme.TextMuted,
                                          TextFormatFlags.Right | TextFormatFlags.NoPadding);
                    var bar = new Rectangle(0, Height - 6, Math.Max(1, Width - 1), 4);
                    SoftTheme.FillRounded(g, bar, SoftTheme.SurfaceSunken, 2);
                    if (progress > 0)
                    {
                        SoftTheme.FillRounded(g,
                            new Rectangle(bar.Left, bar.Top,
                                          Math.Max(2, bar.Width * progress / 100), bar.Height),
                            state == ProgressOperationState.Done
                                ? SoftTheme.Success : SoftTheme.Accent, 2);
                    }
                    using (var pen = new Pen(SoftTheme.Border, 1f))
                    {
                        g.DrawLine(pen, 0, Height - 1, Math.Max(0, Width - 1), Height - 1);
                    }
                }
            }

            private sealed class ProgressSummary : Control
            {
                private int progress;
                private bool indeterminate;

                public ProgressSummary()
                {
                    SetStyle(ControlStyles.AllPaintingInWmPaint
                             | ControlStyles.OptimizedDoubleBuffer
                             | ControlStyles.SupportsTransparentBackColor
                             | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = Color.Transparent;
                }

                public void SetData(int value, bool marquee)
                {
                    progress = value;
                    indeterminate = marquee;
                    Invalidate();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var rect = new Rectangle(0, 0, Math.Max(1, Width - 1),
                                             Math.Max(1, Height - 1));
                    SoftTheme.FillRounded(g, rect, SoftTheme.SurfaceSunken,
                                          SoftTheme.RadiusControl);
                    SoftTheme.DrawRounded(g, rect, SoftTheme.Border,
                                          SoftTheme.RadiusControl, 1f);
                    int pad = SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi);
                    var bar = new Rectangle(pad, SoftTheme.ScaleInt(14, SoftTheme.LayoutDpi),
                                            Math.Max(1, Width - pad * 2), 6);
                    SoftTheme.FillRounded(g, bar, SoftTheme.Mix(SoftTheme.TextPrimary,
                                                                 SoftTheme.SurfaceSunken,
                                                                 0.87f), 3);
                    if (indeterminate)
                    {
                        int segment = Math.Max(30, bar.Width / 3);
                        int left = pad + (Environment.TickCount / 8
                                          % Math.Max(1, bar.Width + segment)) - segment;
                        SoftTheme.FillRounded(g, new Rectangle(left, bar.Top, segment, bar.Height),
                                              SoftTheme.Accent, 3);
                    }
                    else
                    {
                        SoftTheme.FillRounded(g,
                            new Rectangle(bar.Left, bar.Top,
                                          Math.Max(1, bar.Width * progress / 100), bar.Height),
                            progress >= 100 ? SoftTheme.Success : SoftTheme.Accent, 3);
                    }
                    TextRenderer.DrawText(g, indeterminate ? "Working..." : progress + "%",
                                          SoftTheme.MetaFont,
                                          new Rectangle(pad, bar.Bottom + 10,
                                                        Math.Max(40, Width - pad * 2), 20),
                                          SoftTheme.TextMuted,
                                          TextFormatFlags.Right | TextFormatFlags.NoPadding);
                }
            }
        }

        private sealed class OverlayPanel : Panel
        {
            public OverlayPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw
                         | ControlStyles.UserPaint, true);
                BackColor = Color.Transparent;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                Region = new Region(new Rectangle(0, 0, Width, Height));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                // Design notes sit directly over the app so their numbered
                // pins can explain the composition. The palette and transfer
                // sheet are modal and keep the concept's dimmed scrim.
                if (Controls.OfType<NotesPanel>().Any(panel => panel.Visible))
                {
                    return;
                }
                using (var brush = new SolidBrush(SoftTheme.WithAlpha(SoftTheme.Backdrop,
                                                                       178)))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }
            }
        }

        private sealed class NotesPanel : Panel
        {
            private sealed class DesignNote
            {
                public DesignNote(int x, int y, string heading, string body)
                {
                    X = x;
                    Y = y;
                    Heading = heading;
                    Body = body;
                }

                public int X { get; }
                public int Y { get; }
                public string Heading { get; }
                public string Body { get; }
            }

            private static readonly DesignNote[] designNotes =
            {
                new DesignNote(16, 5, "No menu bar",
                    "File/Edit/View/Settings/Help collapse into one searchable command palette. Menus hide capability; a palette advertises it."),
                new DesignNote(4, 46, "One navigation spine",
                    "The rail replaces the toolbar, the view switch and the menu bar. Five destinations, always visible, with live counts."),
                new DesignNote(36, 14, "Search is the primary control",
                    "Full-text across name, author, description and identifier, with filters as pills rather than a modal dialog."),
                new DesignNote(34, 50, "Shelves, not rows",
                    "The 12,000-row grid becomes intent-shaped shelves. The ranked shelf retitles itself when you change the sort."),
                new DesignNote(47, 78, "Action at the point of decision",
                    "The card's button writes the change set directly - the same property the old grid checkbox wrote, so conflicts and the dry run stay shared."),
                new DesignNote(56, 96, "The change set is the app",
                    "Staged work is always on screen, named and cancellable, applied as one transaction. No more unapplied changes on quit."),
                new DesignNote(84, 36, "Inspector, not a tab strip",
                    "Details slide in beside the browse surface. Overview, changelog, relationships and file preview, without losing your place."),
                new DesignNote(91, 15, "One switch, four densities",
                    "Fewer, bigger tiles - or the whole catalogue as a flat list. The same switch drives the shelves, the gallery and the Library."),
            };

            private int selected = -1;

            public NotesPanel(Action closeNotes)
            {
                // A transparent app-sized annotation layer mirrors the
                // concept's notes-layer. Visibility is still controlled by the
                // stage button and the N shortcut in the owning shell.
                TabStop = true;
                Dock = DockStyle.Fill;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw
                         | ControlStyles.Selectable, true);
                BackColor = Color.Transparent;
                AccessibleName = Properties.Resources.DiscoverDesignNotes;
                MouseDown += (sender, e) =>
                {
                    if (e.Button != MouseButtons.Left)
                    {
                        return;
                    }
                    int hit = HitTest(e.Location);
                    if (hit >= 0)
                    {
                        selected = selected == hit ? -1 : hit;
                        Invalidate();
                        Focus();
                    }
                    else if (selected >= 0 && !CalloutBounds(designNotes[selected])
                                                      .Contains(e.Location))
                    {
                        selected = -1;
                        Invalidate();
                    }
                };
                MouseMove += (sender, e) =>
                {
                    Cursor = HitTest(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
                };
                MouseLeave += (sender, e) => Cursor = Cursors.Default;
            }

            public void ResetSelection()
            {
                selected = -1;
                Invalidate();
            }

            public void RefreshTheme()
            {
                Invalidate();
            }

            private int HitTest(Point point)
            {
                for (int i = 0; i < designNotes.Length; ++i)
                {
                    if (PinBounds(designNotes[i]).Contains(point))
                    {
                        return i;
                    }
                }
                return -1;
            }

            private Rectangle PinBounds(DesignNote note)
            {
                int size = SoftTheme.ScaleInt(24, SoftTheme.LayoutDpi);
                return new Rectangle((int)Math.Round(Width * note.X / 100.0),
                                     (int)Math.Round(Height * note.Y / 100.0),
                                     size, size);
            }

            private Rectangle CalloutBounds(DesignNote note)
            {
                int width = Math.Min(SoftTheme.ScaleInt(264, SoftTheme.LayoutDpi),
                                     Math.Max(SoftTheme.ScaleInt(220, SoftTheme.LayoutDpi), Width - 24));
                int pad = SoftTheme.ScaleInt(15, SoftTheme.LayoutDpi);
                int titleHeight = SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi);
                int bodyWidth = Math.Max(80, width - pad * 2);
                int bodyHeight = TextRenderer.MeasureText(note.Body,
                    SoftTheme.CardBodyFont, new Size(bodyWidth, 800),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                int height = pad + titleHeight + SoftTheme.ScaleInt(3, SoftTheme.LayoutDpi)
                           + bodyHeight + pad;
                int left = Math.Min((int)Math.Round(Width * Math.Min(note.X + 3, 66) / 100.0),
                                    Math.Max(12, Width - width - 12));
                int top = Math.Min((int)Math.Round(Height * Math.Min(note.Y + 5, 80) / 100.0),
                                   Math.Max(12, Height - height - 12));
                return new Rectangle(left, top, width, height);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < designNotes.Length; ++i)
                {
                    var pin = PinBounds(designNotes[i]);
                    SoftTheme.FillRounded(g, pin, SoftTheme.Accent, pin.Width / 2);
                    using (var pen = new Pen(SoftTheme.Backdrop,
                                             SoftTheme.Scale(2f, SoftTheme.LayoutDpi)))
                    {
                        g.DrawEllipse(pen, pin);
                    }
                    TextRenderer.DrawText(g, (i + 1).ToString(), SoftTheme.PillFont,
                                          pin, Color.White,
                                          TextFormatFlags.HorizontalCenter
                                          | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding);
                }

                if (selected < 0 || selected >= designNotes.Length)
                {
                    return;
                }
                var note = designNotes[selected];
                var callout = CalloutBounds(note);
                SoftTheme.DrawSoftShadow(g, callout, SoftTheme.RadiusCard, 8, 58);
                SoftTheme.FillRounded(g, callout, SoftTheme.SurfaceRaised,
                                      SoftTheme.RadiusCard);
                SoftTheme.DrawRounded(g, callout, SoftTheme.BorderStrong,
                                      SoftTheme.RadiusCard, 1f);
                int pad = SoftTheme.ScaleInt(15, SoftTheme.LayoutDpi);
                var heading = new Rectangle(callout.X + pad, callout.Y + pad,
                                            callout.Width - pad * 2,
                                            SoftTheme.ScaleInt(20, SoftTheme.LayoutDpi));
                TextRenderer.DrawText(g, note.Heading, SoftTheme.TitleFont, heading,
                                      SoftTheme.TextPrimary,
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                var copy = new Rectangle(callout.X + pad,
                                         heading.Bottom + SoftTheme.ScaleInt(3, SoftTheme.LayoutDpi),
                                         callout.Width - pad * 2,
                                         Math.Max(1, callout.Bottom - heading.Bottom
                                                  - pad - SoftTheme.ScaleInt(3, SoftTheme.LayoutDpi)));
                TextRenderer.DrawText(g, note.Body, SoftTheme.CardBodyFont, copy,
                                      SoftTheme.TextSecondary,
                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        private sealed class PalettePanel : Panel
        {
            private readonly TextBox search;
            private readonly FlowLayoutPanel commands;
            private List<PaletteCommand> allCommands = new List<PaletteCommand>();

            public PalettePanel()
            {
                BackColor = SoftTheme.Surface;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw, true);
                search = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = SoftTheme.Surface,
                    ForeColor = SoftTheme.TextPrimary,
                    Font = SoftTheme.SearchFont,
                    TabStop = true,
                };
                commands = new SoftFlowPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = SoftTheme.Surface,
                    Padding = new Padding(SoftTheme.ScaleInt(10, SoftTheme.LayoutDpi)),
                    Margin = new Padding(0),
                };
                search.TextChanged += (sender, e) => Filter();
                search.KeyDown += (sender, e) =>
                {
                    if (e.KeyCode == Keys.Enter && commands.Controls.Count > 0
                        && commands.Controls[0].Tag is PaletteCommand command)
                    {
                        e.SuppressKeyPress = true;
                        CommandInvoked?.Invoke(command);
                    }
                    else if (e.KeyCode == Keys.Down && commands.Controls.Count > 0)
                    {
                        e.SuppressKeyPress = true;
                        commands.Controls[0].Focus();
                    }
                };
                Controls.Add(commands);
                Controls.Add(search);
                Size = new Size(SoftTheme.ScaleInt(560, SoftTheme.LayoutDpi),
                                SoftTheme.ScaleInt(410, SoftTheme.LayoutDpi));
                Resize += (sender, e) => LayoutPalette();
            }

            public event Action<PaletteCommand>? CommandInvoked;

            public void Open(IEnumerable<PaletteCommand> source)
            {
                allCommands = source.ToList();
                search.Clear();
                BuildButtons(allCommands);
                LayoutPalette();
                search.Focus();
            }

            public void RefreshTheme()
            {
                BackColor = SoftTheme.Surface;
                search.BackColor = SoftTheme.Surface;
                search.ForeColor = SoftTheme.TextPrimary;
                commands.BackColor = SoftTheme.Surface;
                Invalidate(true);
            }

            public void Close()
            {
                search.Clear();
            }

            private void Filter()
            {
                string query = search.Text.Trim();
                BuildButtons(allCommands.Where(command => query.Length == 0
                    || command.Label.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || command.Meta.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0));
            }

            private void BuildButtons(IEnumerable<PaletteCommand> source)
            {
                foreach (Control old in commands.Controls.Cast<Control>().ToArray())
                {
                    old.Dispose();
                }
                commands.Controls.Clear();
                foreach (var command in source)
                {
                    var button = new ModernButton
                    {
                        Text = command.Label,
                        Style = ModernButtonStyle.Ghost,
                        Icon = command.Icon,
                        MetaText = command.Meta,
                        AutoSize = false,
                        Width = Math.Max(1, Width - SoftTheme.ScaleInt(34, SoftTheme.LayoutDpi)),
                        Height = SoftTheme.ScaleInt(42, SoftTheme.LayoutDpi),
                        AccessibleName = command.Label,
                        Tag = command,
                    };
                    button.Click += (sender, e) => CommandInvoked?.Invoke(command);
                    button.KeyDown += (sender, e) =>
                    {
                        int index = commands.Controls.IndexOf(button);
                        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                        {
                            e.SuppressKeyPress = true;
                            int next = index + (e.KeyCode == Keys.Down ? 1 : -1);
                            if (next < 0)
                            {
                                search.Focus();
                            }
                            else if (next < commands.Controls.Count)
                            {
                                commands.Controls[next].Focus();
                                commands.ScrollControlIntoView(commands.Controls[next]);
                            }
                        }
                    };
                    commands.Controls.Add(button);
                }
                LayoutPalette();
            }

            private void LayoutPalette()
            {
                int pad = SoftTheme.ScaleInt(16, SoftTheme.LayoutDpi);
                search.Bounds = new Rectangle(pad, pad, Math.Max(1, Width - pad * 2),
                                              SoftTheme.ScaleInt(32, SoftTheme.LayoutDpi));
                commands.Bounds = new Rectangle(0, search.Bottom + 4,
                                                Width, Math.Max(1, Height - search.Bottom - 4));
                foreach (Control control in commands.Controls)
                {
                    control.Width = Math.Max(1, commands.ClientSize.Width - commands.Padding.Horizontal
                                             - SystemInformation.VerticalScrollBarWidth - control.Margin.Horizontal);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                SoftTheme.DrawRounded(e.Graphics, rect, SoftTheme.BorderStrong,
                                      SoftTheme.RadiusCard, 1f);
                using (var pen = new Pen(SoftTheme.Border, 1f))
                {
                    e.Graphics.DrawLine(pen, 16, SoftTheme.ScaleInt(55, SoftTheme.LayoutDpi),
                                        Width - 16, SoftTheme.ScaleInt(55, SoftTheme.LayoutDpi));
                }
            }
        }

        private sealed class PaletteCommand
        {
            public PaletteCommand(string label, string meta, Action action,
                                   ShellIcon icon = ShellIcon.Search)
            {
                Label = label;
                Meta = meta;
                Action = action;
                Icon = icon;
            }

            public string Label { get; }
            public string Meta { get; }
            public Action Action { get; }
            public ShellIcon Icon { get; }
        }
    }

    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal static class ModernGraphicsExtensions
    {
        public static void DrawRoundedRectangle(this Graphics graphics, Pen pen,
                                                 Rectangle bounds, int radius)
        {
            using (var path = SoftTheme.RoundedPath(bounds, radius))
            {
                graphics.DrawPath(pen, path);
            }
        }
    }
}
