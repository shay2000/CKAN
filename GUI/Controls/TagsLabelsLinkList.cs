using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

using CKAN.Extensions;
using CKAN.GUI.Attributes;

namespace CKAN.GUI
{
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public partial class TagsLabelsLinkList : FlowLayoutPanel
    {
        public TagsLabelsLinkList()
            : base()
        {
            ToolTip.ScaleFonts();
        }

        [ForbidGUICalls]
        public void UpdateTagsAndLabels(IEnumerable<ModuleTag>?   tags,
                                        IEnumerable<ModuleLabel>? labels)
        {
            Util.Invoke(this, () =>
            {
                SuspendLayout();
                Controls.Clear();
                if (tags != null)
                {
                    foreach (ModuleTag tag in tags)
                    {
                        Controls.Add(TagLabelLink(
                            tag.Name, tag, tagToolTip,
                            new LinkLabelLinkClickedEventHandler(TagLinkLabel_LinkClicked)));
                    }
                }
                if (labels != null)
                {
                    foreach (ModuleLabel mlbl in labels)
                    {
                        Controls.Add(TagLabelLink(
                            mlbl.Name, mlbl, Properties.Resources.FilterLinkToolTip,
                            new LinkLabelLinkClickedEventHandler(LabelLinkLabel_LinkClicked)));
                    }
                }
                ResumeLayout();
            });
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string TagToolTipText
        {
            get => tagToolTip;
            set
            {
                tagToolTip = value;
                foreach (var lbl in Controls.OfType<LinkLabel>()
                                            .Where(lbl => lbl.Tag is ModuleTag))
                {
                    ToolTip.SetToolTip(lbl, tagToolTip);
                }
            }
        }

        public event Action<ModuleTag,   bool>? TagClicked;
        public event Action<ModuleLabel, bool>? LabelClicked;
        public event Action<ModuleTag>?         ShowHideTag;
        public event Action<ModuleLabel>?       AddRemoveModuleLabel;

        private string tagToolTip = Properties.Resources.FilterLinkToolTip;

        private static int LinkLabelBottom(LinkLabel? lbl)
            => lbl == null ? 0
                           : lbl.Bottom + lbl.Margin.Bottom;

        public int TagsHeight
            => LinkLabelBottom(Controls.OfType<LinkLabel>()
                                       .LastOrDefault());

        /// <summary>
        /// A tag or label chip.
        ///
        /// The stock <see cref="LinkLabel"/> renders as bare coloured text,
        /// which reads as a hyperlink and is the one element of this header
        /// that still looks like a web page. A row of tags is presented as
        /// chips on the platforms this is modelled on, so the text is drawn on
        /// a soft rounded pill instead.
        ///
        /// It stays a <see cref="LinkLabel"/> so every bit of the existing
        /// wiring - left click to filter, middle click to merge, the context
        /// menu, the tooltips - keeps working untouched.
        /// </summary>
        private sealed class TagChip : LinkLabel
        {
            /// <summary>Horizontal padding inside the pill, in 96 DPI units.</summary>
            private const int DesignPadX = 9;

            /// <summary>Vertical padding inside the pill, in 96 DPI units.</summary>
            private const int DesignPadY = 3;

            private readonly Color fill;
            private bool hovered;

            public TagChip(string text, Color fill)
            {
                this.fill = fill;

                SetStyle(ControlStyles.SupportsTransparentBackColor
                         | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.ResizeRedraw, true);

                AutoSize         = true;
                BackColor        = Color.Transparent;
                LinkBehavior     = LinkBehavior.NeverUnderline;
                VisitedLinkColor = IdleText;
                ActiveLinkColor  = HoverText;
                LinkColor        = IdleText;
                Text             = text;

                ApplyMetrics();
            }

            /// <summary>
            /// The chip text at rest.
            ///
            /// Darker than <see cref="SoftTheme.TextSecondary"/>, which is what
            /// a caption on the page uses. The chip sits on its own fill rather
            /// than on the sheet, so at chip size the muted grey the rest of the
            /// header uses comes out at around 3.5:1 against it - readable in a
            /// screenshot and a strain in a list of twelve tags.
            /// </summary>
            private static Color IdleText
                => SoftTheme.Mix(SoftTheme.TextSecondary, SoftTheme.TextPrimary, 0.45f);

            private static Color HoverText => SoftTheme.AccentDeep;

            /// <summary>
            /// The fill for a chip with no colour of its own.
            ///
            /// A shade stronger than <see cref="SoftTheme.SurfaceSunken"/>:
            /// that token is tuned for a large sunken area, and at chip size on
            /// a white sheet it comes close enough to the page that the pill
            /// only reads where a rounded corner happens to catch the light.
            /// </summary>
            internal static Color NeutralFill
                => SoftTheme.Mix(SoftTheme.SurfaceSunken, SoftTheme.BorderStrong, 0.45f);

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ApplyMetrics();
            }

            protected override void OnDpiChangedAfterParent(EventArgs e)
            {
                base.OnDpiChangedAfterParent(e);
                ApplyMetrics();
            }

            /// <summary>
            /// The padding is what gives the text room inside the pill, and
            /// because the label is auto-sized it also feeds the control's
            /// preferred size - which is what the row height is measured from.
            /// </summary>
            private void ApplyMetrics()
            {
                int padX = SoftTheme.ScaleInt(DesignPadX, DeviceDpi);
                int padY = SoftTheme.ScaleInt(DesignPadY, DeviceDpi);
                Padding = new Padding(padX, padY, padX, padY);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                LinkColor = HoverText;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                LinkColor = IdleText;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    Color back = hovered ? SoftTheme.AccentSoft : fill;
                    // Half the height, so the corners close into a pill.
                    SoftTheme.FillRounded(e.Graphics, bounds, back, bounds.Height / 2);
                }
                base.OnPaint(e);
            }
        }

        private LinkLabel TagLabelLink(string name,
                                       object tag,
                                       string toolTip,
                                       LinkLabelLinkClickedEventHandler onClick)
        {
            // A label carries the user's own colour, so the chip takes a pale
            // wash of it; a tag has no colour of its own and gets the neutral
            // chip the rest of the design uses for secondary content.
            var custom = (tag as ModuleLabel)?.Color;
            var fill   = custom is Color c && c.A == byte.MaxValue && c != Color.Transparent
                             ? SoftTheme.Mix(c, SoftTheme.Surface, 0.72f)
                             : TagChip.NeutralFill;

            var link = new TagChip(name, fill)
            {
                Margin = new Padding(0, 2, 6, 2),
                Tag    = tag,
            };
            link.LinkClicked += onClick;
            ToolTip.SetToolTip(link, toolTip);
            return link;
        }

        private void TagLinkLabel_LinkClicked(object?                        sender,
                                              LinkLabelLinkClickedEventArgs? e)
        {
            if (sender is LinkLabel { Tag: ModuleTag t } llbl)
            {
                switch (e)
                {
                    case { Button: MouseButtons.Left }:
                        TagClicked?.Invoke(t, ModifierKeys.HasAnyFlag(Keys.Control, Keys.Shift));
                        break;

                    case { Button: MouseButtons.Middle }:
                        TagClicked?.Invoke(t, true);
                        break;

                    case { Button: MouseButtons.Right }:
                        var showHideLink = new ToolStripMenuItem(string.Format(Properties.Resources.UtilShowHideLink,
                                                                               t.Name));
                        showHideLink.Click += (sender, ev) => ShowHideTag?.Invoke(t);
                        OpenLinkLabelContextMenu(llbl, showHideLink);
                        break;
                }
            }
        }

        private void LabelLinkLabel_LinkClicked(object?                        sender,
                                                LinkLabelLinkClickedEventArgs? e)
        {
            if (sender is LinkLabel { Tag: ModuleLabel l } llbl)
            {
                switch (e)
                {
                    case { Button: MouseButtons.Left }:
                        LabelClicked?.Invoke(l, ModifierKeys.HasAnyFlag(Keys.Control, Keys.Shift));
                        break;
                    case { Button: MouseButtons.Middle }:
                        LabelClicked?.Invoke(l, true);
                        break;
                    case { Button: MouseButtons.Right }:
                        var addRemoveLink = new ToolStripMenuItem(string.Format(Properties.Resources.UtilAddRemoveModuleLink,
                                                                                l.Name));
                        addRemoveLink.Click += (sender, ev) => AddRemoveModuleLabel?.Invoke(l);
                        OpenLinkLabelContextMenu(llbl, addRemoveLink);
                        break;
                }
            }
        }

        private static void OpenLinkLabelContextMenu(LinkLabel                  label,
                                                     params ToolStripMenuItem[] options)
        {
            var menu = new ContextMenuStrip
            {
                Renderer = new FlatToolStripRenderer(),
            };
            menu.Items.AddRange(options);
            menu.ScaleFonts();
            menu.Show(label.PointToScreen(new Point(0, label.Height)));
        }

        private readonly ToolTip ToolTip = new ToolTip()
        {
            AutoPopDelay = 10000,
            InitialDelay = 250,
            ReshowDelay  = 250,
            ShowAlways   = true,
        };
    }
}
