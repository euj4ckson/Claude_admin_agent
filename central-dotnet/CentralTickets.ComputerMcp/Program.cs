using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (!OperatingSystem.IsWindows()) return 3;
var evidenceRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CENTRAL_TICKET_EVIDENCE") ?? Path.Combine(Environment.CurrentDirectory, "scratch", "evidence"));
var ticket = Regex.Replace(Environment.GetEnvironmentVariable("CENTRAL_TICKET_ID") ?? "ticket", "[^A-Za-z0-9_-]", "_");
string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (line.Length > 2_000_000) continue;
    JsonObject? message;
    try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
    if (message is null || message["id"] is null) continue;
    var id = message["id"]!.DeepClone();
    try
    {
        var method = message["method"]?.GetValue<string>() ?? "";
        JsonNode result = method switch
        {
            "initialize" => new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "central-computer", ["version"] = "1.0.0" } },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
            "tools/call" => await Call(message["params"] as JsonObject),
            _ => throw new NotSupportedException("Método MCP não suportado.")
        };
        Reply(id, result);
    }
    catch (Exception error)
    {
        if (message["method"]?.GetValue<string>() == "tools/call") Reply(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = error.Message }), ["isError"] = true });
        else ReplyError(id, -32603, error.Message);
    }
}
return 0;

async Task<JsonObject> Call(JsonObject? args)
{
    var name = args?["name"]?.GetValue<string>() ?? "";
    var input = args?["arguments"] as JsonObject ?? new JsonObject();
    if (name == "computer_screenshot")
    {
        var scenario = SafePart(input["scenario"]?.GetValue<string>(), "cenario");
        var label = SafePart(input["label"]?.GetValue<string>(), "custom");
        if (label is not ("before" or "action" or "result" or "custom")) throw new InvalidOperationException("Rótulo de screenshot inválido.");
        Directory.CreateDirectory(evidenceRoot);
        var path = Path.Combine(evidenceRoot, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}-{ticket}-{scenario}-{label}.png");
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var script = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}')); Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; $bmp=[Drawing.Bitmap]::new($b.Width,$b.Height); $g=[Drawing.Graphics]::FromImage($bmp); try {{$g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size);$bmp.Save($p,[Drawing.Imaging.ImageFormat]::Png)}} finally {{$g.Dispose();$bmp.Dispose()}}; Write-Output $p";
        await RunPowerShell(script);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Evidência salva em {path}" });
        if (input["includeImage"]?.GetValue<bool>() != false) content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(path)), ["mimeType"] = "image/png" });
        return new JsonObject { ["content"] = content };
    }
    if (name == "computer_active_window")
    {
        var script = "Add-Type @'\nusing System;using System.Text;using System.Runtime.InteropServices;public static class CentralWindow{[DllImport(\"user32.dll\")]public static extern IntPtr GetForegroundWindow();[DllImport(\"user32.dll\",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);[DllImport(\"user32.dll\")]public static extern bool GetWindowRect(IntPtr h,out RECT r);public struct RECT{public int Left,Top,Right,Bottom;}}\n'@;$h=[CentralWindow]::GetForegroundWindow();$s=New-Object Text.StringBuilder 512;[void][CentralWindow]::GetWindowText($h,$s,$s.Capacity);$r=New-Object CentralWindow+RECT;[void][CentralWindow]::GetWindowRect($h,[ref]$r);[pscustomobject]@{title=$s.ToString();left=$r.Left;top=$r.Top;right=$r.Right;bottom=$r.Bottom}|ConvertTo-Json -Compress";
        var json = await RunPowerShell(script);
        return TextResult(json);
    }
    if (name == "computer_click")
    {
        var x = RequiredInt(input, "x"); var y = RequiredInt(input, "y");
        var activeJson = JsonNode.Parse((await RunPowerShell("Add-Type @'\nusing System;using System.Text;using System.Runtime.InteropServices;public static class CentralWindow{[DllImport(\"user32.dll\")]public static extern IntPtr GetForegroundWindow();[DllImport(\"user32.dll\",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);[DllImport(\"user32.dll\")]public static extern bool GetWindowRect(IntPtr h,out RECT r);public struct RECT{public int Left,Top,Right,Bottom;}}\n'@;$h=[CentralWindow]::GetForegroundWindow();$s=New-Object Text.StringBuilder 512;[void][CentralWindow]::GetWindowText($h,$s,$s.Capacity);$r=New-Object CentralWindow+RECT;[void][CentralWindow]::GetWindowRect($h,[ref]$r);[pscustomobject]@{title=$s.ToString();left=$r.Left;top=$r.Top;right=$r.Right;bottom=$r.Bottom}|ConvertTo-Json -Compress"))) as JsonObject ?? throw new InvalidOperationException("Janela ativa não identificada.");
        if (x < Int(activeJson, "left") || x > Int(activeJson, "right") || y < Int(activeJson, "top") || y > Int(activeJson, "bottom")) throw new InvalidOperationException("Coordenada fora da janela ativa.");
        var button = input["button"]?.GetValue<string>() ?? "left";
        if (button is not ("left" or "right" or "double")) throw new InvalidOperationException("Botão do mouse inválido.");
        var down = button == "right" ? "0x0008" : "0x0002"; var up = button == "right" ? "0x0010" : "0x0004"; var dbl = button == "double" ? ";Start-Sleep -Milliseconds 80;[NativeMouse]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero);[NativeMouse]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)" : "";
        await RunPowerShell($"Add-Type @'\nusing System;using System.Runtime.InteropServices;public static class NativeMouse{{[DllImport(\"user32.dll\")]public static extern bool SetCursorPos(int x,int y);[DllImport(\"user32.dll\")]public static extern void mouse_event(uint f,uint dx,uint dy,uint data,UIntPtr extra);}}\n'@;[NativeMouse]::SetCursorPos({x},{y});[NativeMouse]::mouse_event({down},0,0,0,[UIntPtr]::Zero);[NativeMouse]::mouse_event({up},0,0,0,[UIntPtr]::Zero){dbl}");
        return TextResult(JsonSerializer.Serialize(new { clicked = new { x, y }, window = activeJson["title"]?.GetValue<string>() }));
    }
    if (name == "computer_type")
    {
        var text = input["text"]?.GetValue<string>() ?? ""; if (text.Length is 0 or > 10_000) throw new InvalidOperationException("Texto deve ter entre 1 e 10.000 caracteres.");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        await RunPowerShell($"Add-Type -AssemblyName System.Windows.Forms;$t=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'));$had=[Windows.Forms.Clipboard]::ContainsText();$old=$null;if($had){{$old=[Windows.Forms.Clipboard]::GetText()}};try{{[Windows.Forms.Clipboard]::SetText($t);[Windows.Forms.SendKeys]::SendWait('^v');Start-Sleep -Milliseconds 120}}finally{{if($had){{[Windows.Forms.Clipboard]::SetText($old)}}else{{[Windows.Forms.Clipboard]::Clear()}}}}");
        return TextResult(JsonSerializer.Serialize(new { typed = text.Length }));
    }
    if (name == "computer_key")
    {
        var key = input["key"]?.GetValue<string>()?.ToUpperInvariant() ?? "";
        var keys = new Dictionary<string, string> { ["ENTER"] = "~", ["TAB"] = "{TAB}", ["SHIFT_TAB"] = "+{TAB}", ["ESC"] = "{ESC}", ["BACKSPACE"] = "{BACKSPACE}", ["DELETE"] = "{DELETE}", ["UP"] = "{UP}", ["DOWN"] = "{DOWN}", ["LEFT"] = "{LEFT}", ["RIGHT"] = "{RIGHT}", ["HOME"] = "{HOME}", ["END"] = "{END}", ["CTRL_A"] = "^a", ["CTRL_C"] = "^c", ["CTRL_V"] = "^v", ["F5"] = "{F5}", ["ALT_F4"] = "%{F4}" };
        if (!keys.TryGetValue(key, out var sequence)) throw new InvalidOperationException("Tecla não permitida.");
        await RunPowerShell($"Add-Type -AssemblyName System.Windows.Forms;[Windows.Forms.SendKeys]::SendWait('{sequence}')");
        return TextResult(JsonSerializer.Serialize(new { key }));
    }
    if (name == "computer_wait")
    {
        var milliseconds = Math.Clamp(RequiredInt(input, "milliseconds"), 100, 10_000); await Task.Delay(milliseconds); return TextResult("Aguardado.");
    }
    if (name == "computer_wait_for_title")
    {
        var contains = input["contains"]?.GetValue<string>()?.Trim() ?? "";
        if (contains.Length is 0 or > 120) throw new InvalidOperationException("Informe parte do título da janela (até 120 caracteres).");
        var timeout = Math.Clamp(RequiredInt(input, "timeoutMilliseconds"), 100, 20_000);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(contains));
        var script = $"Add-Type @'\nusing System;using System.Text;using System.Runtime.InteropServices;public static class CentralWaitWindow{{[DllImport(\"user32.dll\")]public static extern IntPtr GetForegroundWindow();[DllImport(\"user32.dll\",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);}}\n'@;$needle=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}'));$clock=[Diagnostics.Stopwatch]::StartNew();do{{$title=New-Object Text.StringBuilder 512;[void][CentralWaitWindow]::GetWindowText([CentralWaitWindow]::GetForegroundWindow(),$title,$title.Capacity);if($title.ToString().IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0){{Write-Output $title.ToString();exit 0}};Start-Sleep -Milliseconds 300}}while($clock.ElapsedMilliseconds -lt {timeout});Write-Output '__CENTRAL_TIMEOUT__'";
        var title = await RunPowerShell(script);
        if (title == "__CENTRAL_TIMEOUT__") throw new InvalidOperationException("Janela esperada não apareceu no prazo.");
        return TextResult(JsonSerializer.Serialize(new { activeWindow = title }));
    }
    if (name == "computer_sequence")
    {
        var expectedTitle = input["expectedWindowTitle"]?.GetValue<string>()?.Trim() ?? "";
        var steps = input["steps"] as JsonArray;
        if (expectedTitle.Length is 0 or > 120 || steps is null || steps.Count is < 1 or > 10)
            throw new InvalidOperationException("Informe o título esperado e de 1 a 10 passos.");
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] is not JsonObject step
                || step["action"]?.GetValue<string>() is not ("computer_click" or "computer_key" or "computer_type" or "computer_wait")
                || step["arguments"] is not JsonObject)
                throw new InvalidOperationException($"Passo {index + 1} inválido; nada foi executado.");
        }
        var completed = 0;
        foreach (var node in steps)
        {
            if (node is not JsonObject step) throw new InvalidOperationException($"Passo {completed + 1} inválido.");
            var action = step["action"]?.GetValue<string>() ?? "";
            if (action is not ("computer_click" or "computer_key" or "computer_type" or "computer_wait"))
                throw new InvalidOperationException($"Ação do passo {completed + 1} não permitida.");
            var active = await Call(new JsonObject { ["name"] = "computer_active_window", ["arguments"] = new JsonObject() });
            var activeText = active["content"]?[0]?["text"]?.GetValue<string>() ?? "{}";
            var activeTitle = JsonNode.Parse(activeText)?["title"]?.GetValue<string>() ?? "";
            if (!activeTitle.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Sequência interrompida antes do passo {completed + 1}: a janela ativa mudou. {completed} passo(s) executado(s).");
            var arguments = step["arguments"] as JsonObject ?? new JsonObject();
            await Call(new JsonObject { ["name"] = action, ["arguments"] = arguments.DeepClone() });
            completed++;
        }
        return TextResult(JsonSerializer.Serialize(new { completed, window = expectedTitle }));
    }
    throw new InvalidOperationException("Ferramenta MCP desconhecida.");
}

async Task<string> RunPowerShell(string script)
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new IOException("PowerShell não iniciou.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var killOnTimeout = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
    var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
    await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(timeout.Token));
    var output = await outputTask; var error = await errorTask;
    if (process.ExitCode != 0) { var safeError = Regex.Replace(error, "\\s+", " ").Trim(); throw new IOException(safeError[..Math.Min(1000, safeError.Length)]); }
    return output.Trim();
}

static JsonArray ToolDefinitions() => JsonNode.Parse("""
[
 {"name":"computer_screenshot","description":"Captura a tela virtual e salva PNG na pasta de evidências do ticket. Use labels before/action/result/custom; não capture credenciais.","inputSchema":{"type":"object","properties":{"scenario":{"type":"string"},"label":{"type":"string","enum":["before","action","result","custom"]},"includeImage":{"type":"boolean"}},"required":["scenario","label"],"additionalProperties":false}},
 {"name":"computer_active_window","description":"Lê título e limites da janela em primeiro plano.","inputSchema":{"type":"object","properties":{},"additionalProperties":false}},
 {"name":"computer_click","description":"Clica somente dentro da janela ativa, em coordenadas vistas em screenshot.","inputSchema":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"button":{"type":"string","enum":["left","right","double"]}},"required":["x","y"],"additionalProperties":false}},
 {"name":"computer_type","description":"Digita texto não sensível por clipboard temporário.","inputSchema":{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":10000}},"required":["text"],"additionalProperties":false}},
 {"name":"computer_key","description":"Envia uma tecla permitida para a janela ativa.","inputSchema":{"type":"object","properties":{"key":{"type":"string","enum":["ENTER","TAB","SHIFT_TAB","ESC","BACKSPACE","DELETE","UP","DOWN","LEFT","RIGHT","HOME","END","CTRL_A","CTRL_C","CTRL_V","F5","ALT_F4"]}},"required":["key"],"additionalProperties":false}},
 {"name":"computer_wait","description":"Aguarda transição visual por no máximo 10 segundos.","inputSchema":{"type":"object","properties":{"milliseconds":{"type":"integer","minimum":100,"maximum":10000}},"required":["milliseconds"],"additionalProperties":false}},
 {"name":"computer_wait_for_title","description":"Aguarda até 20 segundos uma janela com o título esperado ficar ativa, sem ciclos de prints e esperas no chat.","inputSchema":{"type":"object","properties":{"contains":{"type":"string","minLength":1,"maxLength":120},"timeoutMilliseconds":{"type":"integer","minimum":100,"maximum":20000}},"required":["contains","timeoutMilliseconds"],"additionalProperties":false}},
 {"name":"computer_sequence","description":"Executa até 10 passos determinísticos na mesma janela ativa, conferindo seu título antes de cada passo; retorna apenas um resumo. Capture telas nos pontos de decisão.","inputSchema":{"type":"object","properties":{"expectedWindowTitle":{"type":"string","minLength":1,"maxLength":120},"steps":{"type":"array","minItems":1,"maxItems":10,"items":{"type":"object","properties":{"action":{"type":"string","enum":["computer_click","computer_key","computer_type","computer_wait"]},"arguments":{"type":"object"}},"required":["action","arguments"],"additionalProperties":false}}},"required":["expectedWindowTitle","steps"],"additionalProperties":false}}
]
""")!.AsArray();
static JsonObject TextResult(string text) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
static int RequiredInt(JsonObject? obj, string key) => obj?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : throw new InvalidOperationException($"Parâmetro {key} inválido.");
static int Int(JsonObject obj, string key) => obj[key]?.GetValue<int>() ?? 0;
void Reply(JsonNode id, JsonNode result) => Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
void ReplyError(JsonNode id, int code, string text) => Console.Out.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } }.ToJsonString());
static string SafePart(string? value, string fallback) { var clean = Regex.Replace((value ?? "").Trim(), "[^A-Za-z0-9._-]+", "_").Trim('-', '.', '_'); return clean.Length == 0 ? fallback : clean[..Math.Min(70, clean.Length)]; }
