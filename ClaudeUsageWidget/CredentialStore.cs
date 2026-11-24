using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageWidgetProvider;

internal enum ServiceType { Claude, Codex }

internal sealed record AccountInfo(
    ServiceType Service,
    OAuthCredential Credential,
    string Label
);

internal sealed class OAuthCredential
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long ExpiresAt { get; set; } // Unix epoch milliseconds
    public string[] Scopes { get; set; } = []; // Claude OAuth scopes, sent back on refresh
    public string SourcePath { get; set; } = ""; // "wsl:~/.claude/.credentials.json" or Windows path

    public bool IsExpired =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAt - 60_000; // 60s buffer
}

internal static class CredentialStore
{
    private const string WslCredPath = "~/.claude/.credentials.json";
    private const string WslSourceMarker = "wsl:" + WslCredPath;

    private enum CredOrigin { Wsl, Windows }

    /// <summary>Definition of one credential source — a combination of service, location and parser.</summary>
    private sealed record CredSource(
        ServiceType Service,
        string Label,
        CredOrigin Origin,
        string RelativePath,
        Func<string, OAuthCredential?> Parse);

    // Canonical order: the native CLI (claude/codex) takes precedence over the orchestrator (hermes).
    // WSL before Windows — users typically run the CLI in Linux; the Windows credential is often stale.
    // "First working wins" — expired ones are skipped, dedup keeps the first survivor in this order.
    private static readonly CredSource[] AllSources =
    [
        new(ServiceType.Claude, "claude-wsl",           CredOrigin.Wsl,     ".claude/.credentials.json", ParseClaudeRaw),
        new(ServiceType.Claude, "claude-windows",       CredOrigin.Windows, ".claude/.credentials.json", ParseClaudeRaw),
        new(ServiceType.Codex,  "codex-wsl",            CredOrigin.Wsl,     ".codex/auth.json",          ParseCodexRaw),
        new(ServiceType.Codex,  "codex-windows",        CredOrigin.Windows, ".codex/auth.json",          ParseCodexRaw),
        new(ServiceType.Codex,  "codex-hermes-wsl",     CredOrigin.Wsl,     ".hermes/auth.json",         ParseHermesRaw),
        new(ServiceType.Codex,  "codex-hermes-windows", CredOrigin.Windows, ".hermes/auth.json",         ParseHermesRaw),
    ];

    private static OAuthCredential? ParseClaudeRaw(string json) => ParseCredentialJson(json, "");
    private static OAuthCredential? ParseCodexRaw(string json)  => ParseCodexCredentialJson(json, "");
    private static OAuthCredential? ParseHermesRaw(string json) => ParseHermesCredentialJson(json, "");

    /// <summary>Reads the raw JSON plus its canonical SourcePath label for the given source.</summary>
    private static (string Json, string SourcePath)? ReadSource(CredSource src)
    {
        if (src.Origin == CredOrigin.Wsl)
        {
            var wslPath = "~/" + src.RelativePath;
            var json = ReadWslFile(wslPath);
            return string.IsNullOrWhiteSpace(json) ? null : ((string, string)?)(json, "wsl:" + wslPath);
        }

        var fullPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            src.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(fullPath) ? ((string, string)?)(File.ReadAllText(fullPath), fullPath) : null;
        }
        catch { return null; }
    }

    /// <summary>Unified per-service dedup key. Same key for the WSL and Windows variants of the same account.</summary>
    private static string? MakeDedupKey(ServiceType service, OAuthCredential cred)
    {
        if (service == ServiceType.Claude)
        {
            var jwtId = GetJwtClaim(cred.AccessToken, "org_id")
                     ?? GetJwtClaim(cred.AccessToken, "organization_id")
                     ?? GetJwtClaim(cred.AccessToken, "sub");
            // Opaque token (sk-ant-oat01-...) has no JWT payload — collapse into a single slot.
            return jwtId != null ? "claude:" + jwtId : "claude:opaque";
        }
        // A Codex JWT always contains account_id or sub.
        var acctId = GetJwtClaim(cred.AccessToken, "account_id")
                  ?? GetJwtClaim(cred.AccessToken, "sub");
        return acctId != null ? "codex:" + acctId : null;
    }

    public static OAuthCredential? LoadCredential()
        => LoadAllAccounts().FirstOrDefault(a => a.Service == ServiceType.Claude)?.Credential;

    /// <summary>Backward compat for ClaudeApiClient.RefreshTokenAsync re-read paths.</summary>
    public static List<OAuthCredential> LoadAllCredentials()
        => LoadAllAccounts().Where(a => a.Service == ServiceType.Claude).Select(a => a.Credential).ToList();

    public static void SaveCredential(OAuthCredential credential)
    {
        if (string.IsNullOrEmpty(credential.SourcePath)) return;

        if (credential.SourcePath == WslSourceMarker)
        {
            SaveWslCredential(credential);
            return;
        }

        try
        {
            JsonNode? root;
            if (File.Exists(credential.SourcePath))
            {
                var existing = File.ReadAllText(credential.SourcePath);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            ApplyCredential(root, credential);

            // Write to a temp file and swap it in, so the Claude CLI never reads a half-written file.
            var options = new JsonSerializerOptions { WriteIndented = true };
            var tmpPath = credential.SourcePath + ".tmp";
            File.WriteAllText(tmpPath, root.ToJsonString(options));
            File.Move(tmpPath, credential.SourcePath, overwrite: true);
        }
        catch
        {
            // Best-effort write-back
        }
    }

    private static void SaveWslCredential(OAuthCredential credential)
    {
        try
        {
            // Read existing JSON from WSL
            var existingJson = RunWsl($"cat {WslCredPath}");
            JsonNode root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson) ?? new JsonObject();

            ApplyCredential(root, credential);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var newJson = root.ToJsonString(options);

            // Write via wsl.exe stdin
            if (!File.Exists(WslExe)) return;
            var psi = new ProcessStartInfo
            {
                FileName = WslExe,
                Arguments = $"-- bash -c 'cat > {WslCredPath}'",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            proc.StandardInput.Write(newJson);
            proc.StandardInput.Close();
            proc.WaitForExit(3000);
        }
        catch
        {
            // Best-effort write-back
        }
    }

    /// <summary>Updates the token fields inside claudeAiOauth and keeps every other field the CLI
    /// stores there (subscriptionType, rateLimitTier, accountUuid, …).</summary>
    private static void ApplyCredential(JsonNode root, OAuthCredential credential)
    {
        if (root["claudeAiOauth"] is not JsonObject oauth)
        {
            oauth = new JsonObject();
            root["claudeAiOauth"] = oauth;
        }

        oauth["accessToken"] = credential.AccessToken;
        oauth["refreshToken"] = credential.RefreshToken;
        oauth["expiresAt"] = credential.ExpiresAt;
        if (credential.Scopes.Length > 0)
            oauth["scopes"] = new JsonArray(credential.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    private static readonly string WslExe =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    private static string? RunWsl(string command)
    {
        if (!File.Exists(WslExe)) return null;
        var psi = new ProcessStartInfo
        {
            FileName = WslExe,
            Arguments = $"-- {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        return proc.ExitCode == 0 ? output : null;
    }

    /// <summary>Invalidates the cache of read WSL files — called after Save, or on force refresh.</summary>
    internal static void InvalidateWslCache()
    {
        lock (_wslCacheLock) { _wslFilesCache = null; }
    }

    // Known WSL distros — enumerating the UNC root (\\wsl.localhost\) doesn't work in .NET, but subpaths
    // like \\wsl.localhost\Ubuntu\home do. So we probe known names in a fixed order.
    private static readonly string[] KnownWslDistros =
    [
        "Ubuntu",
        "Ubuntu-24.04", "Ubuntu-22.04", "Ubuntu-20.04",
        "Debian",
        "kali-linux",
        "openSUSE-Leap-15.5", "openSUSE-Tumbleweed",
        "Fedora",
        "Alpine",
        "Arch",
    ];

    /// <summary>Tries to read WSL credential files directly via the \\wsl.localhost\ share
    /// (without wsl.exe). Probes known distros, enumerates /home/* in each and tries the target files.</summary>
    private static bool TryReadViaWslFs(string[] paths, Dictionary<string, string?> result)
    {
        bool anyRead = false;
        foreach (var root in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            foreach (var distro in KnownWslDistros)
            {
                var homeDir = root + distro + @"\home";

                string[] userHomes;
                try { userHomes = Directory.GetDirectories(homeDir); }
                catch { continue; } // distro doesn't exist or home isn't accessible

                foreach (var userHome in userHomes)
                {
                    foreach (var path in paths)
                    {
                        if (result[path] != null) continue;
                        if (!path.StartsWith("~/")) continue;
                        var subPath = path.Substring(2).Replace('/', '\\');
                        var fullPath = Path.Combine(userHome, subPath);
                        try
                        {
                            if (!File.Exists(fullPath)) continue;
                            result[path] = File.ReadAllText(fullPath);
                            anyRead = true;
                        }
                        catch { /* ignore — try the next user/distro */ }
                    }
                }

                if (anyRead && result.Values.All(v => v != null)) return true;
            }
        }
        return anyRead;
    }

    // Known credential paths are read in a single wsl.exe call — a wsl.exe cold start is expensive (1-5s),
    // without batching it would be multiplied by the number of files. The cache is per-process, invalidated only by a restart.
    private static readonly string[] WslCredentialPaths =
    [
        "~/.claude/.credentials.json",
        "~/.codex/auth.json",
        "~/.hermes/auth.json",
    ];

    private static Dictionary<string, string?>? _wslFilesCache;
    private static readonly object _wslCacheLock = new();

    private static string? ReadWslFile(string path)
    {
        lock (_wslCacheLock)
        {
            if (_wslFilesCache == null)
                _wslFilesCache = ReadWslFilesBatch(WslCredentialPaths);
            return _wslFilesCache.TryGetValue(path, out var content) ? content : null;
        }
    }

    private static Dictionary<string, string?> ReadWslFilesBatch(string[] paths)
    {
        var result = new Dictionary<string, string?>();
        foreach (var p in paths) result[p] = null;

        // 1) Direct filesystem via \\wsl$\ (fast, non-blocking, doesn't use vsock interop).
        if (TryReadViaWslFs(paths, result)) return result;

        if (!File.Exists(WslExe)) return result;

        const string marker = "__CRED_MARKER_8f3d2c1e__";
        // Each cat is always followed by a marker — a missing file yields an empty chunk.
        var script = string.Join("; ", paths.Select(p => $"cat {p} 2>/dev/null; echo {marker}"));

        var psi = new ProcessStartInfo
        {
            FileName = WslExe,
            Arguments = $"-- bash -c \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return result;

            // Hard timeout — wsl.exe can hang (vsock errors, NTFS lock); ReadToEnd would otherwise
            // block forever. We read stdout asynchronously and kill the whole process after 5s.
            var readTask = proc.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(TimeSpan.FromSeconds(5)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return result;
            }
            var output = readTask.Result;
            if (!proc.WaitForExit(1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            var chunks = output.Split(new[] { marker }, StringSplitOptions.None);
            for (int i = 0; i < paths.Length && i < chunks.Length; i++)
            {
                var content = chunks[i].Trim();
                result[paths[i]] = string.IsNullOrEmpty(content) ? null : content;
            }
        }
        catch { }

        return result;
    }

    private static OAuthCredential? ParseCredentialJson(string json, string sourcePath)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            var oauth = doc?["claudeAiOauth"];
            if (oauth == null) return null;

            var accessToken = oauth["accessToken"]?.GetValue<string>();
            var refreshToken = oauth["refreshToken"]?.GetValue<string>();
            var expiresAt = oauth["expiresAt"]?.GetValue<long>() ?? 0;
            var scopes = (oauth["scopes"] as JsonArray)?
                .Select(s => s?.GetValue<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToArray() ?? [];

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                return null;

            return new OAuthCredential
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                Scopes = scopes,
                SourcePath = sourcePath
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Single entry point for loading all accounts. Walks <see cref="AllSources"/>
    /// in a fixed order and dedups per account. "First working wins": valid tokens are taken first;
    /// an expired Claude token is kept only as a fallback, because ClaudeApiClient can refresh it.</summary>
    public static List<AccountInfo> LoadAllAccounts()
    {
        var candidates = new List<(CredSource Source, OAuthCredential Cred)>();
        foreach (var source in AllSources)
        {
            var read = ReadSource(source);
            if (read == null) continue;

            var cred = source.Parse(read.Value.Json);
            if (cred == null || string.IsNullOrEmpty(cred.AccessToken)) continue;
            cred.SourcePath = read.Value.SourcePath;
            candidates.Add((source, cred));
        }

        var result = new List<AccountInfo>();
        var seen = new HashSet<string>();

        // Pass 1: valid tokens. Pass 2: expired Claude tokens that still have a refresh token.
        foreach (var expiredPass in new[] { false, true })
        {
            foreach (var (source, cred) in candidates)
            {
                if (cred.IsExpired != expiredPass) continue;
                if (expiredPass && (source.Service != ServiceType.Claude || string.IsNullOrEmpty(cred.RefreshToken)))
                    continue;

                var key = MakeDedupKey(source.Service, cred);
                if (key == null) continue;
                if (!seen.Add(key)) continue;

                result.Add(new AccountInfo(source.Service, cred, source.Label));
            }
        }

        return result;
    }

    private static OAuthCredential? ParseHermesCredentialJson(string json, string sourcePath)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc == null) return null;

            // Hermes stores tokens in providers.openai-codex.tokens (active_provider says which one is active)
            var providers = doc["providers"] as JsonObject;
            if (providers == null) return null;

            var activeProvider = doc["active_provider"]?.GetValue<string>() ?? "openai-codex";
            var providerNode = providers[activeProvider] ?? providers["openai-codex"];
            var tokens = providerNode?["tokens"];

            var token = tokens?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) return null;

            var refreshToken = tokens?["refresh_token"]?.GetValue<string>() ?? "";
            var expiresAt = GetJwtExpiryMs(token);

            return new OAuthCredential
            {
                AccessToken = token,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                SourcePath = sourcePath
            };
        }
        catch
        {
            return null;
        }
    }

    private static OAuthCredential? ParseCodexCredentialJson(string json, string sourcePath)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc == null) return null;

            // Codex auth.json nests tokens under "tokens" object
            var tokensNode = doc["tokens"];
            var tokenNode = doc["access_token"] ?? doc["accessToken"] ?? doc["token"]
                ?? tokensNode?["access_token"];
            var token = tokenNode?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) return null;

            var refreshNode = doc["refresh_token"] ?? doc["refreshToken"]
                ?? tokensNode?["refresh_token"];
            var refreshToken = refreshNode?.GetValue<string>() ?? "";

            // Try to get expiry from JWT payload (exp field, seconds → ms)
            var expiresAt = GetJwtExpiryMs(token);

            return new OAuthCredential
            {
                AccessToken = token,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                SourcePath = sourcePath
            };
        }
        catch
        {
            return null;
        }
    }

    private static long GetJwtExpiryMs(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return long.MaxValue;
            var payload = parts[1];
            var padded = payload.PadRight((payload.Length + 3) / 4 * 4, '=')
                                .Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(padded);
            var doc = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var exp = doc?["exp"]?.GetValue<long>() ?? 0;
            return exp > 0 ? exp * 1000L : long.MaxValue;
        }
        catch { return long.MaxValue; }
    }

    private static string? GetJwtClaim(string jwt, string claimName)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            var padded = payload.PadRight((payload.Length + 3) / 4 * 4, '=')
                                .Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(padded);
            var doc = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            return doc?[claimName]?.GetValue<string>();
        }
        catch { return null; }
    }

    internal static string? GetAccountKey(string token, ServiceType service)
    {
        if (service == ServiceType.Claude)
        {
            // Try org_id, fall back to sub (standard JWT claim)
            var orgId = GetJwtClaim(token, "org_id")
                     ?? GetJwtClaim(token, "organization_id")
                     ?? GetJwtClaim(token, "sub");
            return orgId != null ? "claude:" + orgId : null;
        }
        else // Codex
        {
            var accountId = GetJwtClaim(token, "account_id")
                         ?? GetJwtClaim(token, "sub");
            return accountId != null ? "codex:" + accountId : null;
        }
    }

    internal static string? GetAccountKey(AccountInfo account)
    {
        var key = GetAccountKey(account.Credential.AccessToken, account.Service);
        if (key != null) return key;

        // Opaque token fallback (sk-ant-oat01-...) — no JWT payload to decode
        if (account.Service == ServiceType.Claude &&
            !string.IsNullOrEmpty(account.Credential.AccessToken))
        {
            var suffix = account.Credential.SourcePath.StartsWith("wsl:") ? "wsl" : "win";
            return "claude:" + suffix;
        }
        return null;
    }

    /// <summary>Re-reads the credential from its original path after a refresh failure (called by ClaudeApiClient).
    /// Covers the Claude format only — the refresh flow is Claude-specific.</summary>
    public static OAuthCredential? LoadCredentialFromPath(string sourcePath)
    {
        string? json;
        if (sourcePath.StartsWith("wsl:"))
        {
            InvalidateWslCache(); // we want fresh content, not the cache from startup
            json = ReadWslFile(sourcePath.Substring("wsl:".Length));
        }
        else
        {
            try { json = File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : null; }
            catch { return null; }
        }
        if (string.IsNullOrWhiteSpace(json)) return null;

        var cred = ParseCredentialJson(json, sourcePath);
        if (cred != null) cred.SourcePath = sourcePath;
        return cred;
    }
}
