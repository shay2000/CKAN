"""Portable behaviour checks of the exact Main method bodies.

No WinForms is loaded: configuration, DI, and refresh operations are test doubles.
The UI removal is covered separately by test_modern_settings_contract.py.
Set DOTNET to a .NET 10 SDK executable. CKAN_SETTINGS_BASELINE=1 checks git HEAD
instead of the working tree, to demonstrate that the original implementation fails.
"""
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("DOTNET") or shutil.which("dotnet")


def extract_method(source, signature):
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


@unittest.skipUnless(DOTNET, "Set DOTNET to a .NET 10 SDK executable")
class ModernSettingsRuntimeTests(unittest.TestCase):
    def test_setting_mutations_and_refresh_guards(self):
        source = (subprocess.check_output(
            ["git", "show", "HEAD:GUI/Main/Main.cs"], cwd=ROOT, text=True)
            if os.environ.get("CKAN_SETTINGS_BASELINE") else
            (ROOT / "GUI/Main/Main.cs").read_text())
        methods = "\n".join(extract_method(source, signature) for signature in (
            "internal bool GetModernSetting(int index)",
            "internal void SetModernSetting(int index, bool value)",
            "internal void RefreshModernCatalogue()"))
        harness = r'''
using System;
interface IConfiguration { bool? DevBuilds { get; set; } }
class GlobalConfiguration : IConfiguration { public bool? DevBuilds { get; set; } }
class Container
{
    public GlobalConfiguration Config = new GlobalConfiguration();
    public T Resolve<T>() => (T)(object)Config;
}
static class ServiceLocator { public static Container Container = new Container(); }
class Configuration
{
    public bool CheckForUpdatesOnLaunch, SuppressRecommendations;
    public bool ModernVerifyDownloads = true, ModernCacheArtwork = true;
    public int Saves;
    public void Save(object instance) { ++Saves; }
}
class MainHarness
{
    public Configuration configuration = new Configuration();
    public object CurrentInstance = new object();
    public bool Waiting;
    public int RepositoryRefreshes, GridRefreshes;
    public void UpdateRepo() { ++RepositoryRefreshes; }
    public void RefreshModList(bool ignored) { ++GridRefreshes; }
METHODS
}
class Program
{
    static int failures;
    static void Check(bool value, string name)
    {
        Console.WriteLine((value ? "PASS " : "FAIL ") + name);
        if (!value) ++failures;
    }
    static int Main()
    {
        var main = new MainHarness();
        foreach (bool value in new[] { false, true })
        {
            for (int index = 0; index < 3; ++index)
            {
                main.SetModernSetting(index, value);
                Check(main.GetModernSetting(index) == value, "supported setting round trip " + index + "=" + value);
            }
        }
        Check(main.configuration.Saves == 6, "supported settings persist");
        main.SetModernSetting(3, false);
        main.SetModernSetting(4, false);
        Check(main.configuration.ModernVerifyDownloads, "unsupported verification setting cannot be changed");
        Check(main.configuration.ModernCacheArtwork, "unsupported cache setting cannot be changed");
        Check(main.configuration.Saves == 6, "unsupported settings do not persist");
        main.configuration.ModernVerifyDownloads = false;
        main.configuration.ModernCacheArtwork = false;
        main.SetModernSetting(3, true);
        main.SetModernSetting(4, true);
        Check(!main.configuration.ModernVerifyDownloads && !main.configuration.ModernCacheArtwork,
              "legacy stored values are preserved rather than migrated");
        main.RefreshModernCatalogue();
        Check(main.RepositoryRefreshes == 1 && main.GridRefreshes == 0, "refresh fetches repositories");
        main.Waiting = true;
        main.RefreshModernCatalogue();
        main.Waiting = false;
        main.CurrentInstance = null;
        main.RefreshModernCatalogue();
        main.CurrentInstance = new object();
        main.configuration = null;
        main.RefreshModernCatalogue();
        Check(main.RepositoryRefreshes == 1 && main.GridRefreshes == 0, "refresh blocked when busy or missing instance/config");
        main.SetModernSetting(0, true);
        main.SetModernSetting(1, true);
        main.SetModernSetting(3, false);
        main.SetModernSetting(4, false);
        Check(!main.GetModernSetting(0) && main.GetModernSetting(1), "null configuration safe defaults");
        return failures == 0 ? 0 : 1;
    }
}
'''.replace("METHODS", methods)
        with tempfile.TemporaryDirectory(prefix="ckan-modern-settings-") as tmp:
            project = pathlib.Path(tmp)
            (project / "Harness.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<LangVersion>9</LangVersion><Nullable>disable</Nullable>'
                '</PropertyGroup></Project>')
            (project / "Program.cs").write_text(harness)
            result = subprocess.run([DOTNET, "run", "--project", str(project / "Harness.csproj"),
                                     "--verbosity", "quiet"], text=True, capture_output=True, timeout=120)
            print(result.stdout)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
