using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

namespace CKAN.GUI
{
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal sealed class SoftFlowPanel : FlowLayoutPanel
    {
        private readonly SoftScrollChrome chrome;
        public SoftFlowPanel()
        {
            DoubleBuffered = true;
            chrome = new SoftScrollChrome(this);
        }
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
        protected override void Dispose(bool disposing)
        {
            if (disposing) chrome.Dispose();
            base.Dispose(disposing);
        }
    }

    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal sealed class SoftScrollPanel : Panel
    {
        private readonly SoftScrollChrome chrome;
        public SoftScrollPanel()
        {
            DoubleBuffered = true;
            chrome = new SoftScrollChrome(this);
        }
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
        protected override void Dispose(bool disposing)
        {
            if (disposing) chrome.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Client-painted scroll thumb, with native wheel/scroll semantics.</summary>
    #if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
    #endif
    internal sealed class SoftScrollChrome : NativeWindow, IDisposable
    {
        private readonly ScrollableControl owner;
        private bool updating;
        private bool dragging;
        private int grab;
        private Rectangle thumb;
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr handle, int bar, bool show);

        public SoftScrollChrome(ScrollableControl control)
        {
            owner = control;
            owner.HandleCreated += (s, e) => { AssignHandle(owner.Handle); HideBars(); };
            owner.HandleDestroyed += (s, e) => ReleaseHandle();
            owner.Layout += (s, e) => HideBars();
            owner.Scroll += (s, e) => { HideBars(); InvalidateTrack(); };
            owner.Paint += Paint;
            owner.MouseDown += MouseDown;
            owner.MouseMove += MouseMove;
            owner.MouseUp += (s, e) => { dragging = false; owner.Capture = false; };
        }
        private int Maximum => Math.Max(0, owner.DisplayRectangle.Height - owner.ClientSize.Height);
        private Rectangle Track => new Rectangle(Math.Max(0, owner.ClientSize.Width - SoftTheme.Px(10)),
            SoftTheme.Px(4), SoftTheme.Px(6), Math.Max(1, owner.ClientSize.Height - SoftTheme.Px(8)));
        private void InvalidateTrack() => owner.Invalidate(new Rectangle(
            Math.Max(0, owner.ClientSize.Width - SoftTheme.Px(12)), 0,
            SoftTheme.Px(12), owner.ClientSize.Height));
        private void HideBars()
        {
            if (updating || !owner.IsHandleCreated || !owner.AutoScroll) return;
            updating = true;
            try { ShowScrollBar(owner.Handle, 3, false); }
            finally { updating = false; }
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x85 || m.Msg == 5 || m.Msg == 0x47) HideBars();
        }
        private void Paint(object? sender, PaintEventArgs e)
        {
            if (!owner.AutoScroll || Maximum == 0) { thumb = Rectangle.Empty; return; }
            var track = Track;
            int height = Math.Min(track.Height, Math.Max(SoftTheme.Px(36),
                track.Height * owner.ClientSize.Height / Math.Max(1, owner.DisplayRectangle.Height)));
            int top = track.Top + (track.Height - height) * Math.Max(0, -owner.AutoScrollPosition.Y)
                / Math.Max(1, Maximum);
            thumb = new Rectangle(track.Left, top, track.Width, height);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            SoftTheme.FillRounded(e.Graphics, thumb,
                dragging ? SoftTheme.TextSecondary : SoftTheme.BorderStrong, track.Width / 2);
        }
        private void MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || Maximum == 0
                || e.X < Track.Left - SoftTheme.Px(3)) return;
            dragging = true;
            grab = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
            owner.Capture = true;
            MoveThumb(e.Y);
        }
        private void MouseMove(object? sender, MouseEventArgs e)
        {
            if (dragging) MoveThumb(e.Y);
        }
        private void MoveThumb(int y)
        {
            var track = Track;
            int travel = Math.Max(1, track.Height - thumb.Height);
            int offset = Math.Max(0, Math.Min(travel, y - grab - track.Top));
            owner.AutoScrollPosition = new Point(0, offset * Maximum / travel);
            InvalidateTrack();
        }
        public void Dispose() => ReleaseHandle();
    }
}
