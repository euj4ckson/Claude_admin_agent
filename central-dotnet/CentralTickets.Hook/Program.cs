using System.Text.Json;
using System.Text.Json.Nodes;
using CentralTickets.Api;

if (args.Length == 4 && args[0] == "--managed-session-event")
{
    var root = Environment.GetEnvironmentVariable("CENTRAL_TICKETS_DATA");
    if (string.IsNullOrWhiteSpace(root)) { Console.Error.WriteLine("Central: diretÃ³rio local de dados ausente."); return 2; }
    try
    {
        await new HookProcessor(root).HandleCodexSessionEventAsync(args[1], args[2], args[3]);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Central: {error.Message}");
        return 2;
    }
}

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
