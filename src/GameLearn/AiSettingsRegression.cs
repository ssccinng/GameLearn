using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SkiaSharp;

namespace GameLearn;

internal static class AiSettingsRegression
{
    public static async Task RunAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var results = new List<object>();
        void Check(string name, bool passed)
        {
            results.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "ai-settings-results.json"), JsonSerializer.Serialize(new { results }, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("AI settings regression: " + name);
        }
        var window = new MainWindow(); // Build controls without showing a window or registering shortcuts.
        try
        {
            ((TextBox)window.FindName("LocalIntervalBox")).Text = "not a number";
            ((TextBox)window.FindName("ObsPortBox")).Text = "not a port";
            ((TextBox)window.FindName("AiUrlBox")).Text = "http://model.invalid:8080";
            ((TextBox)window.FindName("AiModelBox")).Text = "fixture-model";
            ((PasswordBox)window.FindName("AiTokenBox")).Password = "fixture-key";
            var generation = window.Vm.Scheduler.Generation;
            ((Button)window.FindName("SaveAiButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = AppSettings.Load();
            Check("AI section saves independently of invalid OCR/OBS form fields", saved.AiModel == "fixture-model" && saved.LocalIntervalSeconds == 1 && saved.ObsPort == 4455);
            Check("Saved key is protected and usable after reload", saved.AiSecret != "fixture-key" && AppSettings.Unprotect(saved.AiSecret) == "fixture-key");
            Check("Saving AI updates runtime without invalidating game capture", window.Vm.Settings.AiModel == saved.AiModel && window.Vm.Scheduler.Generation == generation);
            Check("AI save status is visible next to the form", window.Vm.AiConfigurationStatus.Contains("已保存"));
            ((TextBox)window.FindName("AiUrlBox")).Text = "invalid address";
            ((Button)window.FindName("SaveAiButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Invalid AI input does not overwrite the working configuration", AppSettings.Load().AiBaseUrl == saved.AiBaseUrl && window.Vm.Settings.AiBaseUrl == saved.AiBaseUrl);
            Check("Invalid save remains visible in AI panel", window.Vm.AiConfigurationStatus.Contains("未保存"));
        }
        finally { await window.Vm.ShutdownAsync(); }

        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new StubHandler(async (request, ct) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (calls > 1) Check("New requests use newly saved model", body.RootElement.GetProperty("model").GetString() == "new-model");
            if (calls == 1) return await response.Task; // Deliberately ignore cancellation to exercise stale-result rejection.
            return Json("new explanation");
        }));
        var vm = new MainViewModel(client); vm.SelectedSource = null;
        try
        {
            using var bitmap = new SKBitmap(100, 100); bitmap.Erase(SKColors.White);
            var frame = CaptureService.CreateFrame(bitmap, "test", "fixture");
            var line = new RecognizedLine("The ancient gate.", 0.99, new(0, 0, 90, 20));
            vm.Present(new(frame, new[] { line }, "fixture", TimeSpan.Zero)); vm.SelectedLine = line; vm.Learn("ancient");
            var old = vm.Settings.Copy(); var oldKey = AiExplanationService.CacheKey(old, vm.DetailWord, vm.DetailSentence);
            var pending = vm.ExplainAsync();
            vm.SaveAiConfiguration("http://model.invalid:8080/v1/chat/completions", "new-model", "new-fixture-key");
            response.SetResult(Json("stale explanation")); await pending;
            Check("Changing AI settings discards old in-flight explanation", vm.AiExplanation.Length == 0 && vm.Store.GetExplanation(oldKey) is null);
            await vm.ExplainAsync();
            Check("Next explanation works immediately without restart", vm.AiExplanation == "new explanation");
            var beforeTest = calls;
            await vm.TestAiConnectionAsync();
            Check("Connection test makes a real request rather than using cached explanation", calls == beforeTest + 1 && vm.AiConfigurationStatus.Contains("连接成功") && !vm.IsAiTesting);
        }
        finally { await vm.ShutdownAsync(); }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
