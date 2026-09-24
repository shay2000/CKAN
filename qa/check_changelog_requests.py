#!/usr/bin/env python3
"""Execute real changelog async methods with controlled Tasks and UI doubles.

Set DOTNET to a .NET 10 SDK executable. No network/native WinForms is exercised.
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


source = (ROOT / 'GUI/Discover/Changelog.cs').read_text()
methods = '\n'.join(method(source, sig) for sig in (
    'private async void FetchRemote()', 'private void CancelFetch()'))
program = r'''
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
class GUIMod { }
class IRegistryQuerier { }
class ModChangelogEntry { }
class Widget { public bool Enabled = true; public string Text = "Fetch"; }
static class Properties
{
    public static class Resources
    {
        public const string ChangelogFetching = "Fetching";
        public const string ChangelogFetchButton = "Fetch";
        public const string ChangelogFetchFailed = "Failed";
    }
}
class Service
{
    public List<TaskCompletionSource<IReadOnlyList<ModChangelogEntry>>> Requests = new();
    public List<CancellationToken> Tokens = new();
    public Task<IReadOnlyList<ModChangelogEntry>> GetFullChangelogAsync(IRegistryQuerier registry, GUIMod mod, CancellationToken token)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ModChangelogEntry>>();
        Requests.Add(completion); Tokens.Add(token); return completion.Task;
    }
}
class Changelog
{
    public Func<IRegistryQuerier?> registryProvider = () => new IRegistryQuerier();
    public GUIMod? selectedModule = new GUIMod();
    public Service? service = new Service();
    public Widget fetchButton = new Widget();
    public Widget sourceNote = new Widget();
    public bool IsDisposed;
    private CancellationTokenSource? fetchCancellation;
    public int Renders;
    private void SetEntries(IReadOnlyList<ModChangelogEntry> entries, bool remote) { ++Renders; }
    public void Fetch() => FetchRemote();
    public void Cancel() => CancelFetch();
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
    static void Expect(bool condition, string name) { if (!condition) throw new Exception(name); }
    static IReadOnlyList<ModChangelogEntry> Entries = Array.Empty<ModChangelogEntry>();
    static int Main()
    {
        Check("old success cannot reset active newer request", () => {
            var view = new Changelog(); var service = view.service!;
            view.Fetch(); view.Fetch();
            service.Requests[0].SetResult(Entries);
            Expect(!view.fetchButton.Enabled, "old finally re-enabled fetch button");
            Expect(view.fetchButton.Text == "Fetching", "old finally changed fetching label");
            Expect(view.Renders == 0, "cancelled response rendered");
            service.Requests[1].SetResult(Entries);
            Expect(view.Renders == 1 && view.fetchButton.Enabled, "current response should render and enable");
        });
        Check("cancel restores button without waiting for old network task", () => {
            var view = new Changelog(); var service = view.service!;
            view.Fetch(); view.Cancel();
            Expect(service.Tokens[0].IsCancellationRequested, "old work not cancelled");
            Expect(view.fetchButton.Enabled && view.fetchButton.Text == "Fetch", "button stuck waiting");
            service.Requests[0].SetResult(Entries);
            Expect(view.Renders == 0, "cancelled result rendered");
        });
        Check("old errors cannot overwrite current status", () => {
            var view = new Changelog(); var service = view.service!;
            view.Fetch(); view.Fetch(); service.Requests[0].SetException(new Exception("old failure"));
            Expect(view.sourceNote.Text != "Failed" && !view.fetchButton.Enabled, "old error affected current view");
            service.Requests[1].SetResult(Entries);
        });
        Check("current error reports failure and restores button", () => {
            var view = new Changelog(); view.Fetch();
            view.service!.Requests[0].SetException(new Exception("current failure"));
            Expect(view.sourceNote.Text == "Failed" && view.fetchButton.Enabled, "missing current failure");
        });
        Check("disposed view ignores late success", () => {
            var view = new Changelog(); view.Fetch(); view.IsDisposed = true; view.Cancel();
            view.service!.Requests[0].SetResult(Entries);
            Expect(view.Renders == 0, "render after disposal");
            Expect(!view.fetchButton.Enabled, "button written after disposal");
        });
        Check("disposed view ignores late errors", () => {
            var view = new Changelog(); view.Fetch(); view.IsDisposed = true; view.Cancel();
            view.service!.Requests[0].SetException(new Exception("late failure"));
            Expect(view.sourceNote.Text != "Failed", "error written after disposal");
        });
        Check("old cancellation cannot enable newer fetch", () => {
            var view = new Changelog(); var service = view.service!;
            view.Fetch(); view.Fetch(); service.Requests[0].SetCanceled();
            Expect(!view.fetchButton.Enabled, "old cancellation enabled new fetch");
            service.Requests[1].SetResult(Entries);
        });
        Check("current cancellation restores controls", () => {
            var view = new Changelog(); view.Fetch(); view.service!.Requests[0].SetCanceled();
            Expect(view.fetchButton.Enabled && view.sourceNote.Text != "Failed", "cancellation state incorrect");
        });
        return failures == 0 ? 0 : 1;
    }
}
'''.replace('// METHODS', methods)
with tempfile.TemporaryDirectory(prefix='ckan-changelog-requests-') as tmp:
    folder = Path(tmp)
    (folder / 'Program.cs').write_text(program)
    (folder / 'Harness.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>9</LangVersion>
<Nullable>enable</Nullable><NoWarn>CS0649</NoWarn></PropertyGroup></Project>''')
    raise SystemExit(subprocess.call([os.environ.get('DOTNET', 'dotnet'), 'run', '--project', str(folder / 'Harness.csproj'), '--verbosity', 'quiet']))
