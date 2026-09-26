using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLearn;

public sealed class SentenceTranslationService(HttpClient client)
{
    public static string DefaultBaseUrl(SentenceTranslationProvider provider) => provider == SentenceTranslationProvider.DeepL
        ? "https://api-free.deepl.com" : "https://api.cognitive.microsofttranslator.com";
    public static string CacheKey(AppSettings settings, string sentence)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.TranslationProvider}|{ResolveBaseUrl(settings)}|{settings.TranslationSourceLanguage}|{settings.TranslationTargetLanguage}|{sentence}")));
    public static void ValidateConfiguration(AppSettings settings, bool allowDisabled = false)
    {
        if (allowDisabled && string.IsNullOrWhiteSpace(settings.TranslationSecret) && string.IsNullOrWhiteSpace(settings.TranslationBaseUrl)) return;
        if (string.IsNullOrWhiteSpace(AppSettings.Unprotect(settings.TranslationSecret))) throw new ArgumentException("请填写快速句子翻译 API Key。");
        if (!Uri.TryCreate(ResolveBaseUrl(settings), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("快速翻译地址必须是 http:// 或 https:// 地址。");
    }
    public async Task<string> TranslateAsync(AppSettings settings, string sentence, CancellationToken token)
    {
        ValidateConfiguration(settings);
        var secret = AppSettings.Unprotect(settings.TranslationSecret);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = CreateRequest(settings, secret, sentence);
        try
        {
            using var response = await client.SendAsync(request, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"快速翻译服务返回 HTTP {(int)response.StatusCode}：{Hint(response.StatusCode)}");
            return ParseResponse(settings.TranslationProvider, body);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("快速翻译等待超过 15 秒。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接快速翻译服务，请检查网络和地址。"); }
    }
    public static HttpRequestMessage CreateRequest(AppSettings settings, string secret, string sentence)
    {
        if (settings.TranslationProvider == SentenceTranslationProvider.DeepL)
        {
            var uri = ResolveBaseUrl(settings).TrimEnd('/') + "/v2/translate";
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Add("Authorization", "DeepL-Auth-Key " + secret);
            request.Content = JsonContent.Create(new { text = new[] { sentence }, target_lang = string.IsNullOrWhiteSpace(settings.TranslationTargetLanguage) ? "ZH" : settings.TranslationTargetLanguage.ToUpperInvariant(),
                source_lang = string.IsNullOrWhiteSpace(settings.TranslationSourceLanguage) ? null : settings.TranslationSourceLanguage.ToUpperInvariant() });
            return request;
        }
        var target = string.IsNullOrWhiteSpace(settings.TranslationTargetLanguage) || settings.TranslationTargetLanguage.Equals("ZH", StringComparison.OrdinalIgnoreCase)
            ? "zh-Hans" : settings.TranslationTargetLanguage;
        var query = $"api-version=3.0&to={Uri.EscapeDataString(target)}" + (string.IsNullOrWhiteSpace(settings.TranslationSourceLanguage) ? "" : $"&from={Uri.EscapeDataString(settings.TranslationSourceLanguage)}");
        var azureRequest = new HttpRequestMessage(HttpMethod.Post, ResolveBaseUrl(settings).TrimEnd('/') + "/translate?" + query);
        azureRequest.Headers.Add("Ocp-Apim-Subscription-Key", secret);
        if (!string.IsNullOrWhiteSpace(settings.TranslationRegion)) azureRequest.Headers.Add("Ocp-Apim-Subscription-Region", settings.TranslationRegion.Trim());
        azureRequest.Content = JsonContent.Create(new[] { new { Text = sentence } });
        return azureRequest;
    }
    public static string ParseResponse(SentenceTranslationProvider provider, string body)
    {
        using var json = JsonDocument.Parse(body);
        string? text = provider == SentenceTranslationProvider.DeepL
            ? json.RootElement.GetProperty("translations")[0].GetProperty("text").GetString()
            : json.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("快速翻译服务没有返回译文。");
        return text.Trim();
    }
    private static string ResolveBaseUrl(AppSettings settings) => string.IsNullOrWhiteSpace(settings.TranslationBaseUrl) ? DefaultBaseUrl(settings.TranslationProvider) : settings.TranslationBaseUrl.Trim().TrimEnd('/');
    private static string Hint(HttpStatusCode code) => code switch { HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API Key 无效或没有权限。", HttpStatusCode.TooManyRequests => "服务限流，请稍后重试。", _ => "请检查 API 配置。" };
}
