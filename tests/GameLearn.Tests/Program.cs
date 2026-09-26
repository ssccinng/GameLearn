using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using GameLearn;
using Microsoft.Data.Sqlite;
using SkiaSharp;

var results = new List<object>();
var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true }); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); results.Add(new { name, passed = false, error = e.ToString() }); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
CapturedFrame Frame(string sentence = "a", DateTimeOffset? at = null, string game = "Test game") => new(Guid.NewGuid(), at ?? DateTimeOffset.UtcNow, game, "fixture", new byte[] { 1, 2 }, new byte[] { 3 }, 800, 600, new(100, 200, 300, 200), sentence);
RecognitionResult Result(CapturedFrame frame, string sentence = "The wrecked ship.") => new(frame, new[] { new RecognizedLine(sentence, 0.99, new(100, 200, 150, 20)) }, "fake", TimeSpan.FromMilliseconds(1));

await Test("Automatic recovery: backoff, rate limits and permanent/uncertain failures", () =>
{
    Assert(AutomaticRecovery.Delay(new InvalidOperationException("black frame"), 1, false) == TimeSpan.FromSeconds(2), "capture not retried");
    Assert(AutomaticRecovery.Delay(new TimeoutException(), 20, false) == TimeSpan.FromSeconds(30), "unbounded backoff");
    Assert(AutomaticRecovery.Delay(new OcrRateLimitException(TimeSpan.FromSeconds(75)), 1, true) == TimeSpan.FromSeconds(75), "retry-after ignored");
    Assert(AutomaticRecovery.Delay(new OcrAuthenticationException(), 1, true) is null, "auth loop");
    Assert(AutomaticRecovery.Delay(new TimeoutException(), 1, true) is null, "ambiguous cloud submission repeated");
    Assert(AutomaticRecovery.Delay(new FileNotFoundException(), 1, false) is null, "missing model loop");
    return Task.CompletedTask;
});
await Test("Recent OCR: consecutive dedup, retention, persistence, no unqueried vocabulary", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "GameLearn-recent-" + Guid.NewGuid());
    using (var store = new LearningStore(directory))
    {
        store.SaveRecentRecognition(Result(Frame(), "The wrecked ship."));
        store.SaveRecentRecognition(Result(Frame(), "The wrecked ship."));
        Assert(store.RecentRecognitions().Count == 1 && store.Words().Count == 0, "duplicate or unsolicited saved word");
        for (var i = 0; i < 205; i++) store.SaveRecentRecognition(Result(Frame(), "Different sentence " + i));
        Assert(store.RecentRecognitions().Count == 200 && store.RecentRecognitions()[0].Text == "Different sentence 204", "retention/order incorrect");
    }
    using (var store = new LearningStore(directory))
    {
        Assert(store.RecentRecognitions("204").Count == 1, "history lost on restart or search incorrect");
        store.Clear(); Assert(store.RecentRecognitions().Count == 0, "clear records omitted recent history");
    }
    return Task.CompletedTask;
});
await Test("Projector classification and black-bar coordinates at mixed DPI", () =>
{
    Assert(new WindowSource(1, "Fullscreen Projector (Preview)", "obs64").IsObsProjector, "English projector missing");
    Assert(new WindowSource(1, "全屏投影（场景）", "obs64").IsObsProjector, "Chinese projector missing");
    Assert(!new WindowSource(1, "OBS Studio", "obs64").IsObsProjector, "OBS interface classified as projector");
    Assert(CaptureGeometry.Map(new(200, 800, 600, 40), 1920, 1080, 1536, 864) == new PixelRect(160, 640, 480, 32), "DPI/letterbox mapping incorrect");
    return Task.CompletedTask;
});
await Test("Sentence layout: fragments and wrapped dialogue merge with screenshot bounds", () =>
{
    var lines = SentenceAssembler.Merge(new[] {
        new RecognizedLine("The wrecked", .99, new(40, 100, 120, 20)),
        new RecognizedLine("ship needs", .97, new(170, 100, 130, 20)),
        new RecognizedLine("a little elbow grease.", .98, new(40, 126, 240, 20)) });
    Assert(lines.Count == 1 && lines[0].Text == "The wrecked ship needs a little elbow grease.", "wrapped sentence remained split");
    Assert(lines[0].Fragments is { Count: 3 } fragments && fragments[1].Bounds.Y == 100, "merged sentence lost original word regions");
    Assert(lines[0].Bounds == new PixelRect(40, 100, 260, 46) && lines[0].Confidence == .97, "scene bounds/confidence lost");
    Assert(SentenceAssembler.Merge(lines).SequenceEqual(lines), "merging changed an already assembled sentence");
    return Task.CompletedTask;
});
await Test("Sentence layout: speaker, menu, columns and completed sentences stay separate", () =>
{
    foreach (var pair in new[] {
        new[] { new RecognizedLine("Valdi", .99, new(40, 100, 50, 20)), new RecognizedLine("The wrecked ship", .99, new(40, 125, 240, 20)) },
        new[] { new RecognizedLine("Load game", .99, new(40, 100, 180, 20)), new RecognizedLine("New game", .99, new(40, 125, 180, 20)) },
        new[] { new RecognizedLine("The wrecked ship", .99, new(40, 100, 240, 20)), new RecognizedLine("Inventory", .99, new(500, 125, 200, 20)) },
        new[] { new RecognizedLine("We are ready.", .99, new(40, 100, 240, 20)), new RecognizedLine("Let us go.", .99, new(40, 125, 240, 20)) } })
        Assert(SentenceAssembler.Merge(pair).Count == 2, "unrelated UI text was joined");
    return Task.CompletedTask;
});
await Test("Sentence display: punctuation and repeated words survive", () =>
{
    using var dictionary = new OfflineDictionary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite"));
    var line = LearningPriority.Score(new("You, you can do it!", .99, new(0, 0, 200, 20)), dictionary, new Dictionary<string, SavedWord>());
    Assert(string.Join(" ", line.DisplayWords) == "You, you can do it!", "display removed punctuation or repeated words");
    return Task.CompletedTask;
});
await Test("Cloud: multipart PP-OCRv6, pending/done, signed JSONL without token", async () =>
{
    var calls = new List<string>();
    var handler = new FakeHttp(async (request, ct) =>
    {
        calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
        if (request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert(body.Contains("PP-OCRv6") && body.Contains("game-frame.png"), "wrong model or file upload");
            Assert(request.Headers.Authorization?.Parameter == "test-token", "missing bearer");
            return FakeHttp.Json("{\"code\":0,\"data\":{\"jobId\":\"job1\"}}");
        }
        if (request.RequestUri!.Host == "result.example")
        {
            Assert(request.Headers.Authorization is null, "token leaked to signed result host");
            return FakeHttp.Json("{\"result\":{\"ocrResults\":[{\"prunedResult\":{\"rec_texts\":[\"wrecked ship\"],\"rec_scores\":[0.99],\"rec_polys\":[[[10,20],[110,20],[110,40],[10,40]]]}}]}}\n");
        }
        return calls.Count < 3 ? FakeHttp.Json("{\"data\":{\"state\":\"running\"}}") : FakeHttp.Json("{\"data\":{\"state\":\"done\",\"resultUrl\":{\"jsonUrl\":\"https://result.example/result.jsonl\"}}}");
    });
    using var client = new HttpClient(handler);
    var provider = new PaddleCloudOcrProvider(client, "test-token", TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromMilliseconds(1));
    var result = await provider.RecognizeAsync(Frame(), CancellationToken.None);
    Assert(result.Lines.Single().Bounds == new PixelRect(110, 220, 100, 20), "crop coordinates not restored");
    Assert(calls.Count == 4, "unexpected submission or poll count");
});
await Test("Cloud: rec_boxes fallback and empty image", () =>
{
    var lines = PaddleCloudOcrProvider.ParseResult("{\"result\":{\"ocrResults\":[{\"prunedResult\":{\"rec_texts\":[\"hello\"],\"rec_scores\":[0.98],\"rec_boxes\":[[1,2,101,22]]}}]}}", new(10, 20, 200, 100));
    Assert(lines[0].Bounds == new PixelRect(11, 22, 100, 20), "wrong box");
    var empty = PaddleCloudOcrProvider.ParseResult("{\"result\":{\"ocrResults\":[{\"prunedResult\":{\"rec_texts\":[],\"rec_scores\":[]}}]}}", new(0, 0, 10, 10));
    Assert(empty.Count == 0, "empty OCR unsupported"); return Task.CompletedTask;
});
await Test("Cloud: 401 and 429 classify errors, no resubmission", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHttp((_, _) => { calls++; var r = new HttpResponseMessage(status); r.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(17)); return Task.FromResult(r); }));
        try { await new PaddleCloudOcrProvider(client, "token", TimeSpan.FromSeconds(2)).RecognizeAsync(Frame(), CancellationToken.None); throw new Exception("Expected error"); }
        catch (OcrAuthenticationException) when (status == HttpStatusCode.Unauthorized) { }
        catch (OcrRateLimitException e) when (status == HttpStatusCode.TooManyRequests) { Assert(e.RetryAfter.TotalSeconds == 17, "retry time lost"); }
        Assert(calls == 1, "submission retried");
    }
});
await Test("Cloud: total timeout and explicit cancellation", async () =>
{
    using var client = new HttpClient(new FakeHttp(async (_, ct) => { await Task.Delay(10000, ct); return FakeHttp.Json("{}"); }));
    var provider = new PaddleCloudOcrProvider(client, "token", TimeSpan.FromMilliseconds(30));
    try { await provider.RecognizeAsync(Frame(), CancellationToken.None); throw new Exception("Expected timeout"); } catch (TimeoutException) { }
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await provider.RecognizeAsync(Frame(), cts.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
});
await Test("Cloud: failed and malformed task results", async () =>
{
    foreach (var payload in new[] { "{\"data\":{\"state\":\"failed\"}}", "{\"data\":{\"state\":\"strange\"}}" })
    {
        using var client = new HttpClient(new FakeHttp((request, _) => Task.FromResult(FakeHttp.Json(request.Method == HttpMethod.Post ? "{\"data\":{\"jobId\":\"a\"}}" : payload))));
        var error = false; try { await new PaddleCloudOcrProvider(client, "token", TimeSpan.FromSeconds(1)).RecognizeAsync(Frame(), CancellationToken.None); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { error = true; }
        Assert(error, "bad state accepted");
    }
});
await Test("Scheduler: automatic off performs zero OCR; manual still works", async () =>
{
    using var scheduler = new RecognitionScheduler(); var fake = new FakeOcr((frame, _) => Task.FromResult(Result(frame))); var complete = 0;
    scheduler.Completed += (_, _) => complete++;
    await scheduler.EnqueueAsync(new(Frame(), TriggerKind.Automatic, scheduler.Generation, fake));
    Assert(fake.Calls == 0 && complete == 0, "automatic work while disabled");
    await scheduler.EnqueueAsync(new(Frame(), TriggerKind.Manual, scheduler.Generation, fake));
    Assert(fake.Calls == 1 && complete == 1, "manual disabled with automatic");
});
await Test("Scheduler: disable automatic discards even cancellation-ignoring results", async () =>
{
    using var scheduler = new RecognitionScheduler(); scheduler.SetAutomatic(true);
    var gate = new TaskCompletionSource<RecognitionResult>(); var fake = new FakeOcr((_, _) => gate.Task); var complete = 0;
    scheduler.Completed += (_, _) => complete++;
    var frame = Frame(); var run = scheduler.EnqueueAsync(new(frame, TriggerKind.Automatic, scheduler.Generation, fake));
    scheduler.SetAutomatic(false); gate.SetResult(Result(frame)); await run;
    Assert(complete == 0 && !scheduler.IsBusy, "stale result emitted");
});
await Test("Scheduler: newest manual request replaces pending; no concurrency", async () =>
{
    using var scheduler = new RecognitionScheduler(); var gate = new TaskCompletionSource<RecognitionResult>(); var frames = new List<Guid>();
    var fake = new FakeOcr((frame, _) => { frames.Add(frame.Id); return frames.Count == 1 ? gate.Task : Task.FromResult(Result(frame)); });
    var a = Frame("a"); var b = Frame("b"); var c = Frame("c");
    var first = scheduler.EnqueueAsync(new(a, TriggerKind.Manual, scheduler.Generation, fake));
    await scheduler.EnqueueAsync(new(b, TriggerKind.Manual, scheduler.Generation, fake));
    await scheduler.EnqueueAsync(new(c, TriggerKind.Manual, scheduler.Generation, fake));
    gate.SetResult(Result(a)); await first;
    Assert(frames.SequenceEqual(new[] { a.Id, c.Id }), "pending queue not coalesced");
});
await Test("Scheduler: source switch invalidates in-flight and pending frames", async () =>
{
    using var scheduler = new RecognitionScheduler(); var gate = new TaskCompletionSource<RecognitionResult>(); var fake = new FakeOcr((_, _) => gate.Task); var complete = 0;
    scheduler.Completed += (_, _) => complete++;
    var frame = Frame(); var first = scheduler.EnqueueAsync(new(frame, TriggerKind.Manual, scheduler.Generation, fake));
    await scheduler.EnqueueAsync(new(Frame(), TriggerKind.Manual, scheduler.Generation, fake)); scheduler.Invalidate(); gate.SetResult(Result(frame)); await first;
    Assert(complete == 0 && fake.Calls == 1, "old source wrote a result");
});
await Test("Scheduler: unchanged auto frame skips inference but updates observation time", async () =>
{
    using var scheduler = new RecognitionScheduler(); scheduler.SetAutomatic(true); var fake = new FakeOcr((frame, _) => Task.FromResult(Result(frame))); var ids = new List<Guid>();
    scheduler.Completed += (_, result) => ids.Add(result.Frame.Id);
    var a = Frame("same"); var b = Frame("same");
    await scheduler.EnqueueAsync(new(a, TriggerKind.Automatic, scheduler.Generation, fake));
    await scheduler.EnqueueAsync(new(b, TriggerKind.Automatic, scheduler.Generation, fake));
    Assert(fake.Calls == 1 && ids.SequenceEqual(new[] { a.Id, b.Id }), "unchanged frame handling wrong");
});
await Test("Capture: normalized crop preserves full snapshot and image dimensions", () =>
{
    using var bitmap = new SKBitmap(800, 600); bitmap.Erase(SKColors.White);
    var frame = CaptureService.CreateFrame(bitmap, "test", "window", new(0.25, 0.5, 0.5, 0.25));
    using var cropped = SKBitmap.Decode(frame.OcrPng); using var full = SKBitmap.Decode(frame.FullPng);
    Assert(frame.Region == new PixelRect(200, 300, 400, 150) && cropped.Width == 400 && cropped.Height == 150 && full.Width == 800, "crop mismatch"); return Task.CompletedTask;
});
await Test("Storage/tracker: morphology, persistence, dedup, prior scene, mastery, cleanup", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GameLearn-tests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    var dictPath = Path.Combine(dir, "dictionary.sqlite");
    using (var fixture = new SqliteConnection("Data Source=" + dictPath))
    {
        fixture.Open(); using var cmd = fixture.CreateCommand(); cmd.CommandText = "CREATE TABLE entries(word TEXT PRIMARY KEY,phonetic TEXT,translation TEXT,lemma TEXT); CREATE TABLE forms(form TEXT PRIMARY KEY,lemma TEXT); INSERT INTO entries VALUES('wreck','rek','毁坏',''),('wrecked','','毁坏的','wreck');"; cmd.ExecuteNonQuery();
    }
    using var dictionary = new OfflineDictionary(dictPath);
    var bearing = dictionary.Lookup("wrecked");
    Assert(bearing.Word == "wreck" && bearing.Observed == "wrecked" && bearing.ObservedTranslation == "毁坏的", "word form definition was not preserved");
    Assert(dictionary.Lookup("Wrecked").Word == "wreck", "lemma failed");
    var at = DateTimeOffset.UtcNow;
    using (var store = new LearningStore(dir))
    {
        var tracker = new EncounterTracker(store, dictionary);
        var original = Frame(at: at); var line = Result(original).Lines[0]; tracker.Learn("wrecked", line, original);
        for (var i = 1; i <= 60; i++) tracker.Observe(Result(Frame(at: at.AddSeconds(i))), TriggerKind.Automatic);
        var saved = store.Words().Single(); Assert(saved.Encounters == 1, "static dialogue inflated history");
        var changed = Frame(at: at.AddSeconds(61)); var recalls = tracker.Observe(Result(changed, "Another wrecked boat."), TriggerKind.Automatic);
        Assert(recalls.Count == 1 && recalls[0].Previous.Sentence == line.Text && recalls[0].Current.Sentence != line.Text, "previous scene lost");
        tracker.Observe(Result(Frame(at: at.AddSeconds(62)), "Another wrecked boat."), TriggerKind.Manual);
        Assert(store.Words().Single().Encounters == 2, "manual dedup failed");
        store.UpdateWord(saved.Id, true, "my note");
        Assert(tracker.Observe(Result(Frame(at: at.AddMinutes(10)), "A wrecked car."), TriggerKind.Automatic).Count == 0, "mastered word notified");
    }
    using (var store = new LearningStore(dir))
    {
        var word = store.Words().Single(); Assert(word.Mastered && word.Notes == "my note" && word.Encounters == 3, "restart lost data");
        Assert(store.History(word.Id).All(e => File.Exists(e.ImagePath)), "missing screenshot");
        store.DeleteWord(word.Id); Assert(store.Words().Count == 0 && Directory.GetFiles(Path.Combine(dir, "scenes")).Length == 0, "orphan scenes retained");
    }
    return Task.CompletedTask;
});
await Test("DPAPI secrets round-trip and hotkey validation", () =>
{
    var encrypted = AppSettings.Protect("not-a-real-secret"); Assert(encrypted != "not-a-real-secret" && AppSettings.Unprotect(encrypted) == "not-a-real-secret", "secret protection failed");
    Assert(HotkeyManager.Parse("Ctrl+Alt+E").Key == 69, "shortcut parse failed");
    var rejected = false; try { HotkeyManager.Parse("E"); } catch (ArgumentException) { rejected = true; } Assert(rejected, "unmodified hotkey allowed"); return Task.CompletedTask;
});

await Test("Scheduler: queued cloud work honors Retry-After; local remains usable", async () =>
{
    using var scheduler = new RecognitionScheduler();
    var gate = new TaskCompletionSource<HttpResponseMessage>(); var calls = 0;
    using var client = new HttpClient(new FakeHttp((_, _) => { calls++; return gate.Task; }));
    var cloud = new PaddleCloudOcrProvider(client, "test", TimeSpan.FromSeconds(5));
    var first = scheduler.EnqueueAsync(new(Frame(), TriggerKind.Manual, scheduler.Generation, cloud));
    await scheduler.EnqueueAsync(new(Frame(), TriggerKind.Manual, scheduler.Generation, cloud));
    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); gate.SetResult(limited); await first;
    Assert(calls == 1, "queued request bypassed Retry-After");
    var local = new FakeOcr((frame, _) => Task.FromResult(Result(frame)));
    await scheduler.EnqueueAsync(new(Frame(), TriggerKind.Manual, scheduler.Generation, local));
    Assert(local.Calls == 1, "cloud rate limit blocked local provider");
});
await Test("Cloud provider works with automatic and manual scheduler triggers", async () =>
{
    foreach (var trigger in new[] { TriggerKind.Automatic, TriggerKind.Manual })
    {
        using var client = new HttpClient(new FakeHttp((request, _) => Task.FromResult(FakeHttp.Json(
            request.Method == HttpMethod.Post ? "{\"data\":{\"jobId\":\"a\"}}" : request.RequestUri!.Host == "result.example"
            ? "{\"result\":{\"ocrResults\":[{\"prunedResult\":{\"rec_texts\":[\"wrecked\"],\"rec_scores\":[0.99],\"rec_boxes\":[[1,2,50,20]]}}]}}"
            : "{\"data\":{\"state\":\"done\",\"resultUrl\":{\"jsonUrl\":\"https://result.example/result\"}}}"))));
        using var scheduler = new RecognitionScheduler(); scheduler.SetAutomatic(trigger == TriggerKind.Automatic);
        RecognitionResult? completed = null; scheduler.Completed += (_, r) => completed = r;
        await scheduler.EnqueueAsync(new(Frame(), trigger, scheduler.Generation, new PaddleCloudOcrProvider(client, "test", TimeSpan.FromSeconds(2))));
        Assert(completed?.Lines.Single().Text == "wrecked", "cloud trigger failed: " + trigger);
    }
});
await Test("Tracker: slow OCR is not disappearance; verified absence creates a new encounter", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GameLearn-presence-" + Guid.NewGuid());
    using var store = new LearningStore(dir); using var dictionary = new OfflineDictionary(Path.Combine(dir, "no-dictionary"));
    var tracker = new EncounterTracker(store, dictionary); var at = DateTimeOffset.UtcNow;
    tracker.Learn("wrecked", Result(Frame()).Lines[0], Frame(at: at));
    tracker.Observe(Result(Frame(at: at.AddSeconds(60))), TriggerKind.Automatic);
    Assert(store.Words().Single().Encounters == 1, "slow response treated as missing word");
    tracker.Observe(new(Frame(at: at.AddSeconds(61)), Array.Empty<RecognizedLine>(), "fake", TimeSpan.Zero), TriggerKind.Automatic);
    tracker.Observe(Result(Frame(at: at.AddSeconds(72))), TriggerKind.Automatic);
    Assert(store.Words().Single().Encounters == 2, "actual reappearance not recorded"); return Task.CompletedTask;
});
await Test("AI explanation: text-only request and context cache key", async () =>
{
    using var client = new HttpClient(new FakeHttp(async (request, ct) =>
    {
        Assert(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "wrong endpoint");
        var body = await request.Content!.ReadAsStringAsync(ct);
        Assert(body.Contains("wrecked") && !body.Contains("image_url") && !body.Contains("base64"), "incorrect context payload");
        return FakeHttp.Json("{\"choices\":[{\"message\":{\"content\":\"wrecked means damaged\"}}]}");
    }));
    var settings = new AppSettings { AiBaseUrl = "https://model.example/v1", AiModel = "example-model", AiSecret = AppSettings.Protect("test") };
    var result = await new AiExplanationService(client).ExplainAsync(settings, "wreck", "A wrecked ship", CancellationToken.None);
    Assert(result.Contains("damaged"), "explanation missing");
    Assert(AiExplanationService.CacheKey(settings, "wreck", "A wrecked ship") != AiExplanationService.CacheKey(settings, "wreck", "A wrecked car"), "different contexts share cache");
});

await Test("Hotkeys: A/R conflicts must not unregister available E", () =>
{
    var native = new FakeHotkeyRegistrar();
    native.Blocked.Add(HotkeyManager.Parse("Ctrl+Alt+A")); native.Blocked.Add(HotkeyManager.Parse("Ctrl+Alt+R"));
    using var bindings = new HotkeyRegistrationSet(native);
    var result = bindings.Apply(new Dictionary<int, string> { [1] = "Ctrl+Alt+E", [2] = "Ctrl+Alt+A", [3] = "Ctrl+Alt+R" });
    Assert(result[0].MatchesRequest && bindings.Active[1] == "Ctrl+Alt+E", "recognition key was rolled back");
    Assert(result[1].Error == 1409 && result[2].Error == 1409 && result[1].Active is null, "conflict hidden");
    return Task.CompletedTask;
});
await Test("Hotkeys: failed edit restores old binding without losing other actions", () =>
{
    var native = new FakeHotkeyRegistrar(); using var bindings = new HotkeyRegistrationSet(native);
    var initial = new Dictionary<int, string> { [1] = "Ctrl+Alt+E", [2] = "Ctrl+Alt+A", [3] = "Ctrl+Alt+R" }; bindings.Apply(initial);
    native.Blocked.Add(HotkeyManager.Parse("Ctrl+Shift+F9"));
    var edited = new Dictionary<int, string>(initial) { [1] = "Ctrl+Shift+F9" };
    var result = bindings.Apply(edited);
    Assert(!result[0].MatchesRequest && result[0].Active == "Ctrl+Alt+E", "previous key not restored");
    Assert(result[1].MatchesRequest && result[2].MatchesRequest, "unrelated shortcuts failed");
    try { bindings.Apply(new Dictionary<int, string>(initial) { [2] = "Ctrl+Alt+E" }); throw new Exception("duplicate accepted"); } catch (ArgumentException) { }
    Assert(bindings.Active.Count == 3, "invalid edit tore down working shortcuts");
    return Task.CompletedTask;
});

await Test("OBS: authenticated fragmented screenshots read the source without window pixels", async () =>
{
    using var image = new SKBitmap(320, 180); image.Erase(SKColors.White);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    var data = "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
    var requests = new List<string>();
    await using var server = new FakeObsServer(async (ws, ct) =>
    {
        await FakeObsServer.SendAsync(ws, new { op = 0, d = new { rpcVersion = 1, authentication = new { salt = "salt", challenge = "challenge" } } }, ct);
        var identify = await FakeObsServer.ReceiveAsync(ws, ct);
        Assert(identify.GetProperty("op").GetInt32() == 1 && identify.GetProperty("d").GetProperty("authentication").GetString() == "zTM5ki6L2vVvBQiTG9ckH1Lh64AbnCf6XZ226UmnkIA=", "OBS authentication vector differs");
        Assert(identify.GetProperty("d").GetProperty("eventSubscriptions").GetInt32() == 0, "unneeded event subscription");
        await FakeObsServer.SendAsync(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } }, ct);
        for (var i = 0; i < 4; i++)
        {
            var request = (await FakeObsServer.ReceiveAsync(ws, ct)).GetProperty("d");
            var type = request.GetProperty("requestType").GetString()!; requests.Add(type);
            object response = type switch
            {
                "GetCurrentProgramScene" => new { currentProgramSceneName = "scene-two" },
                "GetSourceScreenshot" => new { imageData = data },
                _ => throw new Exception("Unexpected request: " + type)
            };
            if (type == "GetSourceScreenshot")
            {
                var sent = request.GetProperty("requestData");
                var expectedName = i == 0 || i == 1 ? "ns" : "scene-two";
                Assert(sent.GetProperty("sourceName").GetString() == expectedName && sent.GetProperty("imageFormat").GetString() == "png", "wrong OBS source selected");
            }
            await FakeObsServer.SendAsync(ws, new { op = 7, d = new { requestId = request.GetProperty("requestId").GetString(), requestStatus = new { result = true, code = 100 }, responseData = response } }, ct, fragmented: true);
        }
    });
    using var obs = new ObsCaptureService(() => new ObsConnectionOptions(server.Port, "password"));
    var source = new WindowSource(0, "ns", "OBS", CaptureSourceKind.ObsInput, "ns", true);
    var first = await obs.CaptureAsync(source, "game", new(0.25, 0.25, 0.5, 0.5), CancellationToken.None);
    Assert(first.Width == 320 && first.Height == 180 && first.Region == new PixelRect(80, 45, 160, 90), "source PNG/crop mismatch");
    var second = await obs.CaptureAsync(source, "game", null, CancellationToken.None);
    Assert(second.Id != first.Id && obs.IsConnected, "connection or frame not reusable");
    var program = await obs.CaptureAsync(new(0, "Current", "OBS", CaptureSourceKind.ObsProgram), "game", null, CancellationToken.None);
    Assert(program.Source == "OBS · scene-two", "current program scene not resolved at capture time");
    await server.Completion;
    Assert(requests.SequenceEqual(new[] { "GetSourceScreenshot", "GetSourceScreenshot", "GetCurrentProgramScene", "GetSourceScreenshot" }), "extra capture or mutation requests");
});
await Test("OBS: visual sources, active capture priority and stable distinct keys", () =>
{
    using var scenes = JsonDocument.Parse("{\"scenes\":[{\"sceneName\":\"switch2\"}]}");
    using var inputs = JsonDocument.Parse("{\"inputs\":[{\"inputName\":\"mic\",\"inputKind\":\"wasapi_input_capture\"},{\"inputName\":\"overlay\",\"inputKind\":\"window_capture\"},{\"inputName\":\"ns\",\"inputKind\":\"dshow_input\"},{\"inputName\":\"hidden\",\"inputKind\":\"game_capture\"}]}");
    var sources = ObsCaptureService.BuildSourceList(scenes.RootElement, inputs.RootElement, new HashSet<string> { "ns", "overlay" });
    Assert(sources.All(s => s.IsObs && s.Title != "mic"), "audio/UI window mixed into visual capture sources");
    Assert(sources.First(s => s.Kind == CaptureSourceKind.ObsInput && s.IsActive).ObsName == "ns", "active device not preferred");
    Assert(sources.Select(s => s.Key).Distinct().Count() == sources.Count, "zero handles collide");
    var refused = false; try { ObsCaptureService.DecodeScreenshot("https://image.example/x"); } catch (InvalidDataException) { refused = true; }
    Assert(refused, "non-image URI accepted"); return Task.CompletedTask;
});
await Test("OBS: incorrect password is explicit and disconnects", async () =>
{
    await using var server = new FakeObsServer(async (ws, ct) =>
    {
        await FakeObsServer.SendAsync(ws, new { op = 0, d = new { authentication = new { salt = "s", challenge = "c" } } }, ct);
        await FakeObsServer.ReceiveAsync(ws, ct);
        await ws.CloseOutputAsync((WebSocketCloseStatus)4009, "Authentication Failed", ct);
    });
    using var obs = new ObsCaptureService(() => new ObsConnectionOptions(server.Port, "bad"));
    var failedAuth = false;
    try { await obs.ListSourcesAsync(CancellationToken.None); } catch (InvalidOperationException e) { failedAuth = e.Message.Contains("密码"); }
    Assert(failedAuth && !obs.IsConnected, "authentication failure not surfaced");
    await server.Completion;
});
await Test("OBS: cancellation aborts in-flight image and discards connection", async () =>
{
    var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new FakeObsServer(async (ws, ct) =>
    {
        await FakeObsServer.SendAsync(ws, new { op = 0, d = new { rpcVersion = 1 } }, ct);
        await FakeObsServer.ReceiveAsync(ws, ct); await FakeObsServer.SendAsync(ws, new { op = 2, d = new { } }, ct);
        await FakeObsServer.ReceiveAsync(ws, ct); requested.SetResult(); await finish.Task.WaitAsync(ct);
    });
    using var obs = new ObsCaptureService(() => new ObsConnectionOptions(server.Port, "")); using var cts = new CancellationTokenSource();
    var task = obs.CaptureAsync(new(0, "ns", "OBS", CaptureSourceKind.ObsInput, "ns"), "game", null, cts.Token);
    await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
    try { await task; throw new Exception("Cancellation not observed"); } catch (OperationCanceledException) { }
    Assert(!obs.IsConnected, "canceled socket retained"); finish.SetResult(); await server.Completion;
});

await Test("Presentation: nested interaction holds release independently and leases are idempotent", () =>
{
    var gate = new PresentationGate(); var changes = 0; gate.Changed += () => changes++;
    var pointer = new object(); gate.SetHeld(pointer, true); gate.SetHeld(pointer, true);
    var dialog = gate.Hold(); gate.SetHeld(pointer, false);
    Assert(gate.IsHeld && changes == 1, "pointer leave released the open dialog");
    dialog.Dispose(); dialog.Dispose();
    Assert(!gate.IsHeld && changes == 2, "interaction lifetime is not idempotent");
    return Task.CompletedTask;
});

await Test("Floating placement: negative monitors, disconnected screens and screen edges", () =>
{
    var primary = new PixelRect(0, 0, 1920, 1040); var left = new PixelRect(-1920, 0, 1920, 1040);
    Assert(FloatingPlacement.Clamp(new(-1500, 800, 560, 86), new[] { left, primary }) == new PixelRect(-1500, 800, 560, 86), "negative monitor coordinates lost");
    Assert(FloatingPlacement.Clamp(new(-1500, 800, 560, 86), new[] { primary }) == new PixelRect(0, 800, 560, 86), "disconnected monitor strands toolbar");
    Assert(FloatingPlacement.Clamp(new(1900, 1030, 560, 86), new[] { primary }) == new PixelRect(1360, 954, 560, 86), "toolbar extends past work area");
    Assert(FloatingPlacement.Clamp(new(0, 0, 2500, 1300), new[] { primary }) == primary, "oversized bounds not constrained");
    return Task.CompletedTask;
});

await Test("AI endpoint: HTTP root, versioned bases, prefixes and complete endpoints", () =>
{
    var cases = new Dictionary<string, string>
    {
        [" http://203.0.113.7:8080/ "] = "http://203.0.113.7:8080/v1/chat/completions",
        ["https://model.example/v1/"] = "https://model.example/v1/chat/completions",
        ["https://model.example/relay/v1"] = "https://model.example/relay/v1/chat/completions",
        ["http://localhost:8000/v1/chat/completions/"] = "http://localhost:8000/v1/chat/completions"
    };
    foreach (var pair in cases) Assert(AiExplanationService.ResolveEndpoint(pair.Key).AbsoluteUri == pair.Value, "endpoint normalized incorrectly");
    var root = new AppSettings { AiBaseUrl = "https://model.example", AiModel = "m" };
    var full = root.Copy(); full.AiBaseUrl = "https://model.example/v1/chat/completions";
    Assert(AiExplanationService.CacheKey(root, "word", "sentence") == AiExplanationService.CacheKey(full, "word", "sentence"), "equivalent addresses have different cache identities");
    return Task.CompletedTask;
});
await Test("AI HTTP configuration: requests use saved model and encrypted credential", async () =>
{
    using var client = new HttpClient(new FakeHttp(async (request, ct) =>
    {
        Assert(request.RequestUri!.Scheme == "http" && request.RequestUri.AbsolutePath == "/v1/chat/completions", "HTTP root not accepted");
        Assert(request.Headers.Authorization?.Parameter == "fixture-key", "stored credential not used");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Assert(body.RootElement.GetProperty("model").GetString() == "fixture-model" && !body.RootElement.GetProperty("stream").GetBoolean(), "saved model not applied");
        return FakeHttp.Json("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
    }));
    var settings = new AppSettings { AiBaseUrl = "http://203.0.113.7:8080", AiModel = "fixture-model", AiSecret = AppSettings.Protect("fixture-key") };
    Assert(await new AiExplanationService(client).TestConnectionAsync(settings, CancellationToken.None) == "OK", "connection test failed");
});
await Test("AI failures: homepage HTML, empty content and auth errors are actionable", async () =>
{
    var fixtures = new[]
    {
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>homepage</html>", Encoding.UTF8, "text/html") },
        FakeHttp.Json("{\"choices\":[{\"message\":{\"content\":\"\"}}]}"),
        new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private server detail") }
    };
    var expected = new[] { "网页", "没有返回", "密钥" };
    for (var i = 0; i < fixtures.Length; i++)
    {
        var index = i;
        using var client = new HttpClient(new FakeHttp((_, _) => Task.FromResult(fixtures[index])));
        var raised = false;
        try { await new AiExplanationService(client).TestConnectionAsync(new() { AiBaseUrl = "https://model.example", AiModel = "m" }, CancellationToken.None); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { raised = e.Message.Contains(expected[index]) && !e.Message.Contains("private server detail"); }
        Assert(raised, "failure missing safe actionable message");
    }
});
await Test("Dictionary: actual inflection keeps its own definition while normalizing lemma", () =>
{
    using var dictionary = new OfflineDictionary();
    var entry = dictionary.Lookup("bearing");
    Assert(entry.Word == "bear" && entry.Observed == "bearing" && entry.ObservedPhonetic?.Contains("b") == true
        && entry.ObservedTranslation?.Contains("轴承") == true && entry.Translation.Contains("熊"), "inflection definition was collapsed into lemma");
    return Task.CompletedTask;
});
await Test("Learning priority: difficult and unknown sentences rise above common sentences", () =>
{
    using var dictionary = new OfflineDictionary(Path.Combine(Path.GetTempPath(), "GameLearn-priority-missing-" + Guid.NewGuid(), "missing.sqlite"));
    var saved = new Dictionary<string, SavedWord>(StringComparer.OrdinalIgnoreCase);
    var easy = LearningPriority.Score(new RecognizedLine("The old ship is here.", 0.99, new(0, 0, 100, 20)), dictionary, saved);
    var hard = LearningPriority.Score(new RecognizedLine("The uncompromising archaeologist deciphered the inscription.", 0.99, new(0, 0, 100, 20)), dictionary, saved);
    Assert(hard.Score > easy.Score && hard.PriorityLevel > easy.PriorityLevel && hard.PriorityLabel == "优先学习", "difficulty ranking did not elevate rare long words");
    return Task.CompletedTask;
});

var reportPath = args.Length > 0 ? args[0] : "test-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { total = results.Count, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
sealed class FakeOcr(Func<CapturedFrame, CancellationToken, Task<RecognitionResult>> recognize) : IOcrProvider
{
    public string Name => "fake";
    public int Calls { get; private set; }
    public Task<RecognitionResult> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) { Calls++; return recognize(frame, cancellationToken); }
}
sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public HashSet<(uint Modifiers, uint Key)> Blocked { get; } = new();
    private readonly Dictionary<int, (uint Modifiers, uint Key)> active = new();
    public int Register(int id, uint modifiers, uint key)
    {
        if (Blocked.Contains((modifiers, key)) || active.Values.Contains((modifiers, key))) return 1409;
        active[id] = (modifiers, key); return 0;
    }
    public void Unregister(int id) => active.Remove(id);
}
