"""Portable behavioral tests of the actual artwork methods, not a reimplementation.

Run: DOTNET=/path/to/dotnet python3 Tests/DiscoverArtworkRequestRegression_test.py
The production C# region is compiled unchanged against small UI/image doubles.
This exercises queue/cache/cancellation logic on Linux, NOT WinForms rendering.
No NuGet packages or changes to the shared project are required.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / 'GUI/Discover/ModDiscoverView.cs'

HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

class Image : IDisposable
{
    public bool Disposed;
    public object Clone() { if (Disposed) throw new ObjectDisposedException("image"); return new Image(); }
    public void Dispose() { Disposed = true; }
}
class Control
{
    public bool IsDisposed, Disposing;
    public bool Visible = true, IsHandleCreated = true;
    public Rectangle ClientRectangle = new Rectangle(0, 0, 100, 100);
    public Rectangle RectangleToScreen(Rectangle r) => r;
    public int Posts;
    public void BeginInvoke(MethodInvoker action) { Posts++; }
}
delegate void MethodInvoker();
class Mod { public string Identifier = "mod"; public object Module = new object(); }
class ModCard : Control
{
    public Mod Mod = new Mod();
    public Image? Cover;
    public static int WidthFor(int density) => 100;
    public static int CoverHeightFor(int density) => 100;
}
class ModCarouselRow : Control { public List<ModCard> Cards = new List<ModCard>(); }
static class SoftTheme
{
    public static float LayoutDpi => 96f;
    public static int ScaleInt(int value, float dpi) => value;
}
class ModVisualMetadataService
{
    public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
    public readonly TaskCompletionSource<Image> Result = new TaskCompletionSource<Image>();
    public Task<Image> GetCoverAsync(object mod, int width, int height, CancellationToken token)
    { Started.TrySetResult(true); return Result.Task; }
}
class View : Control
{
    const int MaxConcurrentArt = 3;
    readonly List<ModCarouselRow> rows = new List<ModCarouselRow>();
    readonly Queue<ModCard> pendingArt = new Queue<ModCard>();
    readonly HashSet<string> artRequested = new HashSet<string>();
    readonly HashSet<string> artInFlight = new HashSet<string>();
    readonly Dictionary<string, Image> artByMod = new Dictionary<string, Image>();
    ModVisualMetadataService? visuals = new ModVisualMetadataService();
    CancellationTokenSource? artCancellation;
    int activeArtLoads, artGeneration, density = 0;
    bool rebuilding = false;
    // PRODUCTION_REGION
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    static ModCard AddCard(View v)
    { var card = new ModCard(); var row = new ModCarouselRow(); row.Cards.Add(card); v.rows.Add(row); return card; }
    public static async Task Main(string[] args)
    {
        var v = new View();
        switch (args[0])
        {
            case "offscreen":
                var offscreen = AddCard(v);
                offscreen.ClientRectangle = new Rectangle(200, 0, 100, 100);
                v.PumpArtLoads();
                Check(!v.artRequested.Contains("mod") && !v.artInFlight.Contains("mod"), "Offscreen card marked requested");
                offscreen.ClientRectangle = new Rectangle(0, 0, 100, 100);
                v.activeArtLoads = 3;
                v.PumpArtLoads();
                Check(v.pendingArt.Count == 1, "Newly visible card not queued");
                break;
            case "duplicate":
                var first = AddCard(v); var second = AddCard(v);
                v.artByMod["mod"] = new Image(); v.artRequested.Add("mod");
                v.PumpArtLoads();
                Check(first.Cover != null && second.Cover != null, "Duplicate did not receive cache");
                Check(!ReferenceEquals(first.Cover, second.Cover), "Cards must own separate clones");
                break;
            case "hidden":
                var hidden = AddCard(v); hidden.Visible = false; v.activeArtLoads = 3;
                v.PumpArtLoads();
                Check(v.pendingArt.Count == 0 && !v.artInFlight.Contains("mod"), "Hidden/collapsed card queued artwork");
                break;
            case "disposed-queued":
                var queued = new ModCard { IsDisposed = true };
                v.pendingArt.Enqueue(queued); v.artInFlight.Add("mod");
                v.PumpArtLoads();
                Check(!v.artInFlight.Contains("mod"), "Disposed queued card permanently blocks its identifier");
                AddCard(v); v.activeArtLoads = 3; v.PumpArtLoads();
                Check(v.pendingArt.Count == 1, "Live duplicate cannot request art after queued card disposed");
                break;
            case "disposed-loading":
                var loading = AddCard(v); var duplicate = AddCard(v);
                v.activeArtLoads = 1; v.artInFlight.Add("mod");
                var service = v.visuals!;
                var load = v.LoadArtAsync(loading);
                await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                loading.IsDisposed = true;
                var image = new Image(); service.Result.SetResult(image);
                await load;
                v.PumpArtLoads();
                Check(duplicate.Cover != null && !image.Disposed, "Disposing requesting card lost artwork for live duplicate");
                Check(loading.Cover == null, "Disposed card received artwork");
                Check(v.activeArtLoads == 0, "Load slot was not released");
                break;
            case "cancelled-generation":
                var oldCard = AddCard(v);
                v.activeArtLoads = 1; v.artInFlight.Add("mod");
                var oldService = v.visuals!;
                var oldLoad = v.LoadArtAsync(oldCard);
                await oldService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                v.CancelInFlightArt();
                v.artInFlight.Add("mod");
                var staleImage = new Image(); oldService.Result.SetResult(staleImage);
                await oldLoad;
                Check(staleImage.Disposed && oldCard.Cover == null && v.artByMod.Count == 0, "Cancelled generation published or leaked artwork");
                Check(v.artInFlight.Contains("mod") && !v.artRequested.Contains("mod"), "Old completion changed new generation bookkeeping");
                Check(v.activeArtLoads == 0, "Cancelled load slot was not released");
                break;
            case "concurrency":
                for (int i = 0; i < 5; ++i) AddCard(v).Mod.Identifier = "mod" + i;
                AddCard(v).Mod.Identifier = "mod0";
                v.PumpArtLoads();
                Check(v.activeArtLoads == 3 && v.pendingArt.Count == 2 && v.artInFlight.Count == 5,
                      "Concurrency cap or identifier deduplication failed");
                break;
            case "disposed-view":
                var closingCard = AddCard(v); v.activeArtLoads = 1;
                var closingService = v.visuals!;
                var closingLoad = v.LoadArtAsync(closingCard);
                await closingService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                v.CancelInFlightArt(); v.IsDisposed = true;
                var closingImage = new Image(); closingService.Result.SetResult(closingImage);
                await closingLoad;
                Check(closingImage.Disposed && closingCard.Cover == null && v.artByMod.Count == 0,
                      "Closed view published or leaked a completed image");
                Check(v.Posts == 0 && v.activeArtLoads == 0, "Closed view posted work or leaked a slot");
                break;
            case "cancelled-task":
                var cancelledCard = AddCard(v); v.activeArtLoads = 1;
                var cancelledService = v.visuals!;
                var cancelledLoad = v.LoadArtAsync(cancelledCard);
                await cancelledService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                v.CancelInFlightArt(); cancelledService.Result.SetCanceled();
                await cancelledLoad;
                Check(v.activeArtLoads == 0 && v.artInFlight.Count == 0 && v.artRequested.Count == 0,
                      "Cancellation faulted or corrupted request bookkeeping");
                break;
            default: throw new Exception("Unknown case");
        }
        await Task.CompletedTask;
        Console.WriteLine("PASS " + args[0]);
    }
}
'''

class DiscoverArtworkRequestRegression(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='ckan-artwork-regression-')
        cls.addClassCleanup(cls.temp.cleanup)
        root = Path(cls.temp.name)
        cls.dotnet = os.environ.get('DOTNET') or shutil.which('dotnet')
        if not cls.dotnet:
            raise RuntimeError('Set DOTNET to an installed .NET 10 SDK executable')
        source = SOURCE.read_text()
        region = source.split('#region On-demand artwork', 1)[1].split('#endregion', 1)[0]
        (root / 'Program.cs').write_text(HARNESS.replace('// PRODUCTION_REGION', region))
        (root / 'ArtworkRegression.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>'
            '<Nullable>enable</Nullable><LangVersion>9</LangVersion>'
            '</PropertyGroup></Project>')
        build = subprocess.run([cls.dotnet, 'build', str(root / 'ArtworkRegression.csproj'),
                                '--nologo', '-v:q'], capture_output=True, text=True)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.dll = root / 'bin/Debug/net10.0/ArtworkRegression.dll'

    def run_case(self, case):
        result = subprocess.run([self.dotnet, str(self.dll), case],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_offscreen_then_visible_is_not_suppressed(self):
        self.run_case('offscreen')

    def test_duplicates_receive_separate_cached_images(self):
        self.run_case('duplicate')

    def test_disposed_view_discards_late_results_without_posting(self):
        self.run_case('disposed-view')

    def test_cancelled_task_releases_slot(self):
        self.run_case('cancelled-task')

    def test_concurrency_cap_and_duplicate_request_suppression(self):
        self.run_case('concurrency')

    def test_disposed_requesting_card_preserves_art_for_duplicates(self):
        self.run_case('disposed-loading')

    def test_cancelled_generation_disposes_result_without_touching_new_requests(self):
        self.run_case('cancelled-generation')

    def test_disposed_queued_card_releases_identifier(self):
        self.run_case('disposed-queued')

    def test_hidden_collapsed_cards_do_not_request_artwork(self):
        self.run_case('hidden')

if __name__ == '__main__':
    unittest.main(verbosity=2)
