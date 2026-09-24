#!/usr/bin/env python3
"""Run production changelog URL/merge methods with deterministic model doubles.

No network requests. Uses the real ModChangelogEntry class. Requires .NET 10.
"""
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def method(source, signature):
    start = source.index(signature)
    opening = source.index('{', start)
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


source = (ROOT / 'GUI/Discover/ModChangelogService.cs').read_text()
methods = '\n'.join(method(source, sig) for sig in (
    'private static bool TryGetGithubRepo(',
    'private static string NormalizeVersion(',
    'public async Task<IReadOnlyList<ModChangelogEntry>> GetFullChangelogAsync('))
program = r'''
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CKAN.GUI;
class Resources { public Uri? repository; }
class CkanModule { public Resources? resources; }
class GUIMod { public CkanModule Module = new CkanModule(); }
class IRegistryQuerier { }
class Service
{
    public IReadOnlyList<ModChangelogEntry> Local = Array.Empty<ModChangelogEntry>();
    public IReadOnlyList<ModChangelogEntry> Remote = Array.Empty<ModChangelogEntry>();
    public IReadOnlyList<ModChangelogEntry> GetLocalHistory(IRegistryQuerier registry, GUIMod mod) => Local;
    public Task<IReadOnlyList<ModChangelogEntry>> GetGithubReleasesAsync(CkanModule mod, CancellationToken ct) => Task.FromResult(Remote);
    public bool Parse(string? url, out string owner, out string repo) => TryGetGithubRepo(
        new CkanModule { resources = new Resources { repository = url == null ? null : new Uri(url, UriKind.RelativeOrAbsolute) } }, out owner, out repo);
    // METHODS
}
class Program
{
    static int failures;
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { ++failures; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main()
    {
        foreach (var url in new[] {
            "https://notgithub.com/owner/repo", "https://github.com.example.org/owner/repo",
            "ftp://github.com/owner/repo", "file://github.com/owner/repo",
            "https://github.com/owner/.git", "/owner/repo", "https://github.com/owner", null })
            Check("Reject invalid GitHub URL " + url, () => {
                Expect(!new Service().Parse(url, out _, out _), "URL incorrectly accepted");
            });
        foreach (var url in new[] {
            "https://github.com/owner/repo", "http://github.com/owner/repo.git",
            "https://GITHUB.COM/owner/repo.git/releases", "https://www.github.com/owner/repo" })
            Check("Parse valid GitHub URL " + url, () => {
                Expect(new Service().Parse(url, out var owner, out var repo) && owner == "owner" && repo == "repo", "wrong repo identity");
            });
        Check("matching releases enrich local rows instead of disappearing", () => {
            var localDate = new DateTime(2026, 1, 1);
            var local = new ModChangelogEntry("1.0", localDate, true, true, true, "Mod", "Abstract", "CKAN", "https://example.org");
            var remote = new ModChangelogEntry(" v1.0 ", new DateTime(2026, 1, 2), false, false, false, "Release title", "Fixed crashes", "GitHub", "https://github.com/owner/repo/releases/tag/v1.0");
            var service = new Service { Local = new[] { local }, Remote = new[] { remote } };
            var result = service.GetFullChangelogAsync(new IRegistryQuerier(), new GUIMod(), CancellationToken.None).GetAwaiter().GetResult();
            Expect(result.Count == 1, "duplicate row created");
            Expect(result[0].Notes == "Fixed crashes" && result[0].Title == "Release title", "release notes discarded");
            Expect(result[0].IsInstalled && result[0].IsLatest && result[0].IsSelected, "local badges lost");
            Expect(result[0].Version == "1.0" && result[0].ReleaseDate == localDate, "local identity/date lost");
            Expect(result[0].Source == "GitHub" && result[0].Url == remote.Url, "remote attribution/link missing");
        });
        Check("blank remote notes preserve local fallback and duplicate tags are ignored", () => {
            var local = new ModChangelogEntry("1.0", null, true, true, false, "Local", "Abstract", "CKAN", "https://example.org");
            var remote = new ModChangelogEntry("v1.0", new DateTime(2026, 1, 2), false, false, false, null, "  ", "GitHub", null);
            var duplicate = new ModChangelogEntry("V1.0", null, false, false, false, "Duplicate", "Wrong", "GitHub", null);
            var service = new Service { Local = new[] { local }, Remote = new[] { remote, duplicate } };
            var result = service.GetFullChangelogAsync(new IRegistryQuerier(), new GUIMod(), CancellationToken.None).GetAwaiter().GetResult();
            Expect(result.Count == 1 && result[0].Notes == "Abstract" && result[0].Title == "Local", "fallback lost");
            Expect(result[0].Url == local.Url && result[0].ReleaseDate == remote.ReleaseDate, "fallback URL/remote date lost");
        });
        Check("unmatched remote versions append once after local history", () => {
            var local = new ModChangelogEntry("1.0", null, true, true, false, "Local", "Abstract", "CKAN", null);
            var remote = new ModChangelogEntry("v2.0", null, false, true, false, "New", "New notes", "GitHub", null);
            var service = new Service { Local = new[] { local }, Remote = new[] { remote, remote } };
            var result = service.GetFullChangelogAsync(new IRegistryQuerier(), new GUIMod(), CancellationToken.None).GetAwaiter().GetResult();
            Expect(result.Count == 2 && ReferenceEquals(result[0], local) && ReferenceEquals(result[1], remote), "history ordering/deduplication changed");
        });
        Check("absent remote history retains all local entries", () => {
            var local = new ModChangelogEntry("1.0", null, true, true, false, "Local", "Abstract", "CKAN", null);
            var service = new Service { Local = new[] { local } };
            var result = service.GetFullChangelogAsync(new IRegistryQuerier(), new GUIMod(), CancellationToken.None).GetAwaiter().GetResult();
            Expect(result.Count == 1 && ReferenceEquals(result[0], local), "local history changed");
        });
        return failures == 0 ? 0 : 1;
    }
}
'''.replace('// METHODS', methods)
with tempfile.TemporaryDirectory(prefix='ckan-changelog-service-') as tmp:
    folder = Path(tmp)
    (folder / 'Program.cs').write_text(program)
    (folder / 'ModChangelogEntry.cs').write_text((ROOT / 'GUI/Discover/ModChangelogEntry.cs').read_text())
    (folder / 'Harness.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>9</LangVersion>
<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>''')
    raise SystemExit(subprocess.call([os.environ.get('DOTNET', 'dotnet'), 'run', '--project', str(folder / 'Harness.csproj'), '--verbosity', 'quiet']))
