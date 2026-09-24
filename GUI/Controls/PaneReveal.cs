using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    /// <summary>
    /// A one-shot fade-and-slide entrance for a pane's content.
    ///
    /// The content is snapshotted, the real controls are hidden, and the
    /// snapshot is painted back with a falling alpha and a shrinking offset
    /// until it lands. Animating a snapshot rather than the controls
    /// themselves is the whole point: moving or fading real controls would
    /// re-run layout on every frame, which flickers and drops frames on a
    /// subtree this deep.
    ///
    /// Every failure path degrades to "no animation" - if the pane cannot be
    /// snapshotted, the content simply appears, which is exactly the old
    /// behaviour.
    /// </summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal sealed class PaneReveal : IDisposable
    {
        /// <summary>How far the content travels, in 96 DPI design units.</summary>
        private const int DesignTravel = 18;

        /// <summary>
        /// How much the content is scaled up as it lands, as a fraction. Small
        /// enough to read as a settle rather than as a zoom.
        /// </summary>
        private const float DesignScale = 0.015f;

        /// <summary>
        /// Total run time. Long enough to be seen, short enough that clicking
        /// through the list quickly never feels like waiting.
        /// </summary>
        private const int DurationMs = 200;

        /// <summary>
        /// Where the fade starts. Deliberately not zero: fading in from
        /// nothing flashes an empty pane on the way, which on a white sheet
        /// reads as a glitch rather than as an entrance. Starting part way in
        /// keeps the content legible the whole way and lets the slide do the
        /// work of showing where it came from.
        /// </summary>
        private const float MinAlpha = 0.3f;

        private const int FrameMs = 15;

        private readonly Control host;
        private readonly Control content;
        private readonly Timer   timer;

        private Bitmap? frame;
        private int     startedAt;
        private int     travel;
        private bool    running;

        public PaneReveal(Control host, Control content)
        {
            this.host    = host;
            this.content = content;
            timer = new Timer { Interval = FrameMs };
            timer.Tick += (sender, e) => Step();
        }

        /// <summary>
        /// Play the entrance. A run that is already in flight is snapped to its
        /// end first, so holding an arrow key down through the mod list cannot
        /// leave a half-faded snapshot on screen.
        /// </summary>
        public void Play()
        {
            Finish();

            if (host.IsDisposed || content.IsDisposed
                || !host.IsHandleCreated || !content.IsHandleCreated
                || !host.Visible || !content.Visible
                || content.Width <= 0 || content.Height <= 0)
            {
                return;
            }

            try
            {
                frame = new Bitmap(content.Width, content.Height, PixelFormat.Format32bppPArgb);
                content.DrawToBitmap(frame, new Rectangle(0, 0, content.Width, content.Height));
            }
            catch
            {
                // DrawToBitmap is best effort: some controls refuse to render
                // into a bitmap. Without a snapshot there is nothing to
                // animate, so let the pane appear as it always did.
                frame?.Dispose();
                frame = null;
                return;
            }

            travel    = SoftTheme.ScaleInt(DesignTravel, host.DeviceDpi);
            startedAt = Environment.TickCount;
            running   = true;

            content.Visible = false;
            host.Paint += PaintFrame;
            host.Invalidate();
            timer.Start();
        }

        /// <summary>
        /// Snap straight to the end state and drop the snapshot.
        /// </summary>
        public void Finish()
        {
            timer.Stop();
            if (!running)
            {
                return;
            }
            running = false;
            host.Paint -= PaintFrame;
            if (!content.IsDisposed)
            {
                content.Visible = true;
            }
            frame?.Dispose();
            frame = null;
            if (!host.IsDisposed)
            {
                host.Invalidate();
            }
        }

        private void Step()
        {
            if (!running || frame == null
                || unchecked(Environment.TickCount - startedAt) >= DurationMs)
            {
                Finish();
                return;
            }
            host.Invalidate();
        }

        private void PaintFrame(object? sender, PaintEventArgs e)
        {
            if (!running || frame == null)
            {
                return;
            }

            // Keep subtraction 32-bit so a TickCount rollover preserves elapsed time.
            float t = Math.Min(1f, unchecked(Environment.TickCount - startedAt) / (float)DurationMs);

            // Ease-out cubic. The content covers most of its distance almost
            // immediately and then settles, which is the curve the platforms
            // this is modelled on use for something arriving on screen.
            float eased = 1f - ((1f - t) * (1f - t) * (1f - t));

            // Negative, so the content starts tucked under the pane's leading
            // edge and slides out into place. That is what makes it read as
            // arriving from the sidebar rather than as receding towards it.
            int dx = -(int)Math.Round((1f - eased) * travel);
            float scale = 1f - (DesignScale * (1f - eased));
            float alpha = MinAlpha + ((1f - MinAlpha) * eased);

            int w = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int h = Math.Max(1, (int)Math.Round(frame.Height * scale));
            var dest = new Rectangle(dx + ((frame.Width - w) / 2),
                                     (frame.Height - h) / 2,
                                     w, h);

            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            using (var attributes = new ImageAttributes())
            {
                var matrix = new ColorMatrix { Matrix33 = alpha };
                attributes.SetColorMatrix(matrix,
                                          ColorMatrixFlag.Default,
                                          ColorAdjustType.Bitmap);
                e.Graphics.DrawImage(frame, dest, 0, 0, frame.Width, frame.Height,
                                     GraphicsUnit.Pixel, attributes);
            }
        }

        public void Dispose()
        {
            Finish();
            timer.Dispose();
        }
    }
}
