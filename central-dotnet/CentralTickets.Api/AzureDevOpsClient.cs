using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CentralTickets.Api;

public sealed partial class AzureDevOpsClient
{
    private const int MaxAttachmentBytes = 50 * 1024 * 1024;
    private const int MaxTotalAttachmentBytes = 200 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, CachedPreview> _previews = new();
    private readonly string _dataRoot;
    private readonly string _referenceRoot;
    private readonly CentralProfile _profile;
    private readonly TicketWriter _tickets;
    private readonly IHttpClientFactory _clients;
    private readonly Func<CancellationToken, Task<string>> _patProvider;

    public AzureDevOpsClient(string dataRoot, string referenceRoot, CentralProfile profile, TicketWriter tickets, IHttpClientFactory clients,
        Func<CancellationToken, Task<string>>? patProvider = null)
    {
        _dataRoot = dataRoot;
        _referenceRoot = referenceRoot;
        _profile = profile;
        _tickets = tickets;
        _clients = clients;
        _patProvider = patProvider ?? ReadPatFromStorageAsync;
    }

    public object Status() => new
    {
        configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT"))
            || File.Exists(Path.Combine(_dataRoot, "azure-credentials.json")),
        organization = Config("organization") ?? "sistemasunion",
        project = Config("project") ?? "SSUnion",
        readOnly = true
    };

    public async Task<object> Configure(string? pat, CancellationToken cancellationToken)
    {
        var value = (pat ?? "").Trim();
        if (value.Length is < 20 or > 500 || value.Any(char.IsControl))
            throw new TicketOperationException("Informe um PAT válido, sem quebras de linha.");
        if (!OperatingSystem.IsWindows())
            throw new TicketOperationException("A proteção local do PAT exige Windows. Configure AZURE_DEVOPS_PAT no ambiente.", 409);
        var encrypted = Dpapi.Protect(Encoding.UTF8.GetBytes(value));
        Directory.CreateDirectory(_dataRoot);
        var path = Path.Combine(_dataRoot, "azure-credentials.json");
        await AtomicWriteAsync(path, JsonSerializer.Serialize(new { version = 1, provider = "windows-dpapi", pat = Convert.ToBase64String(encrypted) }, JsonOptions), cancellationToken);
        return new { configured = true, message = "Acesso Azure salvo com proteção do Windows (somente leitura)." };
    }

    public async Task<AzurePreviewResponse> Preview(string? reference, CancellationToken cancellationToken)
    {
        var parsed = ParseReference(reference);
        var pat = await _patProvider(cancellationToken);
        var preview = await FetchWorkItem(parsed, pat, cancellationToken);
        PrunePreviews();
        var id = Guid.NewGuid().ToString("N");
        _previews[id] = new CachedPreview(parsed.Id, preview, DateTimeOffset.UtcNow.AddMinutes(5));
        return preview with { PreviewId = id };
    }

    public async Task<AzureImportResponse> Import(AzureImportRequest input, CancellationToken cancellationToken)
    {
        var parsed = ParseReference(input.Reference);
        AzurePreviewResponse preview;
        string? pat = null;
        if (!string.IsNullOrWhiteSpace(input.PreviewId))
        {
            if (!_previews.TryGetValue(input.PreviewId, out var cached) || cached.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _previews.TryRemove(input.PreviewId, out _);
                throw new TicketOperationException("A prévia expirou. Consulte o work item novamente antes de cadastrar.", 409);
            }
            if (cached.WorkItemId != parsed.Id) throw new TicketOperationException("A prévia não corresponde ao work item informado.", 409);
            preview = cached.Preview;
        }
        else
        {
            pat = await _patProvider(cancellationToken);
            preview = await FetchWorkItem(parsed, pat, cancellationToken);
        }

        var ticket = string.IsNullOrWhiteSpace(input.Ticket) ? preview.Suggested.Ticket : input.Ticket.Trim();
        var sprint = string.IsNullOrWhiteSpace(input.Sprint) ? preview.Suggested.Sprint : input.Sprint.Trim();
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(sprint))
            throw new TicketOperationException("Informe o código do ticket e a sprint antes de importar.");

        var selectedNames = input.Attachments is null
            ? preview.Attachments.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : input.Attachments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = input.DownloadAttachments == false
            ? []
            : preview.Attachments.Where(file => selectedNames.Contains(file.Name)).ToArray();
        if (selected.Length > 40) throw new TicketOperationException("Selecione no máximo 40 anexos.");
        if (selected.Any(file => file.Size > MaxAttachmentBytes)) throw new TicketOperationException("Um ou mais anexos ultrapassam 50 MB.");
        if (selected.Sum(file => file.Size ?? 0) > MaxTotalAttachmentBytes) throw new TicketOperationException("O limite total de 200 MB de anexos foi atingido.");
        if (selected.Length > 0) pat ??= await _patProvider(cancellationToken);

        var repositoryId = input.Repository ?? preview.Suggested.RepositoryId ?? Path.GetFileName(_profile.GetString("repository") ?? "");
        var delivery = input.Delivery ?? "local_commit_only";
        var branchType = input.BranchType ?? preview.Suggested.BranchType;
        var release = input.Release;
        var scope = input.Scope ?? preview.Suggested.Scope;
        var title = input.Title ?? preview.Suggested.Title;
        var created = _tickets.Create(new CreateTicketRequest(
            ticket, sprint, release, scope, title, delivery, branchType, input.Agent ?? "claude",
            repositoryId, null, selected.Select(file => file.Name).ToArray(), input.Components,
            JsonSerializer.SerializeToElement(preview.Source, JsonOptions)));
        var canonicalTicketId = created.GetProperty("id").GetString()!;

        var warnings = preview.Warnings.ToList();
        if (preview.Suggested.Ticket is null && !string.IsNullOrWhiteSpace(ticket)) warnings.RemoveAll(message => message.Contains("código do ticket", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(preview.Suggested.Sprint) && !string.IsNullOrWhiteSpace(sprint)) warnings.RemoveAll(message => message.Contains("sprint", StringComparison.OrdinalIgnoreCase));
        var targetDirectory = Path.Combine(_referenceRoot, canonicalTicketId.ToLowerInvariant());
        Directory.CreateDirectory(targetDirectory);
        long totalBytes = 0;
        var downloaded = 0;
        foreach (var attachment in selected)
        {
            try
            {
                var content = await DownloadAttachmentAsync(attachment, pat!, cancellationToken);
                totalBytes += content.Length;
                if (totalBytes > MaxTotalAttachmentBytes) throw new TicketOperationException("O limite total de 200 MB de anexos foi atingido.");
                var target = Path.GetFullPath(Path.Combine(targetDirectory, attachment.Name));
                if (!target.StartsWith(Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new TicketOperationException("Anexo fora da pasta de referência permitida.");
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await output.WriteAsync(content, cancellationToken);
                downloaded++;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TicketOperationException or OperationCanceledException)
            {
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                warnings.Add($"{attachment.Name}: {error.Message}");
            }
        }
        if (!string.IsNullOrWhiteSpace(input.PreviewId)) _previews.TryRemove(input.PreviewId, out _);
        var eventFile = Path.Combine(_dataRoot, "tickets", canonicalTicketId.ToLowerInvariant(), "events.jsonl");
        var eventJson = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.ToString("O"), type = "azure", message = $"Importado do Azure work item {preview.Source.Id}; {downloaded}/{selected.Length} anexo(s) baixado(s)." }, JsonOptions);
        await File.AppendAllTextAsync(eventFile, eventJson + Environment.NewLine, cancellationToken);
        return new AzureImportResponse(created, preview, downloaded, warnings);
    }

    private async Task<AzurePreviewResponse> FetchWorkItem(ParsedAzureReference parsed, string pat, CancellationToken cancellationToken)
    {
        var organization = Config("organization") ?? "sistemasunion";
        var project = Config("project") ?? "SSUnion";
        var projectPath = Uri.EscapeDataString(project);
        var endpoint = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{projectPath}/_apis/wit/workitems/{parsed.Id}?$expand=relations&api-version=7.1";
        using var response = await SendAzureAsync(endpoint, pat, "application/json", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var fields = root.TryGetProperty("fields", out var fieldsValue) ? fieldsValue : default;
        var title = Field(fields, "System.Title")?.Trim() ?? "";
        var description = HtmlToText(Field(fields, "System.Description") ?? Field(fields, "Microsoft.VSTS.Common.Description") ?? "");
        var tags = Field(fields, "System.Tags") ?? "";
        var ticket = TicketRegex().Match($"{title} {tags} {description}");
        var sprint = Field(fields, "Custom.Sprint") ?? Field(fields, "Custom.SprintNumber") ?? FindSprint(fields);
        var attachments = new List<AzureAttachment>();
        if (root.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var relation in relations.EnumerateArray())
            {
                var rel = Field(relation, "rel") ?? "";
                var url = Field(relation, "url") ?? "";
                if (!rel.Equals("attachedfile", StringComparison.OrdinalIgnoreCase) && !url.Contains("_apis/wit/attachments/", StringComparison.OrdinalIgnoreCase)) continue;
                var attrs = relation.TryGetProperty("attributes", out var attributeValue) ? attributeValue : default;
                var name = SafeFileName(Field(attrs, "name") ?? Field(attrs, "title") ?? $"anexo-azure-{++index}.bin");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var attachmentUri) || attachmentUri.Scheme != Uri.UriSchemeHttps || !attachmentUri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)) continue;
                attachments.Add(new AzureAttachment(name, url, LongField(attrs, "resourceSize"), Field(attrs, "comment")));
            }
        }
        var warnings = new List<string>();
        if (!ticket.Success) warnings.Add("Não encontrei o código do ticket no título, tags ou descrição; informe-o manualmente.");
        if (string.IsNullOrWhiteSpace(sprint) || !Regex.IsMatch(sprint, "^\\d{1,6}$")) warnings.Add("Não encontrei uma sprint numérica; informe-a manualmente.");
        var scope = string.IsNullOrWhiteSpace(description) ? title : description;
        if (scope.Length < 20) warnings.Add("A descrição importada é curta; revise o escopo antes de cadastrar.");
        var suggested = ticket.Success ? ticket.Value.ToUpperInvariant() : null;
        var source = new AzureWorkItemSource(parsed.Id, parsed.Url, organization, project, DateTimeOffset.UtcNow.ToString("O"));
        return new AzurePreviewResponse(null, source,
            new AzureWorkItemFields(title, description, Field(fields, "System.State") ?? "", tags, Field(fields, "System.IterationPath") ?? "", Field(fields, "System.AreaPath") ?? ""),
            new AzureTicketSuggestion(suggested, title[..Math.Min(160, title.Length)], scope, sprint ?? "", RepositorySuggestion(suggested), "", Regex.IsMatch(tags, "^hotfix|bug", RegexOptions.IgnoreCase) ? "hotfix" : "feature"),
            attachments, warnings);
    }

    private async Task<byte[]> DownloadAttachmentAsync(AzureAttachment attachment, string pat, CancellationToken cancellationToken)
    {
        using var response = await SendAzureAsync(attachment.Url, pat, "*/*", cancellationToken);
        if (response.Content.Headers.ContentLength > MaxAttachmentBytes) throw new TicketOperationException($"O anexo {attachment.Name} ultrapassa 50 MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaxAttachmentBytes) throw new TicketOperationException($"O anexo {attachment.Name} ultrapassa 50 MB.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return memory.ToArray();
    }

    private async Task<HttpResponseMessage> SendAzureAsync(string url, string pat, string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        HttpResponseMessage response;
        try { response = await _clients.CreateClient("azure").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TicketOperationException("A consulta ao Azure excedeu o tempo limite.", 504); }
        catch (HttpRequestException) { throw new TicketOperationException("Não foi possível conectar ao Azure DevOps.", 502); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        var message = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "O PAT do Azure foi recusado ou não tem permissão de leitura."
            : status == HttpStatusCode.NotFound ? "Work item ou anexo não encontrado no Azure."
            : $"O Azure retornou HTTP {(int)status}.";
        throw new TicketOperationException(message, status == HttpStatusCode.NotFound ? 404 : 409);
    }

    private async Task<string> ReadPatFromStorageAsync(CancellationToken cancellationToken)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT")?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        try
        {
            var file = Path.Combine(_dataRoot, "azure-credentials.json");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            if (!OperatingSystem.IsWindows() || saved.RootElement.GetProperty("provider").GetString() != "windows-dpapi") throw new TicketOperationException("O PAT salvo não pode ser descriptografado neste Windows. Configure novamente.", 409);
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(saved.RootElement.GetProperty("pat").GetString() ?? "")));
        }
        catch (FileNotFoundException) { throw new TicketOperationException("Configure um PAT do Azure com permissão somente de leitura antes de importar.", 409); }
        catch (DirectoryNotFoundException) { throw new TicketOperationException("Configure um PAT do Azure com permissão somente de leitura antes de importar.", 409); }
        catch (JsonException) { throw new TicketOperationException("O arquivo protegido do PAT está inválido. Configure novamente.", 409); }
        catch (CryptographicException) { throw new TicketOperationException("Não foi possível descriptografar o PAT salvo. Configure novamente.", 409); }
    }

    private ParsedAzureReference ParseReference(string? value)
    {
        var raw = (value ?? "").Trim();
        if (Regex.IsMatch(raw, "^\\d{1,10}$")) return MakeReference(long.Parse(raw));
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new TicketOperationException("Use um link HTTPS de dev.azure.com.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var organization = Config("organization") ?? "sistemasunion";
        var project = Config("project") ?? "SSUnion";
        if (segments.Length < 2 || !segments[0].Equals(organization, StringComparison.OrdinalIgnoreCase) || !segments[1].Equals(project, StringComparison.OrdinalIgnoreCase))
            throw new TicketOperationException($"O link deve pertencer ao projeto {organization}/{project}.");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var workItem = query["workitem"] ?? query["workItem"];
        if (string.IsNullOrWhiteSpace(workItem))
        {
            var match = Regex.Match(uri.AbsolutePath, @"(?:_workitems/edit|workitems/edit)/(\d+)", RegexOptions.IgnoreCase);
            workItem = match.Success ? match.Groups[1].Value : null;
        }
        if (!long.TryParse(workItem, out var id) || id <= 0 || id > 9_999_999_999) throw new TicketOperationException("Não encontrei um ID de work item nesse link.");
        return MakeReference(id);
    }

    private ParsedAzureReference MakeReference(long id)
    {
        var organization = Config("organization") ?? "sistemasunion";
        var project = Config("project") ?? "SSUnion";
        return new ParsedAzureReference(id, $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}");
    }

    private string? RepositorySuggestion(string? ticket)
    {
        var prefix = Regex.Match(ticket ?? "", "^[A-Z]+", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        var target = prefix switch { "PDV" => "ss_pdv", "FVW" => "ss_Forca_de_Vendas_Web", "FVA" => "ss_fva", "FIS" => "ss_erp_tag1", "FAT" or "GER" or "ADM" or "CMX" => "ss_erp_tag1", _ => null };
        return target is null ? null : _profile.Repositories().FirstOrDefault(repository => repository.Id.Equals(target, StringComparison.OrdinalIgnoreCase))?.Id ?? target;
    }

    private string? Config(string property)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dataRoot, "config.json")));
            if (config.RootElement.TryGetProperty("azure", out var azure) && azure.TryGetProperty(property, out var value)) return value.GetString();
        }
        catch (IOException) { }
        catch (JsonException) { }
        return null;
    }

    private void PrunePreviews()
    {
        foreach (var item in _previews)
            if (item.Value.ExpiresAt <= DateTimeOffset.UtcNow) _previews.TryRemove(item.Key, out _);
        while (_previews.Count >= 100)
            if (!_previews.TryRemove(_previews.Keys.First(), out _)) break;
    }

    private static async Task AtomicWriteAsync(string path, string value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, value + Environment.NewLine, cancellationToken); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? LongField(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
    private static string FindSprint(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object) return "";
        foreach (var field in fields.EnumerateObject().Where(item => item.Name.Contains("sprint", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("iteration", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(field.Value.ToString(), @"\b(?:sprint|iteration)\s*[/_-]?\s*(\d{1,6})\b", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }
        return "";
    }
    private static string HtmlToText(string value)
    {
        var text = Regex.Replace(value, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<br\\s*/?>|</p\\s*>|</div\\s*>|</li\\s*>|</h[1-6]\\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li[^>]*>", "- ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", "")).Replace("\r", "");
        return string.Join('\n', text.Split('\n').Select(line => Regex.Replace(line, "[ \\t]+", " ").Trim()).Where((line, index) => line.Length > 0 || index > 0));
    }
    private static string SafeFileName(string value)
    {
        var name = value.Split(['/', '\\']).LastOrDefault() ?? "";
        name = Regex.Replace(name, "[<>:\"|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "anexo-azure.bin";
        return name[..Math.Min(name.Length, 180)];
    }
    [GeneratedRegex(@"\b([A-Z]{2,8}\d{2,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex TicketRegex();

    private sealed record CachedPreview(long WorkItemId, AzurePreviewResponse Preview, DateTimeOffset ExpiresAt);
}

public sealed record AzurePreviewResponse(string? PreviewId, AzureWorkItemSource Source, AzureWorkItemFields Fields, AzureTicketSuggestion Suggested, IReadOnlyList<AzureAttachment> Attachments, IReadOnlyList<string> Warnings);
public sealed record AzureWorkItemSource(long Id, string Url, string Organization, string Project, string ImportedAt);
public sealed record AzureWorkItemFields(string Title, string Description, string State, string Tags, string Iteration, string Area);
public sealed record AzureTicketSuggestion(string? Ticket, string Title, string Scope, string Sprint, string? RepositoryId, string Release, string BranchType);
public sealed record AzureAttachment(string Name, string Url, long? Size, string? Comment);
public sealed record AzureImportRequest(string? Reference, string? PreviewId, string? Ticket, string? Sprint, string? Release, string? Title, string? Scope, string? Repository, string? Agent, string? BranchType, string? Delivery, string[]? Attachments, bool? DownloadAttachments, JsonElement? Components);
public sealed record AzurePreviewRequest(string? Reference);
public sealed record AzurePatRequest(string? Pat);
public sealed record AzureImportResponse(JsonElement Ticket, AzurePreviewResponse Preview, int Downloaded, IReadOnlyList<string> Warnings);
internal sealed record ParsedAzureReference(long Id, string Url);

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] input) => Transform(input, protect: true);
    public static byte[] Unprotect(byte[] input) => Transform(input, protect: false);
    private static byte[] Transform(byte[] input, bool protect)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var source = new DataBlob { Length = input.Length, Data = handle.AddrOfPinnedObject() };
            var success = protect ? CryptProtectData(ref source, "Central Tickets Azure PAT", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
