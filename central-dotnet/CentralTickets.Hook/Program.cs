using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.Api;

JsonObject input;
try
{
    using var reader = new StreamReader(Console.OpenStandardInput());
    var text = await reader.ReadToEndAsync();
    if (text.Length > 2_000_000) throw new InvalidDataException("Evento muito grande.");
    input = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? new JsonObject();
}
catch (Exception error) when (error is JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"Central: {error.Message}");
    return 2;
}

var dataRoot = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA");
var ticketId = Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID");
if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(ticketId)) { Console.Write("{}"); return 0; }
try
{
    var output = await new HookProcessor(dataRoot).HandleAsync(input);
    Console.Write(output.ToJsonString());
    return 0;
}
catch (Exception error)
{
    if (input["hook_event_name"]?.GetValue<string>() == "PreToolUse")
    {
        var denied = new JsonObject { ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = $"Central: {error.Message}"
        } };
        Console.Write(denied.ToJsonString());
        return 0;
    }
    Console.Error.WriteLine($"Central: {error.Message}");
    return 2;
}
