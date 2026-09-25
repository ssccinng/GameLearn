namespace GameLearn;

public static class AutomaticRecovery
{
    public static TimeSpan? Delay(Exception error, int failures, bool cloudOcrRequest)
    {
        if (error is OcrRateLimitException limit) return limit.RetryAfter;
        if (error is OcrAuthenticationException or ArgumentException or FileNotFoundException or DirectoryNotFoundException
            or DllNotFoundException or BadImageFormatException or NotSupportedException) return null;
        // A timed-out cloud submission may already exist remotely: do not create more jobs automatically.
        if (cloudOcrRequest) return null;
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(failures, 1, 5))));
    }
}
