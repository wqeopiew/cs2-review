using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CS2Review.Core;

namespace CS2Review.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--parser-fixture") return ImportSelfTests.WriteFixture(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length >= 1 && args[0] == "--self-test-import")
        {
            try { ImportSelfTests.RunAsync(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test-data")).GetAwaiter().GetResult(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        }
        if (args.Length == 4 && args[0] == "--self-test-demo")
        {
            try { ImportSelfTests.RunRealDemoAsync(args[1], args[2], args[3]).GetAwaiter().GetResult(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        if (args.Length >= 1 && args[0] == "--self-test-ui")
        {
            try
            {
                var testWindow = new MainWindow(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "ui-test-data"), true);
                Pump(testWindow.AssertDefaultRatingAsync()); testWindow.AssertOperationStateRecovery(); Pump(testWindow.AssertContextAndHistoryViewsAsync()); Pump(testWindow.AssertCoachingViewsAsync()); testWindow.DisposeServices(); app.Shutdown(); Console.WriteLine("PASS hidden WPF default estimates/model restore/null degradation; success/failure/cancel recovery; context evidence/unknown values; history version dedup/map comparison; local coaching/old-report selection/frozen preview/Qwen defaults"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(); return 1; }
        }
        var smokeIndex = Array.IndexOf(args, "--smoke-test");
        var smoke = smokeIndex >= 0;
        var dataIndex = Array.IndexOf(args, "--smoke-test-data-directory");
        var dataDirectory = smoke ? dataIndex >= 0 && dataIndex + 1 < args.Length ? Path.GetFullPath(args[dataIndex + 1]) : Path.Combine(AppContext.BaseDirectory, "smoke-data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2Review");
        try
        {
            if (smoke)
            {
                var paths = Enumerable.Range(0, args.Length - 1).Where(i => args[i] == "--smoke-test-history-match").Select(i => args[i + 1]).ToList();
                if (paths.Count > 0) Pump(SeedSmokeHistoryAsync(dataDirectory, paths));
            }
            var window = new MainWindow(dataDirectory, smoke);
            if (smoke)
            {
                if (smokeIndex + 1 >= args.Length) throw new ArgumentException("--smoke-test requires an output PNG path");
                var matchIndex = Array.IndexOf(args, "--smoke-test-match");
                if (matchIndex >= 0)
                {
                    if (matchIndex + 1 >= args.Length) throw new ArgumentException("--smoke-test-match requires MatchData JSON");
                    var match = JsonSerializer.Deserialize<MatchData>(File.ReadAllText(args[matchIndex + 1]), DataJson.Options) ?? throw new InvalidDataException("Empty match");
                    ParserImportService.ValidateMatch(match);
                    window.LoadMatch(match);
                }
                var mapIndex=Array.IndexOf(args,"--smoke-test-map-directory");
                if(mapIndex>=0){if(mapIndex+1>=args.Length)throw new ArgumentException("Map smoke directory required");Pump(window.RestoreSmokeMapAsync(args[mapIndex+1]));}
                var ratingIndex=Array.IndexOf(args,"--smoke-test-rating-model");
                if(ratingIndex>=0){if(ratingIndex+1>=args.Length)throw new ArgumentException("Rating smoke model required");Pump(window.RestoreSmokeRatingAsync(args[ratingIndex+1]));}
                Pump(window.AwaitSmokeScoringAsync());
                var sectionIndex = Array.IndexOf(args, "--smoke-test-section");
                if (sectionIndex >= 0 && sectionIndex + 1 < args.Length) window.RenderSmokePage(args[sectionIndex + 1]);
                if (sectionIndex >= 0 && sectionIndex + 1 < args.Length && args[sectionIndex + 1] == "history") Pump(window.PendingHistoryRender);
                var eventIndex = Array.IndexOf(args, "--smoke-test-event");
                if (eventIndex >= 0 && eventIndex + 1 < args.Length) window.SelectSmokeEvent(args[eventIndex + 1]);
                var widthIndex = Array.IndexOf(args, "--smoke-test-width"); var heightIndex = Array.IndexOf(args, "--smoke-test-height");
                var width = widthIndex >= 0 ? int.Parse(args[widthIndex + 1]) : 1600; var height = heightIndex >= 0 ? int.Parse(args[heightIndex + 1]) : 960;
                if (width is < 900 or > 3200 || height is < 600 or > 2400) throw new ArgumentException("Smoke size out of range");
                var visual = (FrameworkElement)window.Content;
                visual.Width = width; visual.Height = height;
                visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
                var scrollIndex = Array.IndexOf(args, "--smoke-test-scroll");
                if (scrollIndex >= 0 && scrollIndex + 1 < args.Length) window.ScrollSmoke(double.Parse(args[scrollIndex + 1], System.Globalization.CultureInfo.InvariantCulture));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var output = Path.GetFullPath(args[smokeIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var stream = File.Create(output); encoder.Save(stream);
                window.DisposeServices(); app.Shutdown(); return 0;
            }
            app.MainWindow = window;
            window.Closed += (_, _) => { window.DisposeServices(); app.Shutdown(); };
            return app.Run(window);
        }
        catch (Exception e)
        {
            if (!smoke) MessageBox.Show("无法启动：" + e.Message, "CS2 复盘", MessageBoxButton.OK, MessageBoxImage.Error);
            Console.Error.WriteLine(e.ToString()); return 1;
        }
    }
    private static void Pump(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static async Task SeedSmokeHistoryAsync(string directory, List<string> paths)
    {
        using var history = new HistoryStore(Path.Combine(directory, "history.db")); await history.InitializeAsync(CancellationToken.None);
        foreach (var path in paths)
        {
            var match = JsonSerializer.Deserialize<MatchData>(await File.ReadAllTextAsync(path), DataJson.Options) ?? throw new InvalidDataException("Empty smoke history match");
            ParserImportService.ValidateMatch(match); await history.SaveMatchAsync(match, CancellationToken.None);
        }
    }
}
