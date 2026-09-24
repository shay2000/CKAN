using System;
using System.Collections.Generic;
using System.Drawing;
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
    /// The detail-pane "Changelog" tab: a readable version history for the
    /// selected mod, built from local repository metadata and optionally
    /// enriched with GitHub release notes pulled on demand.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    public sealed class Changelog : UserControl
    {
        private readonly Label           heading;
        private readonly Label           subheading;
        private readonly Button          fetchButton;
        private readonly Label           sourceNote;
        private readonly FlowLayoutPanel listPanel;
        private readonly Label           emptyLabel;

        private ModChangelogService? service;
        private Func<IRegistryQuerier?>? registryProvider;
        private GUIMod?              selectedModule;
        private CancellationTokenSource? fetchCancellation;

        public Changelog()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
                     true);
            // One continuous sheet, the same as every other tab in the pane.
            // This used to be a backdrop-coloured well with white cards in it,
            // which read as a panel belonging to a different surface and left a
            // visible seam where it met the white tab strip. The cards carry a
            // hairline outline of their own, so they stay separated on a white
            // page without needing the fill to do the work.
            BackColor = SoftTheme.Surface;

            heading = new Label
            {
                Text      = Properties.Resources.ChangelogHeading,
                Font      = SoftTheme.SectionFont,
                ForeColor = SoftTheme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };
            subheading = new Label
            {
                Text      = Properties.Resources.ChangelogSubheading,
                Font      = SoftTheme.MetaFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };
            fetchButton = new Button
            {
                Text      = Properties.Resources.ChangelogFetchButton,
                FlatStyle = FlatStyle.Flat,
                Font      = SoftTheme.ActionFont,
                ForeColor = SoftTheme.Accent,
                BackColor = SoftTheme.Surface,
                AutoSize  = false,
                Height    = 26,
                Cursor    = Cursors.Hand,
            };
            fetchButton.FlatAppearance.BorderSize         = 0;
            fetchButton.FlatAppearance.MouseOverBackColor = SoftTheme.AccentSoft;
            fetchButton.FlatAppearance.MouseDownBackColor = SoftTheme.AccentSoft;
            fetchButton.Click += (sender, e) => FetchRemote();
            using (var g = CreateGraphics())
            {
                fetchButton.Width = TextRenderer.MeasureText(g, fetchButton.Text,
                                                             fetchButton.Font).Width + 22;
            }

            sourceNote = new Label
            {
                Font      = SoftTheme.MetaFont,
                ForeColor = SoftTheme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize  = true,
            };

            emptyLabel = new Label
            {
                Text      = Properties.Resources.ChangelogEmpty,
                Font      = SoftTheme.CardBodyFont,
                ForeColor = SoftTheme.TextSecondary,
                BackColor = Color.Transparent,
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock      = DockStyle.Fill,
            };

            listPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = SoftTheme.Surface,
                Padding       = new Padding(0, 4, 0, 8),
                Margin        = new Padding(0),
            };
            typeof(FlowLayoutPanel)
                .GetProperty("DoubleBuffered",
                             System.Reflection.BindingFlags.Instance
                             | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(listPanel, true, null);

            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 58,
                BackColor = SoftTheme.Surface,
            };
            header.Controls.Add(heading);
            header.Controls.Add(subheading);
            header.Controls.Add(fetchButton);
            header.Controls.Add(sourceNote);
            header.Resize += (sender, e) => LayoutHeader(header);
            LayoutHeader(header);

            Controls.Add(listPanel);
            Controls.Add(header);

            ShowEmpty(Properties.Resources.ChangelogEmpty);
        }

        public void SetService(ModChangelogService? changelogService)
        {
            service = changelogService;
        }

        public void SetRegistryProvider(Func<IRegistryQuerier?> provider)
        {
            registryProvider = provider;
        }

        /// <summary>
        /// Repaint the changelog when the native shell switches between the
        /// concept's dark and light palettes.  The entry views paint directly
        /// from SoftTheme, so invalidating the tree is enough once the host
        /// controls have their new fills and foregrounds.
        /// </summary>
        public void RefreshTheme()
        {
            BackColor = SoftTheme.Surface;
            heading.ForeColor = SoftTheme.TextPrimary;
            subheading.ForeColor = SoftTheme.TextSecondary;
            fetchButton.BackColor = SoftTheme.Surface;
            fetchButton.ForeColor = SoftTheme.Accent;
            sourceNote.ForeColor = SoftTheme.TextMuted;
            listPanel.BackColor = SoftTheme.Surface;
            emptyLabel.ForeColor = SoftTheme.TextSecondary;
            Invalidate(true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public GUIMod? SelectedModule
        {
            get => selectedModule;
            set
            {
                selectedModule = value;
                ReloadLocal();
            }
        }

        private void LayoutHeader(Panel header)
        {
            heading.Location = new Point(2, 2);
            subheading.Location = new Point(4, heading.Bottom + 2);
            int right = Math.Max(120, header.Width - 4);
            fetchButton.Location = new Point(Math.Max(4, right - fetchButton.Width), 2);
            sourceNote.Location = new Point(4, subheading.Bottom + 4);
        }

        private void SetEntries(IReadOnlyList<ModChangelogEntry> entries, bool remote)
        {
            listPanel.SuspendLayout();
            try
            {
                foreach (Control control in listPanel.Controls)
                {
                    control.Dispose();
                }
                listPanel.Controls.Clear();

                if (entries.Count == 0)
                {
                    ShowEmpty(Properties.Resources.ChangelogEmpty);
                    sourceNote.Text = "";
                    return;
                }

                listPanel.Visible = true;
                foreach (var entry in entries)
                {
                    var view = new ChangelogEntryView(entry)
                    {
                        Width = Math.Max(200, listPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6),
                    };
                    listPanel.Controls.Add(view);
                }
                sourceNote.Text = remote ? Properties.Resources.ChangelogSourceRemote
                                         : Properties.Resources.ChangelogSourceLocal;
                emptyLabel.Visible = false;
            }
            finally
            {
                listPanel.ResumeLayout(true);
            }
        }

        private void ShowEmpty(string message)
        {
            emptyLabel.Text = message;
            emptyLabel.Visible = true;
            listPanel.Visible = false;
            if (!Controls.Contains(emptyLabel))
            {
                Controls.Add(emptyLabel);
                emptyLabel.BringToFront();
            }
        }

        private void ReloadLocal()
        {
            CancelFetch();
            var registry = registryProvider?.Invoke();
            if (selectedModule == null || service == null || registry == null)
            {
                SetEntries(Array.Empty<ModChangelogEntry>(), false);
                return;
            }
            try
            {
                SetEntries(service.GetLocalHistory(registry, selectedModule), false);
            }
            catch
            {
                SetEntries(Array.Empty<ModChangelogEntry>(), false);
            }
        }

        private async void FetchRemote()
        {
            var registry = registryProvider?.Invoke();
            var mod = selectedModule;
            if (service == null || mod == null || registry == null)
            {
                return;
            }

            CancelFetch();
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            fetchCancellation = cancellation;
            fetchButton.Enabled = false;
            fetchButton.Text = Properties.Resources.ChangelogFetching;
            try
            {
                var entries = await service.GetFullChangelogAsync(registry, mod, token);
                if (!IsDisposed && !token.IsCancellationRequested
                    && ReferenceEquals(fetchCancellation, cancellation)
                    && selectedModule == mod)
                {
                    SetEntries(entries, true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (!IsDisposed && ReferenceEquals(fetchCancellation, cancellation))
                {
                    sourceNote.Text = Properties.Resources.ChangelogFetchFailed;
                }
            }
            finally
            {
                // An earlier request may finish after cancellation and a new
                // fetch. Only the current request owns the button and token.
                if (ReferenceEquals(fetchCancellation, cancellation))
                {
                    fetchCancellation = null;
                    cancellation.Dispose();
                    if (!IsDisposed)
                    {
                        fetchButton.Enabled = true;
                        fetchButton.Text = Properties.Resources.ChangelogFetchButton;
                    }
                }
            }
        }

        private void CancelFetch()
        {
            var cancellation = fetchCancellation;
            fetchCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            if (!IsDisposed)
            {
                fetchButton.Enabled = true;
                fetchButton.Text = Properties.Resources.ChangelogFetchButton;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int width = Math.Max(200, listPanel.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
            foreach (Control control in listPanel.Controls)
            {
                control.Width = width;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelFetch();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// One row of the changelog: version, when it landed, a couple of
        /// status pills and the release notes.
        /// </summary>
        private sealed class ChangelogEntryView : Control
        {
            private const int NotesLines = 3;

            private readonly ModChangelogEntry entry;

            public ChangelogEntryView(ModChangelogEntry entry)
            {
                this.entry = entry;
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint
                         | ControlStyles.ResizeRedraw, true);
                BackColor = SoftTheme.Surface;
                Margin    = new Padding(0, 3, 0, 3);
                Cursor    = entry.Url != null ? Cursors.Hand : Cursors.Default;
                Height    = 104;
                Click += (sender, e) =>
                {
                    if (entry.Url != null)
                    {
                        Util.TryOpenWebPage(entry.Url);
                    }
                };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // The card's own background, so the gap between two cards is
                // the page rather than a second colour. The outline drawn below
                // is what separates them.
                g.Clear(SoftTheme.Surface);

                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                SoftTheme.FillRounded(g, bounds, SoftTheme.Surface, SoftTheme.RadiusControl);
                SoftTheme.DrawRounded(g, bounds, SoftTheme.Border, SoftTheme.RadiusControl, 1f);

                int pad = 12;
                int y = pad;

                // Pills
                int pillX = pad;
                pillX = DrawPill(g, pillX, y, entry.Version, SoftTheme.AccentSoft, SoftTheme.AccentDeep);
                if (entry.IsInstalled)
                {
                    pillX = DrawPill(g, pillX, y, Properties.Resources.ChangelogPillInstalled,
                                     SoftTheme.SuccessSoft, SoftTheme.Success);
                }
                else if (entry.IsLatest)
                {
                    pillX = DrawPill(g, pillX, y, Properties.Resources.ChangelogPillLatest,
                                     SoftTheme.SurfaceSunken, SoftTheme.TextSecondary);
                }
                if (entry.IsSelected)
                {
                    pillX = DrawPill(g, pillX, y, Properties.Resources.ChangelogPillQueued,
                                     SoftTheme.WarningSoft, SoftTheme.Warning);
                }
                DrawPill(g, pillX, y, entry.Source, SoftTheme.SurfaceSunken, SoftTheme.TextMuted);

                // Date, right aligned
                if (entry.ReleaseDate.HasValue)
                {
                    var date = entry.ReleaseDate.Value.ToLocalTime().ToString("d");
                    var size = TextRenderer.MeasureText(g, date, SoftTheme.MetaFont);
                    TextRenderer.DrawText(g, date, SoftTheme.MetaFont,
                                          new Point(Width - pad - size.Width, y + 3),
                                          SoftTheme.TextMuted,
                                          TextFormatFlags.NoPadding);
                }

                y += 26;

                var title = entry.Title ?? entry.Version;
                TextRenderer.DrawText(g, title, SoftTheme.CardTitleFont,
                                      new Rectangle(pad, y, Width - (pad * 2), 20),
                                      SoftTheme.TextPrimary,
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y += 20;

                var notes = string.IsNullOrWhiteSpace(entry.Notes)
                    ? Properties.Resources.ChangelogNoNotes
                    : entry.Notes!;
                var notesRect = new Rectangle(pad, y, Width - (pad * 2),
                                              Math.Max(1, Height - y - pad));
                using (var brush = new SolidBrush(SoftTheme.TextSecondary))
                using (var format = new StringFormat(StringFormat.GenericTypographic)
                       {
                           Trimming    = StringTrimming.EllipsisWord,
                           FormatFlags = StringFormatFlags.NoClip,
                       })
                {
                    g.DrawString(notes, SoftTheme.CardBodyFont, brush,
                                 new RectangleF(notesRect.X, notesRect.Y,
                                                notesRect.Width,
                                                SoftTheme.CardBodyFont.GetHeight(g) * NotesLines),
                                 format);
                }
            }

            private static int DrawPill(Graphics g, int x, int y, string text, Color back, Color fore)
            {
                var size = TextRenderer.MeasureText(g, text, SoftTheme.PillFont,
                                                    new Size(int.MaxValue, int.MaxValue),
                                                    TextFormatFlags.NoPadding);
                var rect = new Rectangle(x, y, size.Width + 14, 20);
                SoftTheme.FillRounded(g, rect, back, 10);
                TextRenderer.DrawText(g, text, SoftTheme.PillFont, rect, fore,
                                      TextFormatFlags.HorizontalCenter
                                      | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPadding);
                return rect.Right + 5;
            }
        }
    }
}
