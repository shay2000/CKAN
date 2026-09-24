using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

using Autofac;

using CKAN.Configuration;
using CKAN.Versioning;

namespace CKAN.GUI
{
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public partial class ModInfo : UserControl
    {
        private readonly Changelog         changelogTab      = new Changelog();
        private readonly ModChangelogService changelogService = new ModChangelogService();
        private TabPage?                   changelogTabPage;
        private PaneReveal?                reveal;

        public ModInfo()
        {
            InitializeComponent();
            MetadataModuleNameTextBox.ScaleFonts();
            MetadataModuleAbstractLabel.ScaleFonts();
            MetadataModuleDescriptionTextBox.ScaleFonts();
            Contents.OnDownloadClick += gmod => OnDownloadClick?.Invoke(gmod);
            Relationships.ModuleDoubleClicked += mod => ModuleDoubleClicked?.Invoke(mod);
            tagsLabelsLinkList.ShowHideTag += t => ShowHideTag?.Invoke(t);
            tagsLabelsLinkList.AddRemoveModuleLabel += l => AddRemoveModuleLabel?.Invoke(l);
            AddChangelogTab();
        }

        /// <summary>
        /// Bring the detail pane onto the same palette as the Discover surface.
        ///
        /// The pane is one continuous sheet sitting on the backdrop, the way a
        /// content column sits beside a sidebar: no band of chrome between the
        /// header and the page, and a single hairline under the tab strip. That
        /// matters more than any individual colour here - the pane used to
        /// alternate white / grey / white down its height, which is what made
        /// it read as three unrelated strips stacked together.
        ///
        /// Only colours and the name's alignment are touched. The designer's
        /// fonts are left alone because <c>ScaleFonts</c> has already applied
        /// the user's text scale factor to them, and overriding them here would
        /// quietly ignore that setting.
        /// </summary>
        public void ApplySoftTheme()
        {
            BackColor = SoftTheme.Backdrop;
            ApplyGutter();

            ModInfoTable.BackColor = SoftTheme.Surface;

            // The designer centres the name, which reads as a dialog caption
            // rather than as a heading. Every other heading in the app is
            // left-aligned, and a left-aligned title is what lets the tags and
            // the abstract line up beneath it.
            MetadataModuleNameTextBox.TextAlign = HorizontalAlignment.Left;
            MetadataModuleNameTextBox.BackColor = SoftTheme.Surface;
            MetadataModuleNameTextBox.ForeColor = SoftTheme.TextPrimary;

            // The name box is a multiline TextBox docked into an auto-sized
            // row, so the row inherits the designer's fixed height for it -
            // which at 150% leaves a heading-sized hole between the title and
            // the tags below it. Size the row to the text instead, so the
            // title sits as a heading rather than floating in space.
            ModInfoTable.RowStyles[0].SizeType = SizeType.Absolute;
            ModInfoTable.RowStyles[0].Height =
                (int)Math.Ceiling(MetadataModuleNameTextBox.Font.GetHeight())
                + SoftTheme.ScaleInt(4, DeviceDpi);

            // A little air between the abstract and the tab strip.
            ModInfoTabControl.Margin = new Padding(0, SoftTheme.ScaleInt(6, DeviceDpi), 0, 0);

            // Set explicitly rather than left transparent: a TextBox renders
            // its own background through the native edit control, and relying
            // on transparency here is what produced the white band this is
            // replacing.
            MetadataModuleAbstractLabel.BackColor = SoftTheme.Surface;
            MetadataModuleAbstractLabel.ForeColor = SoftTheme.TextSecondary;

            MetadataModuleDescriptionTextBox.BackColor = SoftTheme.Surface;
            MetadataModuleDescriptionTextBox.ForeColor = SoftTheme.TextSecondary;

            tagsLabelsLinkList.BackColor = SoftTheme.Surface;

            foreach (TabPage page in ModInfoTabControl.TabPages)
            {
                // The visual-style background would otherwise win over BackColor.
                page.UseVisualStyleBackColor = false;
                page.BackColor               = SoftTheme.Surface;
                ApplySoftSurface(page);
            }

            // The strip takes the sheet colour too, so the header is one
            // surface rather than a band of chrome above a white page.
            ModInfoTabControl.StripColor = SoftTheme.Surface;
            ModInfoTabControl.BackColor  = SoftTheme.Surface;
            ModInfoTabControl.RefreshTabMetrics();

            Metadata.ApplySoftTheme();

            // After the walker rather than before: the walker only knows
            // the generic rules, and the tabs that own a colour or a control
            // of their own - the versions table, whose row colours come from
            // its legend, and the contents buttons - have to get the last
            // word.
            Versions.ApplySoftTheme();
            Contents.ApplySoftTheme();

            if (reveal == null)
            {
                reveal = new PaneReveal(this, ModInfoTable);
                Disposed += (sender, e) => reveal?.Dispose();
            }
        }

        /// <summary>
        /// The backdrop gutter around the sheet, in 96 DPI design units.
        /// </summary>
        private const int DesignGutter = 14;

        /// <summary>
        /// Bring the tab contents onto the sheet: the sheet colour instead of
        /// the system window colour, and no hard grey frame around the lists
        /// and trees.
        ///
        /// Walked from the pages rather than done with a method per tab, so a
        /// tab added later is covered without anyone having to remember to
        /// theme it.
        ///
        /// Buttons are deliberately not covered here. There is no property that
        /// says "this button has already been given a look of its own", and the
        /// changelog tab's fetch button is one of those - so a rule general
        /// enough to catch the contents buttons would repaint it. The tabs that
        /// own a button style it themselves instead.
        /// </summary>
        private static void ApplySoftSurface(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                switch (child)
                {
                    case ListView list:
                        list.BorderStyle = BorderStyle.None;
                        list.BackColor   = SoftTheme.Surface;
                        list.ForeColor   = SoftTheme.TextPrimary;
                        break;

                    case TreeView tree:
                        tree.BorderStyle = BorderStyle.None;
                        tree.BackColor   = SoftTheme.Surface;
                        tree.ForeColor   = SoftTheme.TextPrimary;
                        // No connector lines. The dotted ones the tree draws by
                        // default come out near-black, and they cannot be
                        // recoloured: TreeView.LineColor is ignored for as long
                        // as visual styles are on for the control. Dropping them
                        // altogether is also the more modern reading of a tree -
                        // the expander and the indent carry the hierarchy on
                        // their own.
                        tree.ShowLines   = false;
                        break;

                    case Label label when label.BackColor == SystemColors.Control:
                        // The designer captured the old dialog colour, which
                        // shows as grey blocks on a white sheet.
                        label.BackColor = Color.Transparent;
                        break;
                }

                if (child.HasChildren)
                {
                    ApplySoftSurface(child);
                }
            }
        }

        /// <summary>
        /// Inset the sheet from the pane's edges.
        ///
        /// This is also what makes the rounded corners possible: the docked
        /// table is inset by the gutter, so its own square corners land well
        /// inside the card and are invisible against it, while the card's
        /// corners stay clear of it and show the backdrop through.
        /// </summary>
        private void ApplyGutter()
        {
            int gutter = SoftTheme.ScaleInt(DesignGutter, DeviceDpi);
            Padding = new Padding(gutter, gutter, gutter, gutter);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyGutter();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Painted before base.OnPaint, because that is what raises the
            // Paint event the reveal draws its snapshot into - the sheet has to
            // be underneath it.
            SoftTheme.FillRounded(e.Graphics, ClientRectangle, SoftTheme.Surface,
                                  SoftTheme.ScaleInt(SoftTheme.RadiusCard, DeviceDpi));
            base.OnPaint(e);
        }

        /// <summary>
        /// The Changelog tab is created in code rather than the designer so that
        /// the existing localized tab layout stays untouched.
        /// </summary>
        private void AddChangelogTab()
        {
            changelogTabPage = new TabPage(Properties.Resources.ModInfoChangelogTab)
            {
                Name    = "ChangelogTabPage",
                Padding = new Padding(6),
                UseVisualStyleBackColor = true,
            };
            changelogTab.Dock = DockStyle.Fill;
            changelogTab.SetService(changelogService);
            changelogTab.SetRegistryProvider(CurrentRegistry);
            changelogTabPage.Controls.Add(changelogTab);
            ModInfoTabControl.Controls.Add(changelogTabPage);
        }

        private static IRegistryQuerier? CurrentRegistry()
        {
            if (manager?.CurrentInstance is GameInstance inst)
            {
                var repoData = ServiceLocator.Container.Resolve<RepositoryDataManager>();
                return RegistryManager.Instance(inst, repoData).registry;
            }
            return null;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public GUIMod? SelectedModule
        {
            set
            {
                if (value != null
                    && manager?.CurrentInstance?.VersionCriteria()
                       is GameVersionCriteria crit)
                {
                    ModInfoTabControl.Enabled = true;
                    UpdateHeaderInfo(value, crit);
                }
                else
                {
                    ModInfoTabControl.Enabled = false;
                }
                if (value != selectedModule)
                {
                    selectedModule = value;
                    LoadTab(value);
                    // Only when a different mod is picked: switching tabs
                    // inside the pane should not re-run the entrance, and
                    // neither should a refresh that re-selects the same mod.
                    reveal?.Play();
                }
            }
            get => selectedModule;
        }

        public void SwitchTab(string name)
        {
            ModInfoTabControl.SelectedTab = ModInfoTabControl.TabPages[name];
        }

        public event Action<GUIMod>?            OnDownloadClick;
        public event Action<SavedSearch, bool>? OnChangeFilter;
        public event Action<CkanModule>?        ModuleDoubleClicked;
        public event Action<ModuleTag>?         ShowHideTag;
        public event Action<ModuleLabel>?       AddRemoveModuleLabel;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ModInfoTable.RowStyles[1].Height = ModInfoTable.Padding.Vertical
                                               + ModInfoTable.Margin.Vertical
                                               + tagsLabelsLinkList.TagsHeight;
            if (MetadataModuleDescriptionTextBox != null
                && !string.IsNullOrEmpty(MetadataModuleDescriptionTextBox.Text))
            {
                MetadataModuleDescriptionTextBox.Height = DescriptionHeight;
            }
        }

        private GUIMod? selectedModule;

        private void LoadTab(GUIMod? gm)
        {
            switch (ModInfoTabControl.SelectedTab?.Name)
            {
                case "MetadataTabPage":
                    if (gm != null)
                    {
                        Metadata.UpdateModInfo(gm);
                    }
                    break;

                case "ContentTabPage":
                    Contents.SelectedModule = gm;
                    break;

                case "RelationshipTabPage":
                    Relationships.SelectedModule = gm;
                    break;

                case "VersionsTabPage":
                    Versions.SelectedModule = gm;
                    if (Platform.IsMono)
                    {
                        // Workaround: make sure the ListView headers are drawn
                        Versions.ForceRedraw();
                    }
                    break;

                case "ChangelogTabPage":
                    changelogTab.SelectedModule = gm;
                    break;
            }
        }

        // When switching tabs ensure that the resulting tab is updated.
        private void ModInfoTabControl_SelectedIndexChanged(object? sender, EventArgs? e)
        {
            if (SelectedModule != null)
            {
                LoadTab(SelectedModule);
            }
        }

        private static GameInstanceManager? manager => Main.Instance?.Manager;

        private int TextBoxStringHeight(TextBox tb)
            => tb.Padding.Vertical + tb.Margin.Vertical
                + tb.CreateGraphics().StringHeight(tb.Text, tb.Font,
                                                   tb.Width - tb.Padding.Horizontal - tb.Margin.Horizontal);

        private int DescriptionHeight => TextBoxStringHeight(MetadataModuleDescriptionTextBox);

        private void UpdateHeaderInfo(GUIMod gmod, GameVersionCriteria crit)
        {
            var module = gmod.Module;
            Util.Invoke(this, () =>
            {
                ModInfoTabControl.SuspendLayout();

                MetadataModuleNameTextBox.Text = module.name;
                UpdateTagsAndLabels(module);
                MetadataModuleAbstractLabel.Text = module.@abstract.Replace("&", "&&");
                MetadataModuleDescriptionTextBox.Text = module.description
                    ?.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                MetadataModuleDescriptionTextBox.Height = DescriptionHeight;

                // Set/reset alert icons to show a user why a mod is incompatible
                RelationshipTabPage.ImageKey = "";
                VersionsTabPage.ImageKey     = "";
                if (gmod.IsIncompatible)
                {
                    var pageToAlert = module.IsCompatible(crit) ? RelationshipTabPage : VersionsTabPage;
                    pageToAlert.ImageKey = "Stop";
                }
                if (manager?.CurrentInstance is GameInstance inst)
                {
                    var filters = ServiceLocator.Container.Resolve<IConfiguration>()
                                                          .GetGlobalInstallFilters(inst.Game)
                                                          .Concat(inst.InstallFilters)
                                                          .ToHashSet();
                    ContentTabPage.ImageKey = ModuleLabels.IgnoreMissingIdentifiers(inst)
                                                          .Contains(gmod.Identifier)
                                              || (gmod.InstalledMod?.AllFilesExist(inst, filters)
                                                                   ?? true)
                                                  ? ""
                                                  : "Stop";
                }

                ModInfoTabControl.ResumeLayout();
            });
        }

        private static ModuleLabelList ModuleLabels => ModuleLabelList.ModuleLabels;

        private void UpdateTagsAndLabels(CkanModule mod)
        {
            if (manager?.CurrentInstance is GameInstance inst)
            {
                var registry = RegistryManager.Instance(inst, ServiceLocator.Container.Resolve<RepositoryDataManager>())
                                              .registry;
                tagsLabelsLinkList.UpdateTagsAndLabels(
                    registry?.Tags
                             .Where(t => t.Value.ModuleIdentifiers.Contains(mod.identifier))
                             .OrderBy(t => t.Key)
                             .Select(t => t.Value),
                    ModuleLabels?.LabelsFor(inst.Name)
                                 .Where(l => l.ContainsModule(inst.Game, mod.identifier))
                                 .OrderBy(l => l.Name));
                Util.Invoke(tagsLabelsLinkList, () =>
                {
                    ModInfoTable.RowStyles[1].Height = ModInfoTable.Padding.Vertical
                                                       + ModInfoTable.Margin.Vertical
                                                       + tagsLabelsLinkList.TagsHeight;
                });
            }
        }


        private void tagsLabelsLinkList_TagClicked(ModuleTag tag, bool merge)
            => OnChangeFilter?.Invoke(ModList.FilterToSavedSearch(Main.Instance!.CurrentInstance!,
                                                                  GUIModFilter.Tag, ModuleLabelList.ModuleLabels,
                                                                  tag, null),
                                      merge);

        private void tagsLabelsLinkList_LabelClicked(ModuleLabel label, bool merge)
            => OnChangeFilter?.Invoke(ModList.FilterToSavedSearch(Main.Instance!.CurrentInstance!,
                                                                  GUIModFilter.CustomLabel, ModuleLabelList.ModuleLabels,
                                                                  null, label),
                                      merge);

        private void Metadata_OnChangeFilter(SavedSearch search, bool merge)
        {
            OnChangeFilter?.Invoke(search, merge);
        }
    }
}
