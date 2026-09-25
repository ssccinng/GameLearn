using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameLearn;

/// <summary>Exercises actual OS keyboard input and native capture resources, not direct command invocation.</summary>
internal static class InteractionRegression
{
    public static async Task RunAsync(string output)
    {
        Environment.SetEnvironmentVariable("GAMELEARN_DATA_DIR", Path.Combine(output, "isolated-data"));
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(output, "interaction-results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("Interaction regression: " + name);
        }
        var main = new MainWindow(); main.Show();
        var text = new TextBlock { Text = "The wrecked ship waits beyond the ancient gate.", FontSize = 32, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(35) };
        var target = new Window { Title = "GameLearn keyboard and capture regression", Width = 800, Height = 460, Background = Brushes.White, Content = text };
        target.Show();
        var handle = new WindowInteropHelper(target).Handle;
        var vm = main.Vm;
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainViewModel.Status))
                File.AppendAllText(Path.Combine(output, "status.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} {vm.Status} / {vm.CaptureDebugState}\n");
        };
        var source = new WindowSource(handle, target.Title, "GameLearn"); vm.Sources.Add(source); vm.SelectedSource = source;
        await target.Dispatcher.InvokeAsync(target.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Check("Capture shortcut independently registered at startup", vm.HotkeyStatus.Contains("即时识别：Ctrl+Alt+E · 已启用"));
        File.WriteAllText(Path.Combine(output, "hotkeys.txt"), vm.HotkeyStatus + "\n" + vm.HotkeyWarning);

        main.WindowState = WindowState.Minimized; target.Activate();
        async Task<RecognitionResult> PressCaptureAsync()
        {
            var completion = new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(RecognitionRequest request, RecognitionResult result) { if (request.Trigger == TriggerKind.Manual) completion.TrySetResult(result); }
            vm.Scheduler.Completed += Handler;
            try
            {
                KeyboardInput.Press(0x11, 0x12, 0x45); // Actual Ctrl+Alt+E through the Windows input queue.
                try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch
                {
                    File.WriteAllText(Path.Combine(output, "timeout-state.json"), JsonSerializer.Serialize(new { vm.Status, vm.IsAutomatic, busy = vm.Scheduler.IsBusy, metrics = vm.CaptureMetrics.ToString(), target.WindowState }));
                    throw;
                }
            }
            finally { vm.Scheduler.Completed -= Handler; }
        }
        void CloseLookup() { foreach (var window in System.Windows.Application.Current.Windows.OfType<LookupWindow>().ToArray()) window.Close(); }
        var first = await PressCaptureAsync();
        Check("Real Ctrl+Alt+E works with main window minimized and automatic off", first.Lines.Any(l => l.Text.Contains("wrecked")));
        Check("Native shortcut opens the lookup window", System.Windows.Application.Current.Windows.OfType<LookupWindow>().Any());
        var lookup = System.Windows.Application.Current.Windows.OfType<LookupWindow>().Single();
        lookup.WindowState = WindowState.Minimized; target.Activate();
        await PressCaptureAsync();
        Check("Shortcut restores a minimized lookup window", lookup.WindowState == WindowState.Normal);
        CloseLookup(); target.Activate();
        Check("Manual capture releases its native session", !vm.CaptureMetrics.Active);

        var automaticResults = 0;
        vm.Scheduler.Completed += (request, _) => { if (request.Trigger == TriggerKind.Automatic) automaticResults++; };
        var before = vm.CaptureMetrics;
        vm.IsAutomatic = true;
        await WaitUntilAsync(() => automaticResults >= 3, TimeSpan.FromSeconds(15));
        var streaming = vm.CaptureMetrics;
        Check("Three automatic screenshots reuse a single native session", streaming.Started == before.Started + 1 && streaming.Stopped == before.Stopped && streaming.Active);
        var second = await PressCaptureAsync(); CloseLookup();
        Check("Real shortcut captures a new frame during automatic mode", second.Frame.Id != first.Frame.Id && vm.CaptureMetrics.Started == streaming.Started);
        target.Width = 960; target.Height = 560;
        await Task.Delay(600);
        var resized = await PressCaptureAsync(); CloseLookup();
        Check("Resize refreshes buffers without restarting capture session", resized.Frame.Width > first.Frame.Width && vm.CaptureMetrics.Started == streaming.Started);
        vm.IsAutomatic = false;
        await WaitUntilAsync(() => !vm.CaptureMetrics.Active, TimeSpan.FromSeconds(5));
        var completedBefore = automaticResults; await Task.Delay(1500);
        Check("Switching automatic off releases the session and stops work", automaticResults == completedBefore && vm.CaptureMetrics.Stopped == before.Stopped + 1);

        // Explicitly block one binding in another HWND, then verify real dispatch for a different binding.
        var blocked = HotkeyManager.Parse("Ctrl+Alt+Shift+F6");
        Check("Conflict fixture registered", RegisterHotKey(handle, 701, blocked.Modifiers, blocked.Key));
        try
        {
            using var manager = new HotkeyManager(target);
            var invoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registered = manager.Configure("Ctrl+Alt+Shift+F7", "Ctrl+Alt+Shift+F6", "Ctrl+Alt+Shift+F8",
                async () => { await Task.Yield(); invoked.TrySetResult(SynchronizationContext.Current is DispatcherSynchronizationContext && target.Dispatcher.CheckAccess()); }, () => { }, () => { });
            Check("One native hotkey conflict leaves other shortcuts active", registered[0].MatchesRequest && !registered[1].MatchesRequest && registered[2].MatchesRequest);
            KeyboardInput.Press(0x11, 0x12, 0x10, 0x76);
            Check("Native hotkey async continuation runs on WPF dispatcher", await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { UnregisterHotKey(handle, 701); }

        using (var capture = new CaptureService())
        {
            var obs = CaptureService.ListWindows().FirstOrDefault(w => w.ProcessName == "obs64");
            if (obs is not null)
            {
                await capture.SetContinuousAsync(true);
                var frame = await capture.CaptureAsync(obs, "OBS regression", null, CancellationToken.None);
                File.WriteAllBytes(Path.Combine(output, "obs.png"), frame.FullPng);
                var backend = capture.LastBackend;
                await capture.CaptureAsync(obs, "OBS regression", null, CancellationToken.None);
                Check("OBS screenshots use PrintWindow without capture sessions or border", backend == "OBS · PrintWindow" && capture.WgcSessionsStarted == 0);
                await capture.SetContinuousAsync(false);
            }
        }
        target.Close(); main.Close();
    }
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Interaction test timed out."); await Task.Delay(50); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);

    private static class KeyboardInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public Keyboard Keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
        public static void Press(params ushort[] keys)
        {
            var inputs = keys.Select(key => new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Key = key } } })
                .Concat(keys.Reverse().Select(key => new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { Key = key, Flags = 2 } } })).ToArray();
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Keyboard test input failed.");
        }
    }
}
