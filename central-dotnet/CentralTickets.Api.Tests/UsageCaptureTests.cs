using System.Text.Json.Nodes;
using CentralTickets.UsageCollector;

namespace CentralTickets.Api.Tests;

public sealed class UsageCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-usage-capture-{Guid.NewGuid():N}");

    [Fact]
    public void Capture_writes_only_valid_windows_and_preserves_previous_file_when_source_is_invalid()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "central-usage.json");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source = JsonNode.Parse("""
            { "rate_limits": {
              "five_hour": { "used_percentage": 0, "resets_at": 1800003600 },
              "seven_day": { "used_percentage": 150, "resets_at": 1800600000 }
            } }
            """)!.AsObject();

        var display = UsageCapture.Capture(source, file, now);

        Assert.Equal("5h: 0% usado", display);
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(now.ToUnixTimeMilliseconds(), saved["capturedAt"]!.GetValue<long>());
        Assert.Equal(0, saved["rateLimits"]?["fiveHour"]?["usedPercentage"]!.GetValue<double>());
        Assert.Null(saved["rateLimits"]?["sevenDay"]);

        var invalid = JsonNode.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":101,\"resets_at\":1800003600}}}")!.AsObject();
        Assert.Null(UsageCapture.Capture(invalid, file, now.AddMinutes(1)));
        Assert.Equal(now.ToUnixTimeMilliseconds(), JsonNode.Parse(File.ReadAllText(file))!["capturedAt"]!.GetValue<long>());
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
