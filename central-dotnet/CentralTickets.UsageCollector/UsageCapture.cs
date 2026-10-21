using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentralTickets.UsageCollector;

public static class UsageCapture
{
    public static string? Capture(JsonObject input, string destination, DateTimeOffset capturedAt)
    {
        var rates = input["rate_limits"] as JsonObject;
        if (rates is null) return null;
        var snapshot = new JsonObject { ["capturedAt"] = capturedAt.ToUnixTimeMilliseconds(), ["rateLimits"] = new JsonObject() };
        var display = new List<string>();
        foreach (var (inputName, outputName, label) in new[] { ("five_hour", "fiveHour", "5h"), ("seven_day", "sevenDay", "7d") })
        {
            var window = rates[inputName] as JsonObject;
            if (!TryNumber(window?["used_percentage"], out var used) || used is < 0 or > 100
                || !TryNumber(window?["resets_at"], out var reset) || reset <= capturedAt.ToUnixTimeSeconds()
                || reset > 253_402_300_799) continue;
            (snapshot["rateLimits"] as JsonObject)![outputName] = new JsonObject { ["usedPercentage"] = used, ["resetsAt"] = reset };
            display.Add($"{label}: {used.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}% usado");
        }
        if (display.Count == 0) return null;

        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, fullPath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
        return string.Join(" · ", display);
    }

    private static bool TryNumber(JsonNode? node, out double result)
    {
        result = 0;
        return node is JsonValue value && value.TryGetValue<double>(out result) && double.IsFinite(result);
    }
}
