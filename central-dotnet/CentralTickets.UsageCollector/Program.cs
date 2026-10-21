using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.UsageCollector;

try
{
    using var input = new StreamReader(Console.OpenStandardInput());
    var text = await input.ReadToEndAsync();
    if (System.Text.Encoding.UTF8.GetByteCount(text) > 1_000_000) return 0;
    var source = JsonNode.Parse(text) as JsonObject;
    if (source is null) return 0;
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var destination = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
        ? Path.GetFullPath(args[0])
        : Path.Combine(home, ".claude", "central-usage.json");
    var display = UsageCapture.Capture(source, destination, DateTimeOffset.UtcNow);
    if (display is not null) Console.Write(display);
}
catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    // A usage snapshot is optional and must never interrupt the agent status line.
}
return 0;
