using System.Text.Json;

namespace EasyGet.Services;

internal static class DouyinCookieHealthReporter
{
    public static string Describe(string? cookieContent)
    {
        if (string.IsNullOrWhiteSpace(cookieContent))
            return "Cookie 未配置";

        var cookies = ParseCookieContent(cookieContent);
        if (cookies.Count == 0)
            return "Cookie 格式未识别";

        string[] requiredKeys = ["ttwid", "odin_tt", "passport_csrf_token"];
        var missing = requiredKeys
            .Where(key => !cookies.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            .ToList();

        if (missing.Count > 0)
            return $"Cookie 缺少 {string.Join("、", missing)}";

        return cookies.TryGetValue("msToken", out var msToken) && !string.IsNullOrWhiteSpace(msToken)
            ? "Cookie 关键项完整"
            : "Cookie 关键项完整 · msToken 可自动生成";
    }

    private static Dictionary<string, string> ParseCookieContent(string cookieContent)
    {
        var trimmed = cookieContent.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
            return ParseCookieJson(trimmed);

        return ParseCookieHeader(trimmed);
    }

    private static Dictionary<string, string> ParseCookieJson(string cookieContent)
    {
        try
        {
            using var document = JsonDocument.Parse(cookieContent);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
                cookies[name] = value.Trim();
            }

            return cookies;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> ParseCookieHeader(string cookieContent)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in cookieContent.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var name = part[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            cookies[name] = part[(separatorIndex + 1)..].Trim();
        }

        return cookies;
    }
}
