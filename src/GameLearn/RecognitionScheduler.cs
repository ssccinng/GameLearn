namespace GameLearn;

/// <summary>Single worker, one replaceable pending manual frame; events run on its caller's synchronization context.</summary>
public sealed class RecognitionScheduler : IDisposable
{
    private readonly object sync = new();
    private RecognitionRequest? pendingManual;
    private CancellationTokenSource? active;
    private TriggerKind? activeTrigger;
    private bool busy;
    private bool autoEnabled;
    private long generation;
    private bool disposed;
    private RecognitionResult? cached;
    private string? cachedEngine;
    public event Action<RecognitionRequest, RecognitionResult>? Completed;
    public event Action<Exception, TriggerKind>? Failed;
    public event Action<bool>? BusyChanged;
    public long Generation { get { lock (sync) return generation; } }
    public bool IsBusy { get { lock (sync) return busy; } }
    public bool AutoEnabled { get { lock (sync) return autoEnabled; } }
    public DateTimeOffset RetryAt { get; private set; }
    public void SetAutomatic(bool enabled)
    {
        lock (sync)
        {
            autoEnabled = enabled;
            if (!enabled && activeTrigger == TriggerKind.Automatic) active?.Cancel();
        }
    }
    public void Invalidate()
    {
        lock (sync) { generation++; pendingManual = null; cached = null; active?.Cancel(); }
    }
    public bool IsCurrent(RecognitionRequest request)
    {
        lock (sync) return !disposed && request.Generation == generation && (request.Trigger == TriggerKind.Manual || autoEnabled);
    }
    public Task EnqueueAsync(RecognitionRequest request)
    {
        lock (sync)
        {
            if (!IsCurrent(request)) return Task.CompletedTask;
            if (DateTimeOffset.UtcNow < RetryAt && request.Provider is PaddleCloudOcrProvider)
            {
                Failed?.Invoke(new OcrRateLimitException(RetryAt - DateTimeOffset.UtcNow), request.Trigger); return Task.CompletedTask;
            }
            if (busy)
            {
                if (request.Trigger == TriggerKind.Manual) pendingManual = request;
                return Task.CompletedTask;
            }
            busy = true;
        }
        return RunAsync(request);
    }
    private async Task RunAsync(RecognitionRequest request)
    {
        BusyChanged?.Invoke(true);
        while (true)
        {
            CancellationTokenSource cancellation;
            lock (sync) { active = cancellation = new(); activeTrigger = request.Trigger; }
            try
            {
                if (IsCurrent(request))
                {
                    if (request.Provider is PaddleCloudOcrProvider && DateTimeOffset.UtcNow < RetryAt)
                        throw new OcrRateLimitException(RetryAt - DateTimeOffset.UtcNow);
                    RecognitionResult result;
                    if (request.Trigger == TriggerKind.Automatic && cached?.Frame.Fingerprint == request.Frame.Fingerprint && cachedEngine == request.Provider.Name)
                        result = cached with { Frame = request.Frame, Elapsed = TimeSpan.Zero, ScanMode = "画面未变化" };
                    else
                    {
                        result = request.Provider is LocalOcrProvider local && request.Trigger == TriggerKind.Automatic && cached is not null
                            ? await local.RecognizeAutomaticAsync(request.Frame, cancellation.Token)
                            : await request.Provider.RecognizeAsync(request.Frame, cancellation.Token);
                        result = result with { Lines = SentenceAssembler.Merge(result.Lines) };
                    }
                    if (!cancellation.IsCancellationRequested && IsCurrent(request))
                    {
                        cached = result; cachedEngine = request.Provider.Name;
                        Completed?.Invoke(request, result);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (e is OcrRateLimitException limit && IsCurrent(request)) RetryAt = DateTimeOffset.UtcNow + limit.RetryAfter;
                if (IsCurrent(request) && !cancellation.IsCancellationRequested) Failed?.Invoke(e, request.Trigger);
            }
            finally { lock (sync) { active = null; activeTrigger = null; } cancellation.Dispose(); }
            lock (sync)
            {
                if (pendingManual is { } next && IsCurrent(next)) { pendingManual = null; request = next; }
                else { pendingManual = null; busy = false; break; }
            }
        }
        BusyChanged?.Invoke(false);
    }
    public void Dispose() { lock (sync) { disposed = true; active?.Cancel(); pendingManual = null; } }
}
