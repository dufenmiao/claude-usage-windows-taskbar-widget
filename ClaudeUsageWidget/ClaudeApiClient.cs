using System.Text.Json.Nodes;

namespace ClaudeUsageWidgetProvider;

public sealed class RateLimitEntry
{
    public string Label { get; set; } = "";
    public double Utilization { get; set; }
    public DateTimeOffset ResetsAt { get; set; }
}

public sealed class UsageData
{
    public List<RateLimitEntry> Limits { get; set; } = [];
    public double? SpendUsed { get; set; }
    public double? SpendLimit { get; set; }
}

internal sealed class ClaudeApiClient : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private DateTimeOffset _lastFetchTime = DateTimeOffset.MinValue;
    private UsageData? _cachedUsage;
    private List<OAuthCredential> _credentials = [];
    private int _credentialIndex;
    private bool _noReload;
    private readonly ServiceType _service;

    private OAuthCredential? _credential => _credentialIndex < _credentials.Count ? _credentials[_credentialIndex] : null;
    private readonly string? _accountKey;

    internal ClaudeApiClient() { }

    internal ClaudeApiClient(AccountInfo account)
    {
        _credentials = [account.Credential];
        _credentialIndex = 0;
        _noReload = true;
        _service = account.Service;
        _accountKey = CredentialStore.GetAccountKey(account);
    }

    public ServiceType AccountService => _service;
    public string? AccountKey => _accountKey;

    public string CredentialPath => _credential?.SourcePath ?? "";
    public string? LastError { get; private set; }

    private static readonly TimeSpan DefaultFetchInterval = TimeSpan.FromSeconds(60);
    private TimeSpan _fetchInterval = DefaultFetchInterval;

    public async Task<UsageData?> GetUsageAsync(bool forceRefresh = false)
    {
        // Reload credentials from disk on each fresh attempt so changes (add/remove file) take effect immediately
        if (_credentialIndex == 0 && !_noReload)
            _credentials = CredentialStore.LoadAllCredentials();

        if (_credential == null)
        {
            LastError = "No credentials found";
            return null;
        }

        if (!forceRefresh && _cachedUsage != null &&
            DateTimeOffset.UtcNow - _lastFetchTime < _fetchInterval)
        {
            return _cachedUsage;
        }

        if (_credential.IsExpired)
        {
            // The Claude CLI may have refreshed the token on disk in the meantime. Re-read first:
            // refresh tokens rotate, so refreshing with a stale one would fail (or race the CLI).
            var sourcePath = _credential.SourcePath;
            if (_service == ServiceType.Claude && !string.IsNullOrEmpty(sourcePath))
            {
                var diskCred = CredentialStore.LoadCredentialFromPath(sourcePath);
                if (diskCred != null && diskCred.RefreshToken != _credential.RefreshToken)
                    _credentials[_credentialIndex] = diskCred;
            }

            if (_credential!.IsExpired && !await RefreshTokenAsync())
            {
                LastError = "Token expired and refresh failed" +
                            (_lastRefreshError != null ? $" ({_lastRefreshError})" : "");
                return _cachedUsage;
            }
        }

        try
        {
            var usage = await FetchWithRetryAsync();
            if (usage != null)
            {
                LastError = null;
                _cachedUsage = usage;
                _lastFetchTime = DateTimeOffset.UtcNow;
            }
            else if (_cachedUsage == null)
            {
                LastError ??= "No usage data available";
            }
            return _cachedUsage;
        }
        catch (UnauthorizedAccessException)
        {
            // Try refresh first
            if (await RefreshTokenAsync())
            {
                try
                {
                    var usage = await FetchUsageAsync();
                    if (usage != null)
                    {
                        LastError = null;
                        _cachedUsage = usage;
                        _lastFetchTime = DateTimeOffset.UtcNow;
                    }
                    return _cachedUsage;
                }
                catch { }
            }

            // Refresh failed — re-read credentials from disk
            var sourcePath = _credential?.SourcePath;
            if (!string.IsNullOrEmpty(sourcePath))
            {
                var freshCred = CredentialStore.LoadCredentialFromPath(sourcePath);
                if (freshCred != null && freshCred.AccessToken != _credential!.AccessToken)
                {
                    _credentials[_credentialIndex] = freshCred;
                    return await GetUsageAsync(forceRefresh: true);
                }
            }

            LastError = "Invalid credentials — re-login with Claude CLI";
            return _cachedUsage;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return _cachedUsage;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;

    private async Task<UsageData?> FetchWithRetryAsync()
    {
        const int maxRetries = 2;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await FetchUsageAsync();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxRetries - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
            }
        }
        return await FetchUsageAsync(); // last attempt — let it throw
    }

    private async Task<UsageData?> FetchUsageAsync()
    {
        if (_service == ServiceType.Codex)
            return await FetchCodexUsageAsync();

        return await FetchFromRateLimitHeadersAsync();
    }

    private async Task<UsageData?> FetchFromRateLimitHeadersAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.anthropic.com/v1/messages");
        request.Headers.Add("Authorization", $"Bearer {_credential!.AccessToken}");
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            """{"model":"claude-haiku-4-5-20251001","max_tokens":1,"messages":[{"role":"user","content":"."}]}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                  or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"{(int)response.StatusCode} {response.StatusCode} — invalid or expired credentials");

        response.EnsureSuccessStatusCode();

        var limits = new List<RateLimitEntry>();

        // Parse unified rate limit headers
        string[] windows = ["5h", "7d"];
        foreach (var window in windows)
        {
            var utilHeader = $"anthropic-ratelimit-unified-{window}-utilization";
            var resetHeader = $"anthropic-ratelimit-unified-{window}-reset";

            if (response.Headers.TryGetValues(utilHeader, out var utilValues))
            {
                var utilStr = utilValues.FirstOrDefault();
                if (double.TryParse(utilStr, System.Globalization.CultureInfo.InvariantCulture, out var util))
                {
                    var resetsAt = DateTimeOffset.UtcNow;
                    if (response.Headers.TryGetValues(resetHeader, out var resetValues))
                    {
                        var resetStr = resetValues.FirstOrDefault();
                        if (long.TryParse(resetStr, out var resetUnix))
                            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
                    }

                    limits.Add(new RateLimitEntry
                    {
                        Label = $"unified-{window}",
                        Utilization = util * 100.0,
                        ResetsAt = resetsAt
                    });
                }
            }
        }

        if (limits.Count == 0) return null;
        return new UsageData { Limits = limits };
    }

    private async Task<UsageData?> FetchCodexUsageAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Add("Authorization", $"Bearer {_credential!.AccessToken}");
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36");
        request.Headers.Add("oai-client-build-number", "5146072");
        request.Headers.Add("oai-language", "en-US");
        request.Headers.Add("Referer", "https://chatgpt.com/codex/settings/usage");
        request.Headers.Add("sec-fetch-mode", "cors");
        request.Headers.Add("sec-fetch-site", "same-origin");

        var response = await Http.SendAsync(request);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                  or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"{(int)response.StatusCode} {response.StatusCode} — invalid or expired credentials");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonNode.Parse(json);
        if (doc == null) return null;

        var limits = new List<RateLimitEntry>();

        // Labels are derived from the response — no hardcoded "5h"/"7d", we use limit_window_seconds.
        AddRateLimit(limits, doc["rate_limit"], "session");
        AddRateLimit(limits, doc["code_review_rate_limit"], "review");

        // Per-model limits (e.g. GPT-5.3-Codex-Spark) — name taken straight from the response.
        if (doc["additional_rate_limits"] is JsonArray extras)
        {
            foreach (var item in extras)
            {
                var name = item?["limit_name"]?.GetValue<string>() ?? "extra";
                AddRateLimit(limits, item?["rate_limit"], name);
            }
        }

        static void AddRateLimit(List<RateLimitEntry> list, JsonNode? rl, string baseLabel)
        {
            if (rl == null) return;
            AddWindow(list, rl["primary_window"], baseLabel);
            AddWindow(list, rl["secondary_window"], baseLabel);
        }

        static void AddWindow(List<RateLimitEntry> list, JsonNode? window, string baseLabel)
        {
            if (window == null) return;
            var seconds = window["limit_window_seconds"]?.GetValue<long>() ?? 0;
            var windowLabel = seconds switch
            {
                0 => "",
                _ when seconds % 86400 == 0 => $"{seconds / 86400}d",
                _ when seconds % 3600 == 0 => $"{seconds / 3600}h",
                _ when seconds % 60 == 0 => $"{seconds / 60}m",
                _ => $"{seconds}s"
            };
            list.Add(new RateLimitEntry
            {
                Label = string.IsNullOrEmpty(windowLabel) ? baseLabel : $"{baseLabel}-{windowLabel}",
                Utilization = window["used_percent"]?.GetValue<double>() ?? 0,
                ResetsAt = DateTimeOffset.FromUnixTimeSeconds(
                    window["reset_at"]?.GetValue<long>() ?? 0)
            });
        }

        if (limits.Count == 0) return null;
        return new UsageData { Limits = limits };
    }

    // Same endpoint and public client id the Claude Code CLI uses for its OAuth refresh.
    private const string ClaudeTokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClaudeOAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private string? _lastRefreshError;

    public async Task<bool> RefreshTokenAsync()
    {
        _lastRefreshError = null;
        if (_credential == null || string.IsNullOrEmpty(_credential.RefreshToken))
        {
            _lastRefreshError = "no refresh token";
            return false;
        }

        // Codex refresh not implemented — token valid for ~10 days, user can re-login
        if (_service == ServiceType.Codex) return false;

        try
        {
            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _credential.RefreshToken,
                ["client_id"] = ClaudeOAuthClientId,
            };
            if (_credential.Scopes.Length > 0)
                body["scope"] = string.Join(" ", _credential.Scopes);

            using var content = new StringContent(body.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(ClaudeTokenUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                _lastRefreshError = $"HTTP {(int)response.StatusCode}";
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);

            var newAccessToken = doc?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(newAccessToken))
            {
                _lastRefreshError = "no access_token in response";
                return false;
            }

            var newRefreshToken = doc!["refresh_token"]?.GetValue<string>();
            var expiresIn = doc["expires_in"]?.GetValue<long>() ?? 0;
            if (expiresIn <= 0) expiresIn = 3600;
            var scope = doc["scope"]?.GetValue<string>();

            _credential.AccessToken = newAccessToken;
            if (!string.IsNullOrEmpty(newRefreshToken))
                _credential.RefreshToken = newRefreshToken;
            _credential.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000);
            if (!string.IsNullOrWhiteSpace(scope))
                _credential.Scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Write back so the Claude CLI picks up the rotated refresh token too.
            CredentialStore.SaveCredential(_credential);
            return true;
        }
        catch (Exception ex)
        {
            _lastRefreshError = ex.Message;
            return false;
        }
    }

    public void Dispose() { }
}
