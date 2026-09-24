#!/usr/bin/env python3
"""Run actual catalogue input methods in a portable C# harness (no NuGet).

Usage: python3 Tests/GUIRegression/catalogue_pane_regression.py --dotnet /path/to/dotnet
The methods are extracted verbatim, not reimplemented. WinForms/GDI dependencies
are instrumented doubles: this tests event routing and disposal control flow,
NOT Windows message dispatch, rendering, or native handle counts.
"""
import argparse
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def method(source, signature):
    start = source.index(signature)
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


STUBS = r'''
using System;
using System.Drawing;
using System.Linq;
class Control
{
    protected virtual void OnKeyDown(KeyEventArgs e) { }
    protected virtual void OnMouseDown(MouseEventArgs e) { }
    public void Focus() { }
}
enum Keys { Enter, Space, Escape, A }
class KeyEventArgs
{
    public Keys KeyCode;
    public bool Handled;
    private bool suppress;
    public bool SuppressKeyPress { get => suppress; set { suppress = value; Handled = value; } }
    public KeyEventArgs(Keys key) { KeyCode = key; }
}
enum MouseButtons { Left, Right, Middle }
class MouseEventArgs
{
    public MouseButtons Button;
    public Point Location;
    public MouseEventArgs(MouseButtons button, Point location) { Button = button; Location = location; }
}
enum ModCardStatus { NotInstalled, Installed, QueuedInstall, QueuedUpdate, QueuedRemove, AutoDetected, Unavailable }
class GUIMod { public bool HasUpdate; }
static class SoftTheme
{
    public static Color Accent, OnAccent, SurfaceSunken, Danger, AccentSoft, AccentDeep, DangerSoft, TextMuted;
}
'''

ROW = r'''
class ModCatalogueRow : Control
{
    public bool updateMode;
    public GUIMod Mod = new GUIMod();
    public ModCardStatus Status;
    private Rectangle ActionRect => new Rectangle(100, 0, 92, 32);
    public event Action<ModCatalogueRow>? Activated;
    public event Action<ModCatalogueRow>? ActionClicked;
    public event Action<ModCatalogueRow>? ContextRequested;
    public KeyEventArgs Press(Keys key) { var e = new KeyEventArgs(key); OnKeyDown(e); return e; }
    public void Click(MouseButtons button, Point location) { OnMouseDown(new MouseEventArgs(button, location)); }
    // ACTUAL_ROW_METHODS
}
'''

PANE = r'''
class Bitmap { public int Width = 200, Height = 100; }
enum CompositingQuality { HighQuality }
enum InterpolationMode { HighQualityBicubic }
enum GraphicsUnit { Pixel }
enum ColorMatrixFlag { Default }
enum ColorAdjustType { Bitmap }
class ColorMatrix { public float Matrix33; }
class ImageAttributes : IDisposable
{
    public static int Created, Disposed;
    public static bool ThrowOnSet;
    public bool IsDisposed;
    public ImageAttributes() { ++Created; }
    public void SetColorMatrix(ColorMatrix matrix, ColorMatrixFlag flag, ColorAdjustType type)
    {
        if (ThrowOnSet) { throw new InvalidOperationException("injected matrix failure"); }
    }
    public void Dispose() { if (!IsDisposed) { ++Disposed; IsDisposed = true; } }
    public static void Reset() { Created = Disposed = 0; ThrowOnSet = false; }
}
class Graphics
{
    public CompositingQuality CompositingQuality;
    public InterpolationMode InterpolationMode;
    public bool ThrowOnDraw;
    public int Draws;
    public void DrawImage(Bitmap bitmap, Rectangle dest, int x, int y, int width, int height,
                          GraphicsUnit unit, ImageAttributes attributes)
    {
        if (attributes.IsDisposed) { throw new Exception("attributes disposed before draw"); }
        ++Draws;
        if (ThrowOnDraw) { throw new InvalidOperationException("injected draw failure"); }
    }
}
class PaintEventArgs { public Graphics Graphics = new Graphics(); }
class PaneReveal
{
    private const int DurationMs = 200;
    private const float DesignScale = 0.015f, MinAlpha = 0.3f;
    private int travel = 18;
    private long startedAt = Environment.TickCount;
    public bool running = true;
    public Bitmap? frame = new Bitmap();
    public void Paint(PaintEventArgs e) { PaintFrame(null, e); }
    // ACTUAL_PANE_METHODS
}
'''

PANE_TESTS = r'''
        ImageAttributes.Reset();
        var pane = new PaneReveal();
        var paint = new PaintEventArgs();
        for (int i = 0; i < 100; ++i) { pane.Paint(paint); }
        Check(paint.Graphics.Draws == 100 && ImageAttributes.Created == 100,
              "each animated frame uses attributes while drawing");
        Check(ImageAttributes.Created == ImageAttributes.Disposed,
              $"frame attributes disposed: created={ImageAttributes.Created}, disposed={ImageAttributes.Disposed}");
        foreach (bool matrixFailure in new[] { false, true })
        {
            ImageAttributes.Reset();
            ImageAttributes.ThrowOnSet = matrixFailure;
            paint.Graphics.ThrowOnDraw = !matrixFailure;
            bool threw = false;
            try { pane.Paint(paint); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, $"injected {(matrixFailure ? "matrix" : "draw")} failure reached");
            Check(ImageAttributes.Created == 1 && ImageAttributes.Disposed == 1,
                  $"attributes disposed on {(matrixFailure ? "matrix" : "draw")} failure");
        }
        ImageAttributes.Reset();
        pane.running = false;
        pane.Paint(paint);
        pane.running = true;
        pane.frame = null;
        pane.Paint(paint);
        Check(ImageAttributes.Created == 0, "inactive/missing frame allocates no attributes");
'''

TESTS = r'''
class Program
{
    static int failures, passes;
    static void Check(bool condition, string name)
    {
        if (condition) { ++passes; }
        else { ++failures; Console.WriteLine("FAIL " + name); }
    }
    static void Main()
    {
        foreach (var status in Enum.GetValues<ModCardStatus>())
        foreach (bool update in new[] { false, true })
        foreach (var key in new[] { Keys.Enter, Keys.Space })
        {
            var row = new ModCatalogueRow { Status = status, updateMode = update };
            row.Mod.HasUpdate = update;
            int activated = 0, action = 0;
            row.Activated += _ => ++activated;
            row.ActionClicked += _ => ++action;
            var e = row.Press(key);
            Check(activated == 1 && action == 0,
                  $"{status} update={update} {key}: details={activated}, destructive/action={action}");
            Check(e.Handled && e.SuppressKeyPress, $"{status} {key}: consumes activation key");
        }
        foreach (var key in new[] { Keys.Escape, Keys.A })
        {
            var row = new ModCatalogueRow();
            int events = 0;
            row.Activated += _ => ++events;
            row.ActionClicked += _ => ++events;
            var e = row.Press(key);
            Check(events == 0 && !e.Handled && !e.SuppressKeyPress, $"{key}: untouched");
        }
        foreach (var status in Enum.GetValues<ModCardStatus>())
        {
            var row = new ModCatalogueRow { Status = status };
            int activated = 0, action = 0, context = 0;
            row.Activated += _ => ++activated;
            row.ActionClicked += _ => ++action;
            row.ContextRequested += _ => ++context;
            row.Click(MouseButtons.Left, new Point(1, 1));
            Check(activated == 1 && action == 0, $"{status}: row body opens details");
            row.Click(MouseButtons.Left, new Point(101, 1));
            bool enabled = status != ModCardStatus.AutoDetected && status != ModCardStatus.Unavailable;
            Check(action == (enabled ? 1 : 0) && activated == (enabled ? 1 : 2),
                  $"{status}: explicit action respects enabled status");
            row.Click(MouseButtons.Right, new Point(101, 1));
            Check(context == 1, $"{status}: context request");
        }
        // PANE_TESTS
        Console.WriteLine($"{passes} passed, {failures} failed");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    row_source = (ROOT / 'GUI/Discover/ModCatalogueRow.cs').read_text()
    methods = '\n'.join(method(row_source, s) for s in [
        'protected override void OnKeyDown(',
        'protected override void OnMouseDown(',
        'private (string Label, Color Back, Color Fore, bool Enabled) ActionAppearance('])
    resources = sorted(set(re.findall(r'Properties.Resources.(\w+)', methods)))
    resource_stubs = 'static class Properties { public static class Resources {\n' + '\n'.join(
        f'public static string {name} => "{name}";' for name in resources) + '\n} }'
    pane_source = (ROOT / 'GUI/Controls/PaneReveal.cs').read_text()
    pane_methods = method(pane_source, 'private void PaintFrame(')
    if 'private static ImageAttributes AlphaMatrix(' in pane_source:
        pane_methods += '\n' + method(pane_source, 'private static ImageAttributes AlphaMatrix(')
    source = (STUBS + resource_stubs + ROW.replace('// ACTUAL_ROW_METHODS', methods)
              + PANE.replace('// ACTUAL_PANE_METHODS', pane_methods)
              + TESTS.replace('// PANE_TESTS', PANE_TESTS))
    with tempfile.TemporaryDirectory(prefix='ckan-catalogue-pane-') as temp:
        path = Path(temp)
        (path / 'Regression.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable><LangVersion>9</LangVersion><NoWarn>0649</NoWarn>
  </PropertyGroup>
</Project>''')
        (path / 'Program.cs').write_text(source)
        return subprocess.call([args.dotnet, 'run', '--project', str(path / 'Regression.csproj')])


if __name__ == '__main__':
    raise SystemExit(main())
