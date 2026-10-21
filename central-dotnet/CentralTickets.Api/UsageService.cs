using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

/// <summary>Reads only locally exposed usage snapshots and Codex's read-only account protocol.</summary>
public sealed class UsageService(CentralProfile profile)
{
    private readonly CentralProfile _profile = profile;
    private readonly SemaphoreSlim _codexGate = new(1, 1);
    private JsonObject? _codexCache;
    private DateTimeOffset _codexCaptured;
    private bool _codexFailed;

    public JsonObject ReadClaude()
    {
        var configured = _profile.GetString("claude_usage_file");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = string.IsNullOrWhiteSpace(configured) ? new[] { Path.Combine(home, ".claude", "central-usage.json"), Path.Combine(home, ".claude", "side-notch-usage.json") } : [configured];
        var snapshots = new List<JsonObject>();
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > 32_768) continue;
                var content = File.ReadAllText(file);
                if (content.Length > 0 && content[0] == '\uFEFF') content = content[1..];
                var raw = JsonNode.Parse(content) as JsonObject;
                var snapshot = Normalize(raw, "claude", "claude_code_statusline", DateTimeOffset.UtcNow);
                if (snapshot is not null) snapshots.Add(snapshot);
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }
        }
        return snapshots.OrderByDescending(snapshot => GetTimestamp(snapshot, "capturedAt")).FirstOrDefault() ?? Unavailable("claude");
    }

    public async Task<JsonObject> ReadCodex(CancellationToken cancellationToken)
    {
        await _codexGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_codexCache is not null && now - _codexCaptured < TimeSpan.FromMinutes(1)) return SnapshotWithFreshness(_codexCache, _codexCaptured, now, _codexFailed);
            try
            {
                var executable = ResolveCodex();
                if (executable is null) throw new FileNotFoundException("Codex CLI não localizado.");
                var data = await QueryCodex(executable, cancellationToken);
                _codexCache = NormalizeCodex(data, now);
                _codexCaptured = now; _codexFailed = false;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                _codexFailed = true;
                if (_codexCache is null) return Unavailable("codex", "codex_app_server", true);
            }
            return SnapshotWithFreshness(_codexCache!, _codexCaptured, DateTimeOffset.UtcNow, _codexFailed);
        }
        finally { _codexGate.Release(); }
    }

    private async Task<JsonObject> QueryCodex(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("app-server");
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o Codex app-server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var killOnTimeout = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var results = new JsonObject();
        await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"central_tickets_usage\",\"title\":\"Central Tickets Usage\",\"version\":\"1.0.0\"}}}".AsMemory(), timeout.Token);
        var initialized = false; var received = 0;
        while (!timeout.IsCancellationRequested && received < 2)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) break;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            if (message is null) continue;
            if (Number(message, "id") == 1 && !initialized)
            {
                if (message["error"] is not null) throw new InvalidOperationException("Codex inicialização recusada.");
                initialized = true;
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), timeout.Token);
                await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}".AsMemory(), timeout.Token);
                await process.StandardInput.WriteLineAsync("{\"id\":3,\"method\":\"account/usage/read\"}".AsMemory(), timeout.Token);
            }
            else if (initialized && Number(message, "id") is 2 or 3)
            {
                var key = Number(message, "id") == 2 ? "limits" : "tokens";
                results[key] = message["error"] is null ? message["result"]?.DeepClone() : null;
                received++;
            }
        }
        if (received != 2) throw new IOException("Codex app-server não respondeu aos dados de uso dentro do limite.");
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        return results;
    }

    private string? ResolveCodex()
    {
        var configured = _profile.GetString("codex_usage_executable");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var codex = _profile.GetString("codex");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(codex) && codex.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) candidates.Add(codex);
        candidates.Add(Path.Combine(home, ".local", "bin", "codex.exe"));
        var npm = string.IsNullOrWhiteSpace(codex) ? Path.Combine(home, "AppData", "Roaming", "npm") : Path.GetDirectoryName(codex)!;
        foreach (var arch in new[] { ("x64", "x86_64-pc-windows-msvc"), ("arm64", "aarch64-pc-windows-msvc") })
            candidates.Add(Path.Combine(npm, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{arch.Item1}", "vendor", arch.Item2, "bin", "codex.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static JsonObject? Normalize(JsonObject? raw, string provider, string source, DateTimeOffset now)
    {
        var capturedMs = Number(raw, "capturedAt");
        if (capturedMs <= 0 || capturedMs > now.ToUnixTimeMilliseconds() + 60_000) return null;
        var captured = DateTimeOffset.FromUnixTimeMilliseconds((long)capturedMs);
        var limits = raw?["rateLimits"] as JsonObject;
        return new JsonObject { ["provider"] = provider, ["source"] = source, ["capturedAt"] = captured.ToString("O"), ["ageSeconds"] = Math.Max(0, (int)(now - captured).TotalSeconds), ["staleAfterSeconds"] = 300,
            ["windows"] = new JsonObject { ["fiveHour"] = Window(limits?["fiveHour"] as JsonObject, captured, now), ["sevenDay"] = Window(limits?["sevenDay"] as JsonObject, captured, now) } };
    }

    private static JsonObject? NormalizeCodex(JsonObject response, DateTimeOffset now)
    {
        var grouped = response["limits"]?["rateLimitsByLimitId"] as JsonObject;
        var all = grouped is not null
            ? response["limits"]?["rateLimitsByLimitId"]?["codex"]
            : response["limits"]?["rateLimits"];
        if (grouped is null && response["limits"]?["limitId"]?.GetValue<string>() is { } limitId && limitId != "codex") all = null;
        var primary = all?["primary"] as JsonObject; var secondary = all?["secondary"] as JsonObject;
        var five = new[] { primary, secondary }.FirstOrDefault(value => Number(value, "windowDurationMins") == 300);
        var seven = new[] { primary, secondary }.FirstOrDefault(value => Number(value, "windowDurationMins") == 10080);
        return new JsonObject { ["provider"] = "codex", ["source"] = "codex_app_server", ["capturedAt"] = now.ToString("O"), ["ageSeconds"] = 0, ["staleAfterSeconds"] = 300,
            ["windows"] = new JsonObject { ["fiveHour"] = CodexWindow(five, now, now), ["sevenDay"] = CodexWindow(seven, now, now) },
            ["tokens"] = new JsonObject { ["lifetime"] = ValidTokens(response["tokens"]?["summary"]?["lifetimeTokens"]), ["daily"] = Daily(response["tokens"]?["dailyUsageBuckets"] as JsonArray), ["capturedAt"] = now.ToString("O") } };
    }

    private static JsonObject SnapshotWithFreshness(JsonObject cache, DateTimeOffset captured, DateTimeOffset now, bool failed)
    {
        var copy = (JsonObject)cache.DeepClone(); copy["ageSeconds"] = Math.Max(0, (int)(now - captured).TotalSeconds);
        if (failed) { copy["refreshFailed"] = true; foreach (var window in (copy["windows"] as JsonObject ?? new JsonObject()).Select(pair => pair.Value as JsonObject).Where(value => value is not null)) if (window!["status"]?.GetValue<string>() == "current") window["status"] = "stale"; }
        return copy;
    }

    private static JsonObject CodexWindow(JsonObject? value, DateTimeOffset captured, DateTimeOffset now)
    {
        var percent = Number(value, "usedPercent");
        var reset = Number(value, "resetsAt");
        if (value is null || percent is < 0 or > 100 || reset <= captured.ToUnixTimeSeconds() || reset > 253_402_300_799) return UnavailableWindow();
        var resetAt = DateTimeOffset.FromUnixTimeSeconds((long)reset);
        return new JsonObject { ["status"] = resetAt <= now ? "expired" : "current", ["used"] = percent, ["remaining"] = Math.Round(100 - percent, 8), ["resetsAt"] = resetAt.ToString("O") };
    }

    private static JsonObject Window(JsonObject? value, DateTimeOffset captured, DateTimeOffset now)
    {
        var percent = Number(value, "usedPercentage"); var reset = Number(value, "resetsAt");
        if (value is null || percent is < 0 or > 100 || reset <= captured.ToUnixTimeSeconds() || reset > 253_402_300_799) return UnavailableWindow();
        var resetAt = DateTimeOffset.FromUnixTimeSeconds((long)reset); var age = now - captured;
        return new JsonObject { ["status"] = resetAt <= now ? "expired" : age > TimeSpan.FromMinutes(5) ? "stale" : "current", ["used"] = percent, ["remaining"] = Math.Round(100 - percent, 8), ["resetsAt"] = resetAt.ToString("O") };
    }

    private static JsonObject UnavailableWindow() => new() { ["status"] = "unavailable", ["used"] = null, ["remaining"] = null, ["resetsAt"] = null };
    private static JsonObject Unavailable(string provider, string? source = null, bool failed = false) => new() { ["provider"] = provider, ["source"] = source, ["capturedAt"] = null, ["windows"] = new JsonObject(), ["unavailable"] = true, ["refreshFailed"] = failed };
    private static JsonNode? ValidTokens(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) && number is >= 0 and <= 9_007_199_254_740_991 ? JsonValue.Create(number) : null;
    private static JsonArray? Daily(JsonArray? source)
    {
        if (source is null) return null;
        var rows = new List<(string Date, long Tokens)>();
        foreach (var item in source.OfType<JsonObject>())
        {
            var date = item["startDate"]?.GetValue<string>(); var tokens = ValidTokens(item["tokens"]);
            if (date is null || !Regex.IsMatch(date, "^\\d{4}-\\d{2}-\\d{2}$") || !DateOnly.TryParse(date, out _) || tokens is null) continue;
            rows.Add((date, tokens.GetValue<long>()));
        }
        var duplicate = rows.GroupBy(row => row.Date).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return new JsonArray(rows.Where(row => !duplicate.Contains(row.Date)).OrderBy(row => row.Date).Select(row => (JsonNode?)new JsonObject { ["date"] = row.Date, ["tokens"] = row.Tokens }).ToArray());
    }
    private static double Number(JsonObject? value, string key) => value?[key] is JsonValue item && item.TryGetValue<double>(out var number) ? number : value?[key] is JsonValue integer && integer.TryGetValue<long>(out var whole) ? whole : 0;
    private static DateTimeOffset GetTimestamp(JsonObject value, string key) => DateTimeOffset.TryParse(value[key]?.GetValue<string>(), out var timestamp) ? timestamp : DateTimeOffset.MinValue;
}
