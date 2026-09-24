#!/usr/bin/env python3
"""Execute verbatim ModCard input methods with portable doubles on .NET 10.

Usage: python3 qa/check_mod_card_keyboard.py --dotnet /path/to/dotnet
No NuGet or Windows required. Covers routing, not native WinForms dispatch,
painting, or hit-test layout; the action rectangle is a fixed test fixture.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def extract(source, signature):
    """Extract a braced declaration or expression-bodied method verbatim."""
    start = source.index(signature)
    opening = source.index('{', start)
    arrow = source.find('=>', start, opening)
    if arrow != -1:
        return source[start:source.index(';', arrow) + 1]
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


HARNESS = r'''
using System;
using System.Drawing;
// STATUS_ENUM
class Control
{
    public int BaseKeys, BaseMouse, BaseDouble, FocusCalls;
    public KeyEventArgs? LastKey;
    protected virtual void OnKeyDown(KeyEventArgs e) { ++BaseKeys; LastKey = e; }
    protected virtual void OnMouseDown(MouseEventArgs e) { ++BaseMouse; }
    protected virtual void OnMouseDoubleClick(MouseEventArgs e) { ++BaseDouble; }
    public void Focus() { ++FocusCalls; }
}
enum Keys { Enter, Space, Escape, A, Tab, Left, Right }
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
class ModCard : Control
{
    public ModCardStatus Status;
    private Rectangle ActionRect => new Rectangle(100, 0, 92, 32);
    public event Action<ModCard>? Activated;
    public event Action<ModCard>? ActionClicked;
    public event Action<ModCard>? ContextRequested;
    public KeyEventArgs Press(Keys key) { var e = new KeyEventArgs(key); OnKeyDown(e); return e; }
    public void Click(MouseButtons button, Point location) { OnMouseDown(new MouseEventArgs(button, location)); }
    public void DoubleClick(MouseButtons button, Point location) { OnMouseDoubleClick(new MouseEventArgs(button, location)); }
    // METHODS
}
class Program
{
    static int passes, failures;
    static void Check(bool condition, string name)
    {
        if (condition) { ++passes; }
        else { ++failures; Console.WriteLine("FAIL " + name); }
    }
    static void Main()
    {
        foreach (var status in Enum.GetValues<ModCardStatus>())
        {
            foreach (var key in Enum.GetValues<Keys>())
            {
                var card = new ModCard { Status = status };
                int activated = 0, action = 0, context = 0;
                card.Activated += sender => { Check(ReferenceEquals(sender, card), "activation sender"); ++activated; };
                card.ActionClicked += _ => ++action;
                card.ContextRequested += _ => ++context;
                var e = card.Press(key);
                bool activationKey = key == Keys.Enter || key == Keys.Space;
                Check(activated == (activationKey ? 1 : 0) && action == 0 && context == 0,
                      $"{status} {key}: details={activated}, destructive/action={action}, context={context}");
                Check(e.Handled == activationKey && e.SuppressKeyPress == activationKey,
                      $"{status} {key}: activation consumed, other keys untouched");
                Check(card.BaseKeys == 1 && ReferenceEquals(card.LastKey, e), $"{status} {key}: base receives event once");
                Check(card.Status == status, $"{status} {key}: status unchanged");
            }
            foreach (var button in Enum.GetValues<MouseButtons>())
            foreach (bool inAction in new[] { false, true })
            {
                var card = new ModCard { Status = status };
                int activated = 0, action = 0, context = 0;
                card.Activated += _ => ++activated;
                card.ActionClicked += _ => ++action;
                card.ContextRequested += _ => ++context;
                var location = new Point(inAction ? 101 : 1, 1);
                bool enabled = status != ModCardStatus.AutoDetected && status != ModCardStatus.Unavailable;
                bool explicitAction = button == MouseButtons.Left && inAction && enabled;
                card.Click(button, location);
                Check(action == (explicitAction ? 1 : 0)
                      && activated == (button == MouseButtons.Left && !explicitAction ? 1 : 0)
                      && context == (button == MouseButtons.Right ? 1 : 0),
                      $"{status} {button} inAction={inAction}: explicit mouse routing unchanged");
                Check(card.BaseMouse == 1 && card.FocusCalls == 1, "mouse base and focus retained");
                activated = action = context = 0;
                card.DoubleClick(button, location);
                // Preserve existing double-click behavior, including disabled statuses.
                Check(action == (button == MouseButtons.Left && !inAction ? 1 : 0)
                      && activated == 0 && context == 0 && card.BaseDouble == 1,
                      $"{status} {button} inAction={inAction}: double-click unchanged");
            }
            // Events are optional: activation must still be consumed without subscribers.
            var bare = new ModCard { Status = status };
            foreach (var key in new[] { Keys.Enter, Keys.Space })
            {
                var e = bare.Press(key);
                Check(e.Handled && e.SuppressKeyPress, $"{status} {key}: no subscribers");
            }
        }
        Console.WriteLine($"{passes} passed, {failures} failed; {Enum.GetValues<ModCardStatus>().Length} statuses");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    source = (ROOT / 'GUI/Discover/ModCard.cs').read_text()
    methods = '\n'.join(extract(source, signature) for signature in [
        'protected override void OnKeyDown(',
        'protected override void OnMouseDown(',
        'protected override void OnMouseDoubleClick(',
        'private bool IsActionEnabled()',
    ])
    program = HARNESS.replace('// STATUS_ENUM', extract(source, 'public enum ModCardStatus'))
    program = program.replace('// METHODS', methods)
    with tempfile.TemporaryDirectory(prefix='ckan-mod-card-keyboard-') as temp:
        path = Path(temp)
        (path / 'Regression.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable><LangVersion>9</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>''')
        (path / 'Program.cs').write_text(program)
        return subprocess.call([args.dotnet, 'run', '--project', str(path / 'Regression.csproj')])


if __name__ == '__main__':
    raise SystemExit(main())
