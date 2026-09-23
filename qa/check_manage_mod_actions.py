#!/usr/bin/env python3
"""Run the real ManageMods action methods without a Windows desktop.

Requires Python 3 and a .NET 10 SDK (DOTNET may specify its executable).
Extracts unchanged C# method bodies from production and compiles them against
small model doubles. This checks selection policy, not WinForms events, registry
integration or actual installation. Generated files live in a temporary folder.
"""
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def method(source, signature):
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


source = (ROOT / "GUI/Controls/ManageMods.cs").read_text()
methods = "\n".join(method(source, signature) for signature in (
    "public void QueueAllUpdates()",
    "public void QueueCollection(IEnumerable<GUIMod> mods)",
    "private void ToggleModInstalled(GUIMod mod)",
))
program = r'''
using System;
using System.Collections.Generic;
using System.Linq;

class CkanModule
{
    public string identifier = "Example";
    public int version;
}
class InstalledModule { public CkanModule Module = null!; }
class GUIMod
{
    public string Identifier => LatestCompatibleMod?.identifier ?? "Example";
    public bool HasUpdate = true;
    public bool IsAutodetected;
    public bool Installable = true;
    public bool IsInstalled => InstalledMod != null;
    public InstalledModule? InstalledMod;
    public CkanModule? LatestCompatibleMod;
    public CkanModule? SelectedMod;
    public bool IsInstallable() => Installable;
}
class ModChange { public CkanModule Mod = null!; }
class ModList { public IEnumerable<GUIMod> Modules = Array.Empty<GUIMod>(); }
class ManageMods
{
    public List<ModChange>? currentChangeSet;
    public ModList? MainModList;
    public void Toggle(GUIMod mod) => ToggleModInstalled(mod);
    // PRODUCTION_METHODS
}
class Program
{
    static readonly CkanModule old = new CkanModule { version = 1 };
    static readonly CkanModule latest = new CkanModule { version = 2 };
    static int failures;
    static GUIMod Installed() => new GUIMod {
        InstalledMod = new InstalledModule { Module = old },
        SelectedMod = old, LatestCompatibleMod = latest
    };
    static ManageMods View(GUIMod mod, bool queued = false) => new ManageMods {
        MainModList = new ModList { Modules = new[] { mod } },
        currentChangeSet = queued ? new List<ModChange> { new ModChange { Mod = old } } : null
    };
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { ++failures; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Selected(GUIMod mod, CkanModule? expected)
    {
        if (!ReferenceEquals(mod.SelectedMod, expected))
            throw new Exception($"expected version {expected?.version.ToString() ?? "none"}, got {mod.SelectedMod?.version.ToString() ?? "none"}");
    }
    static int Main()
    {
        Check("Update all selects latest compatible, not removal", () => {
            var mod = Installed(); View(mod).QueueAllUpdates(); Selected(mod, latest);
        });
        Check("Update all is idempotent before change-set refresh", () => {
            var mod = Installed(); var view = View(mod);
            view.QueueAllUpdates(); view.QueueAllUpdates(); Selected(mod, latest);
        });
        Check("Update all preserves queued removal", () => {
            var mod = Installed(); mod.SelectedMod = null;
            View(mod, queued: true).QueueAllUpdates(); Selected(mod, null);
        });
        Check("Update all preserves queued version choice", () => {
            var mod = Installed(); var chosen = new CkanModule { version = 3 };
            mod.SelectedMod = chosen; View(mod, queued: true).QueueAllUpdates(); Selected(mod, chosen);
        });
        Check("Update all skips autodetected mods", () => {
            var mod = Installed(); mod.IsAutodetected = true;
            View(mod).QueueAllUpdates(); Selected(mod, old);
        });
        Check("Update all skips absent compatible version", () => {
            var mod = Installed(); mod.LatestCompatibleMod = null;
            View(mod).QueueAllUpdates(); Selected(mod, old);
        });
        Check("Update all skips mods without updates", () => {
            var mod = Installed(); mod.HasUpdate = false;
            View(mod).QueueAllUpdates(); Selected(mod, old);
        });
        Check("Card cancels queued upgrade without removal", () => {
            var mod = Installed(); mod.SelectedMod = latest;
            View(mod).Toggle(mod); Selected(mod, old);
        });
        Check("Collection duplicate members do not cancel installation", () => {
            var mod = new GUIMod { LatestCompatibleMod = latest };
            View(mod).QueueCollection(new[] { mod, mod }); Selected(mod, latest);
        });
        Check("Collection repeat is idempotent before change-set refresh", () => {
            var mod = new GUIMod { LatestCompatibleMod = latest }; var view = View(mod);
            view.QueueCollection(new[] { mod }); view.QueueCollection(new[] { mod }); Selected(mod, latest);
        });
        Check("Collection preserves an existing version selection", () => {
            var mod = new GUIMod { LatestCompatibleMod = latest, SelectedMod = old };
            View(mod).QueueCollection(new[] { mod }); Selected(mod, old);
        });
        Check("Collection leaves installed members alone", () => {
            var mod = Installed(); View(mod).QueueCollection(new[] { mod }); Selected(mod, old);
        });
        Check("Collection skips unavailable members", () => {
            var mod = new GUIMod { LatestCompatibleMod = latest, Installable = false };
            View(mod).QueueCollection(new[] { mod }); Selected(mod, null);
        });
        Check("Collection skips autodetected members", () => {
            var mod = new GUIMod { LatestCompatibleMod = latest, IsAutodetected = true };
            View(mod).QueueCollection(new[] { mod }); Selected(mod, null);
        });
        Check("Empty model is safe", () => {
            var view = new ManageMods(); view.QueueAllUpdates(); view.QueueCollection(Array.Empty<GUIMod>());
        });
        return failures == 0 ? 0 : 1;
    }
}
'''.replace("// PRODUCTION_METHODS", methods)
with tempfile.TemporaryDirectory(prefix="ckan-actions-") as temp:
    folder = Path(temp)
    (folder / "Actions.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <LangVersion>9</LangVersion><Nullable>enable</Nullable><NoWarn>CS0649</NoWarn>
  </PropertyGroup>
</Project>''')
    (folder / "Program.cs").write_text(program)
    result = subprocess.run([os.environ.get("DOTNET", "dotnet"), "run", "--project", str(folder / "Actions.csproj"), "--verbosity", "quiet"])
    raise SystemExit(result.returncode)
