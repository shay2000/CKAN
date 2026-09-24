#!/usr/bin/env python3
"""Exercise the actual PaneReveal class with deterministic clock/UI doubles.

Requires Python 3 and .NET 10 SDK (set DOTNET to its executable). Compiles the
unchanged production file with only an Environment alias for the clock; no
WinForms desktop or native drawing is exercised. All build files are temporary.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]

DOUBLES = r'''
using System;
namespace PaneTiming
{
    static class Clock { public static int TickCount; }
}
namespace System.Drawing.Drawing2D
{
    enum CompositingQuality { HighQuality }
    enum InterpolationMode { HighQualityBicubic }
}
namespace System.Drawing.Imaging
{
    enum PixelFormat { Format32bppPArgb }
    enum ColorMatrixFlag { Default }
    enum ColorAdjustType { Bitmap }
    class ColorMatrix { public float Matrix33; }
    class ImageAttributes : IDisposable
    {
        public float Alpha;
        public void SetColorMatrix(ColorMatrix matrix, ColorMatrixFlag flag, ColorAdjustType type)
            => Alpha = matrix.Matrix33;
        public void Dispose() { }
    }
}
namespace System.Drawing
{
    enum GraphicsUnit { Pixel }
    class Bitmap : IDisposable
    {
        public int Width, Height;
        public bool Disposed;
        public Bitmap(int width, int height, Imaging.PixelFormat format)
        { Width = width; Height = height; }
        public void Dispose() => Disposed = true;
    }
    class Graphics
    {
        public Drawing2D.CompositingQuality CompositingQuality;
        public Drawing2D.InterpolationMode InterpolationMode;
        public float Alpha;
        public Rectangle Destination;
        public int Draws;
        public void DrawImage(Bitmap bitmap, Rectangle dest, int x, int y, int w, int h,
                              GraphicsUnit unit, Imaging.ImageAttributes attributes)
        { Alpha = attributes.Alpha; Destination = dest; ++Draws; }
    }
}
namespace System.Windows.Forms
{
    class PaintEventArgs : EventArgs
    { public System.Drawing.Graphics Graphics = new System.Drawing.Graphics(); }
    class Control
    {
        public bool IsDisposed => false;
        public bool IsHandleCreated => true;
        public bool Visible = true;
        public int Width => 400;
        public int Height => 300;
        public int DeviceDpi => 96;
        public System.Drawing.Bitmap? Snapshot;
        public event EventHandler<PaintEventArgs>? Paint;
        public void DrawToBitmap(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle rect)
            => Snapshot = bitmap;
        public void Invalidate() { }
        public PaintEventArgs Render()
        { var args = new PaintEventArgs(); Paint?.Invoke(this, args); return args; }
    }
    class Timer : IDisposable
    {
        public static Timer Last = null!;
        public int Interval;
        public bool Enabled;
        public event EventHandler? Tick;
        public Timer() => Last = this;
        public void Start() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Fire() { if (Enabled) Tick?.Invoke(this, EventArgs.Empty); }
        public void Dispose() => Stop();
    }
}
namespace CKAN.GUI
{
    static class SoftTheme { public static int ScaleInt(int n, int dpi) => n; }
}
class Program
{
    static int failures;
    static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { ++failures; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Run(int start, int elapsed, bool paint)
    {
        PaneTiming.Clock.TickCount = start;
        var host = new System.Windows.Forms.Control();
        var content = new System.Windows.Forms.Control();
        using var reveal = new CKAN.GUI.PaneReveal(host, content);
        reveal.Play();
        Assert(!content.Visible, "Play must hide live controls");
        var snapshot = content.Snapshot!;
        PaneTiming.Clock.TickCount = unchecked(start + elapsed);
        if (paint)
        {
            var graphics = host.Render().Graphics;
            float t = Math.Min(1f, elapsed / 200f);
            float eased = 1f - (1f - t) * (1f - t) * (1f - t);
            float expected = 0.3f + 0.7f * eased;
            Assert(graphics.Draws == 1, "snapshot should be drawn once");
            Assert(Math.Abs(graphics.Alpha - expected) < 0.00001f,
                   $"alpha expected {expected}, got {graphics.Alpha}");
            Assert(graphics.Destination.Width >= 394 && graphics.Destination.Width <= 400,
                   "snapshot width must remain within animation bounds");
        }
        else
        {
            System.Windows.Forms.Timer.Last.Fire();
            bool finished = elapsed >= 200;
            Assert(content.Visible == finished,
                   $"at {elapsed}ms content.Visible expected {finished}, got {content.Visible}");
            Assert(System.Windows.Forms.Timer.Last.Enabled != finished, "timer completion state");
            Assert(snapshot.Disposed == finished, "snapshot completion disposal");
            if (finished) Assert(host.Render().Graphics.Draws == 0, "paint handler must detach");
        }
    }
    static int Main()
    {
        foreach (var start in new[] { 1000, int.MaxValue - 99, -100, int.MinValue })
        foreach (var elapsed in new[] { 0, 100, 199, 200, 250 })
        foreach (var paint in new[] { false, true })
            Check($"{(paint ? "PaintFrame" : "Step")} start={start} elapsed={elapsed}",
                  () => Run(start, elapsed, paint));
        Console.WriteLine($"Failures: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
'''


def main():
    dotnet = os.environ.get("DOTNET") or shutil.which("dotnet")
    if not dotnet:
        raise SystemExit(".NET 10 SDK required; set DOTNET to its executable")
    source = (ROOT / "GUI/Controls/PaneReveal.cs").read_text()
    with tempfile.TemporaryDirectory(prefix="ckan-pane-timing-") as directory:
        folder = Path(directory)
        (folder / "PaneReveal.cs").write_text(
            "using Environment = PaneTiming.Clock;\n" + source)
        (folder / "Program.cs").write_text(DOUBLES)
        (folder / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>')
        (folder / "Timing.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>9.0</LangVersion><NoWarn>CA1416</NoWarn>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>''')
        return subprocess.run([dotnet, "run", "--project", str(folder / "Timing.csproj"),
                               "--configuration", "Release"], cwd=folder).returncode


if __name__ == "__main__":
    raise SystemExit(main())
