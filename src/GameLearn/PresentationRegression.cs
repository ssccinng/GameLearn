using System.Text.Json;
using System.Windows.Threading;
using SkiaSharp;

namespace GameLearn;

/// <summary>Runs on the WPF dispatcher without opening windows or generating input on the user's desktop.</summary>
internal static class PresentationRegression
{
    public static async Task RunAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "presentation-results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("Presentation regression: " + name);
        }
        var vm = new MainViewModel(); vm.SelectedSource = null;
        try
        {
            var at = DateTimeOffset.Now.AddMinutes(-5);
            RecognitionResult Result(int number, SKColor color, string sentence)
            {
                using var bitmap = new SKBitmap(320, 180); bitmap.Erase(color);
                var frame = CaptureService.CreateFrame(bitmap, "Presentation test", "fixture") with { Timestamp = at.AddSeconds(number) };
                return new(frame, new[] { new RecognizedLine("A wrecked ship.", 0.99, new(10, 10, 200, 20)), new RecognizedLine(sentence, 0.99, new(10, 90, 250, 20)) }, "fixture", TimeSpan.FromMilliseconds(1));
            }
            async Task Emit(RecognitionResult result, TriggerKind kind = TriggerKind.Automatic)
                => await vm.Scheduler.EnqueueAsync(new(result.Frame, kind, vm.Scheduler.Generation, new FixedProvider(result)));
            async Task Flush() => await Dispatcher.CurrentDispatcher.InvokeAsync(vm.FlushAutomaticPresentation, DispatcherPriority.Background);
            var frameChanges = 0; var notifications = 0; var noteChanges = 0;
            vm.FrameChanged += () => frameChanges++;
            vm.RecallAvailable += _ => notifications++;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Notes)) noteChanges++; };
            var a = Result(0, SKColors.White, "The ancient gate.");
            vm.Present(a); vm.SelectedLine = a.Lines[1]; vm.Learn("ancient");
            vm.Notes = "unfinished note"; vm.Mastered = true;
            await vm.ExplainAsync(); // Missing optional config produces a displayed message without any network request.
            var explanation = vm.AiExplanation;
            var encounterId = vm.SelectedEncounter!.Id;
            var wordItems = vm.Words.ToArray(); var historyItems = vm.History.ToArray();
            noteChanges = 0;
            var owner = new object(); vm.SetPresentationInteraction(owner, true); vm.LookupIsOpen = true;
            vm.Status = "editing"; vm.Scheduler.SetAutomatic(true);
            var b = Result(1, SKColors.LightBlue, "Ancient bridges remain.");
            var c = Result(2, SKColors.LightGreen, "Ancient ruins remain.");
            await Emit(b); await Emit(c);
            Check("Background capture does not replace the displayed screenshot while interacting", vm.Frame!.Id == a.Frame.Id && frameChanges == 1);
            Check("Selected OCR line and word chips stay fixed", ReferenceEquals(vm.SelectedLine, a.Lines[1]) && vm.LineWords.SequenceEqual(a.Lines[1].Words));
            Check("Word list and timeline are not rebuilt during interaction", vm.Words.SequenceEqual(wordItems) && vm.History.SequenceEqual(historyItems));
            Check("Draft, mastery and explanation are preserved without editor rewrites", vm.Notes == "unfinished note" && vm.Mastered && vm.AiExplanation == explanation && noteChanges == 0);
            Check("Automatic statuses and recall toasts do not interrupt interaction", vm.Status == "editing" && notifications == 0);
            Check("Background encounter recording continues independently", vm.Store.History(vm.SelectedWord!.Id).Count == 3);
            vm.SetPresentationInteraction(owner, false); await Flush();
            Check("Closing one interaction does not unlock another active panel", vm.Frame.Id == a.Frame.Id && vm.IsAutomaticRefreshPaused);
            vm.LookupIsOpen = false; await Flush();
            Check("Resume applies only the newest pending frame once", vm.Frame.Id == c.Frame.Id && frameChanges == 2);
            Check("Resume retains draft and selected historical encounter", vm.Notes == "unfinished note" && vm.Mastered && noteChanges == 0 && vm.SelectedEncounter?.Id == encounterId && vm.History.Count == 3 && vm.AiExplanation == explanation);
            vm.SetPresentationInteraction(owner, true);
            var d = Result(3, SKColors.LightPink, "An ancient temple."); await Emit(d);
            var e = Result(4, SKColors.Orange, "An ancient road."); await Emit(e, TriggerKind.Manual);
            Check("Explicit manual recognition can update a protected view", vm.Frame.Id == e.Frame.Id);
            vm.SetPresentationInteraction(owner, false); await Flush();
            Check("Old deferred results do not roll back a manual result", vm.Frame.Id == e.Frame.Id);
            vm.SetPresentationInteraction(owner, true);
            await Emit(Result(5, SKColors.Gold, "An ancient house.")); vm.Scheduler.SetAutomatic(false);
            vm.SetPresentationInteraction(owner, false); await Flush();
            Check("Disabling automatic mode prevents pending result replay", vm.Frame.Id == e.Frame.Id);
            vm.Scheduler.SetAutomatic(true); vm.SetPresentationInteraction(owner, true);
            await Emit(Result(6, SKColors.Silver, "An ancient tower.")); vm.Invalidate();
            vm.SetPresentationInteraction(owner, false); await Flush();
            Check("Source or settings invalidation discards protected pending results", vm.Frame.Id == e.Frame.Id);
            var repeat = e with { Frame = e.Frame with { Id = Guid.NewGuid(), Timestamp = at.AddSeconds(7) } };
            await Emit(repeat, TriggerKind.Manual); vm.SelectedLine = repeat.Lines[1]; var count = frameChanges;
            await Emit(repeat with { Frame = repeat.Frame with { Id = Guid.NewGuid(), Timestamp = at.AddSeconds(8) } });
            Check("Unchanged automatic frames do not reset selection or redraw", frameChanges == count && ReferenceEquals(vm.SelectedLine, repeat.Lines[1]));
            var newest = Result(20, SKColors.Aqua, "An ancient monument."); vm.Present(newest);
            vm.SetPresentationInteraction(owner, true); await Emit(Result(19, SKColors.Coral, "An ancient fountain."));
            vm.SetPresentationInteraction(owner, false); await Flush();
            Check("An older automatic frame cannot overwrite a newer explicit preview", vm.Frame.Id == newest.Frame.Id);
            // Clicking a word must save the scene being displayed, not the newest background frame.
            vm.SetPresentationInteraction(owner, true); vm.SelectedLine = newest.Lines[1];
            await Emit(Result(21, SKColors.Beige, "The ancient castle.")); vm.Learn("monument");
            Check("Lookup records the visible frame even when a newer capture is pending", vm.SelectedEncounter!.ImagePath.EndsWith(newest.Frame.Id + ".png", StringComparison.Ordinal));
        }
        finally { await vm.ShutdownAsync(); }
    }
    private sealed class FixedProvider(RecognitionResult result) : IOcrProvider
    {
        public string Name => "fixture";
        public Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) => Task.FromResult(result with { Frame = frame });
    }
}
