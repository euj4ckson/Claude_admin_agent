using System.Net;
using System.Text.Json;

namespace CentralTickets.Api;

public static class TicketPageRenderer
{
    public static string Usage() => Layout("Uso dos agentes · Central de Tickets", """
        <header class="topbar"><a class="brand" href="/"><span class="brand-mark">CT</span><span>Central <small>EM C#</small></span></a><nav class="top-nav"><a href="/">Tickets</a><a class="active" href="/usage">Uso dos agentes</a></nav><span class="connection"><i></i> API local</span></header>
        <main class="shell usage-shell"><div class="page-heading"><div><p class="eyebrow">CONSUMO DA CONTA</p><h1>Uso dos agentes</h1><p class="subtitle">Leituras fornecidas localmente pelo Claude Code e Codex. A Central não estima tokens nem consulta credenciais.</p></div><span class="usage-refresh" id="usage-updated">Atualizando…</span></div>
        <section class="usage-providers"><article class="usage-card"><div class="usage-card-head"><div><p class="eyebrow">CLAUDE CODE</p><h2>Limites de uso</h2></div><span class="usage-status" id="claude-status">Lendo fonte local</span></div><div id="claude-windows" class="usage-windows"></div><p class="usage-source" id="claude-source"></p></article>
        <article class="usage-card"><div class="usage-card-head"><div><p class="eyebrow">CODEX</p><h2>Uso da conta</h2></div><span class="usage-status" id="codex-status">Lendo app-server</span></div><div id="codex-windows" class="usage-windows"></div><div id="codex-tokens" class="codex-tokens"></div><p class="usage-source" id="codex-source"></p></article></section>
        <p class="usage-note">Os percentuais e datas de renovação são exibidos somente quando vieram de uma resposta válida. Leituras ausentes, vencidas ou antigas não são convertidas em valores estimados.</p></main><script src="/usage.js" defer></script>
        """);

    public static string Index(
        IReadOnlyList<TicketSummary> tickets,
        IReadOnlyCollection<TicketSummary> all,
        string stage,
        string? query,
        IReadOnlyList<CentralRepository> repositories,
        string? repositoryFilter,
        string? agentFilter)
    {
        var active = all.Count(ticket => ticket.Stage != "concluido");
        var done = all.Count - active;
        var running = all.Count(ticket => ticket.Session is "running" or "uncertain");
        var attention = all.Count(ticket => ticket.Stage.Contains("aguardando", StringComparison.Ordinal)
            || ticket.Stage == "bloqueado" || ticket.StateError is not null || ticket.Runtime?.Phase == "error");
        var repositoryOptions = string.Join("", repositories.Select(item =>
            $"<option value=\"{E(item.Id)}\"{(string.Equals(item.Id, repositoryFilter, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{E(item.Name)}</option>"));
        var agentOptions = string.Join("", new[] { (Id: "claude", Name: "Claude Code"), (Id: "codex", Name: "Codex CLI") }
            .Select(item => $"<option value=\"{item.Id}\"{(string.Equals(item.Id, agentFilter, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{item.Name}</option>"));
        var activeClass = stage == "active" ? " selected" : "";
        var attentionClass = stage == "attention" ? " selected" : "";
        var doneClass = stage == "done" ? " selected" : "";
        var cards = tickets.Count == 0
            ? "<div class=\"empty-state\"><span class=\"empty-icon\">⌕</span><h2>Nenhum ticket encontrado</h2><p>Tente mudar o filtro ou a busca.</p></div>"
            : string.Join(Environment.NewLine, tickets.Select(TicketCard));
        var content = $"""
            <header class="topbar"><button id="sidebar-toggle" class="sidebar-toggle" type="button" aria-label="Recolher barra lateral" aria-controls="ticket-sidebar" aria-expanded="true" title="Recolher barra lateral"><span aria-hidden="true">☰</span></button><a class="brand" href="/"><span class="brand-mark">CT</span><span>Central <small>EM C#</small></span></a><nav class="top-nav"><a class="active" href="/">Tickets</a><a href="/usage">Uso dos agentes</a></nav><span class="connection"><i></i> API local</span></header>
            <div class="dashboard-layout"><aside class="sidebar" id="ticket-sidebar" aria-label="Navegação dos tickets">
              <p class="sidebar-label">SEU ESPAÇO DE TRABALHO</p>
              <a class="sidebar-link{activeClass}" href="/?stage=active" title="Em andamento"><span class="sidebar-icon" aria-hidden="true">▪</span><span class="sidebar-copy">Em andamento</span><b>{active}</b></a>
              <a class="sidebar-link{attentionClass}" href="/?stage=attention" title="Precisam de você"><span class="sidebar-icon" aria-hidden="true">◉</span><span class="sidebar-copy">Precisam de você</span><b>{attention}</b></a>
              <a class="sidebar-link{doneClass}" href="/?stage=done" title="Entregues"><span class="sidebar-icon" aria-hidden="true">✓</span><span class="sidebar-copy">Entregues</span><b>{done}</b></a>
              <div class="sidebar-note"><span class="small-dot"></span><span class="sidebar-copy">Trabalho local<p>Uma conversa por ticket.<br>Seu contexto, organizado.</p></span></div>
            </aside><main class="shell">
              <div class="page-heading"><div><p class="eyebrow">ACOMPANHAMENTO</p><h1>Central de tickets</h1><p class="subtitle">Gestão local dos tickets, sessões e entregas.</p></div><div class="heading-actions"><button class="secondary" id="sync-memory-home">Atualizar conhecimento</button><button class="secondary" id="azure-import">Trazer do Azure</button><button class="primary" id="new-ticket">Novo ticket</button></div></div>
              <div id="environment" class="environment" role="status" hidden></div>
              <div id="home-feedback" class="home-feedback" role="status" hidden></div>
              <section class="stats" aria-label="Resumo dos tickets">
                <a class="stat-card{activeClass}" href="/?stage=active"><span class="stat-label">Em andamento</span><strong>{active}</strong><span class="stat-note">tickets ativos</span></a>
                <a class="stat-card" href="/?stage=all"><span class="stat-label">Conversas abertas</span><strong>{running}</strong><span class="stat-note">sessões identificadas</span></a>
                <a class="stat-card{attentionClass}" href="/?stage=attention"><span class="stat-label">Precisam de você</span><strong>{attention}</strong><span class="stat-note">aprovação, validação ou bloqueio</span></a>
                <a class="stat-card{doneClass}" href="/?stage=done"><span class="stat-label">Entregues</span><strong>{done}</strong><span class="stat-note">histórico concluído</span></a>
                <a class="stat-card" href="/?stage=all"><span class="stat-label">Total</span><strong>{all.Count}</strong><span class="stat-note">tickets cadastrados</span></a>
              </section>
              <section class="list-panel"><div class="list-heading"><div><h2>{(stage == "done" ? "Tickets entregues" : stage == "attention" ? "Tickets que precisam de você" : stage == "all" ? "Todos os tickets" : "Em andamento")}</h2><p>{tickets.Count} resultado(s)</p></div>
                <form class="search" method="get" action="/"><input type="hidden" name="stage" value="{E(stage)}"><input type="hidden" name="repository" value="{E(repositoryFilter ?? "")}"><input type="hidden" name="agent" value="{E(agentFilter ?? "")}"><label class="sr-only" for="q">Buscar tickets</label><span aria-hidden="true">⌕</span><input id="q" name="q" value="{E(query ?? "")}" placeholder="Buscar ticket ou repositório"><button type="submit">Buscar</button></form></div>
                <form class="ticket-filters" method="get" action="/"><input type="hidden" name="stage" value="{E(stage)}"><input type="hidden" name="q" value="{E(query ?? "")}"><label>Repositório<select name="repository"><option value="">Todos os repositórios</option>{repositoryOptions}</select></label><label>Agente<select name="agent"><option value="">Todos os agentes</option>{agentOptions}</select></label><button class="secondary" type="submit">Aplicar filtros</button><a class="filter-reset" href="/?stage={Uri.EscapeDataString(stage)}">Limpar</a><button class="text-button" id="refresh-dashboard" type="button">Atualizar ↻</button></form>
                <div class="ticket-list">{cards}</div>
              </section><footer class="footer"><span id="repo-label">Central de Tickets · ASP.NET Core</span><span>Os dados existentes são preservados.</span></footer>
            </main></div>
            <dialog id="ticket-dialog"><form id="ticket-form"><div class="dialog-head"><p class="eyebrow">NOVO DESENVOLVIMENTO</p><h2>Organizar ticket</h2><p>O cadastro não inicia uma sessão de agente.</p></div><div class="form-grid">
              <label>Ticket<input name="ticket" required placeholder="GER5800"></label><label>Sprint<input name="sprint" required inputmode="numeric" placeholder="166"></label>
              <label class="wide">Repositório<select name="repository" id="repository" required></select></label><label>Agente<select name="agent"><option value="claude">Claude Code</option><option value="codex">Codex CLI</option></select></label><label>Tipo de branch<select name="branchType"><option>feature</option><option>hotfix</option></select></label>
              <label class="wide">Origem<input name="release" required value="main"></label><label class="wide">Título<input name="title" maxlength="160"></label><label class="wide">Escopo<textarea name="scope" required minlength="20" rows="7"></textarea></label><fieldset class="wide component-picker"><legend>Repositórios complementares (opcional)</legend><p>O plano define quais repositórios poderão ser alterados.</p><div data-component-rows></div><input type="hidden" name="components" value="[]"></fieldset><label class="wide">Entrega<select name="delivery"><option value="local_commit_only">Commit local, sem push</option><option value="no_commit">Sem commit</option></select></label>
            </div><div id="form-error" class="form-error" role="alert" hidden></div><div class="dialog-actions"><button type="button" class="secondary" id="cancel-ticket">Cancelar</button><button class="primary" type="submit">Cadastrar</button></div></form></dialog>
            <dialog id="azure-dialog"><form id="azure-form"><div class="dialog-head"><p class="eyebrow">IMPORTAÇÃO SEGURA</p><h2>Trazer ticket do Azure</h2><p>A Central consulta o work item e mostra uma prévia; nenhum agente começa automaticamente.</p></div><div class="form-grid">
              <label class="wide">Link ou ID do work item<input name="reference" required placeholder="https://dev.azure.com/...workitem=1900"></label>
              <label class="wide">PAT somente leitura <span class="optional">necessário apenas na primeira vez</span><input name="pat" type="password" autocomplete="off" placeholder="Cole o PAT do Azure DevOps"></label><div class="wide"><button type="button" class="secondary" id="save-azure-pat">Salvar acesso protegido</button><span id="azure-status" class="field-help"></span></div>
              <div class="wide"><button type="button" class="secondary" id="preview-azure">Consultar e mostrar prévia</button></div><section class="wide azure-preview" id="azure-preview" hidden></section>
              <div class="wide form-grid" id="azure-fields" hidden><label>Ticket<input name="ticket" required></label><label>Sprint<input name="sprint" required></label><label class="wide">Repositório<select name="repository" id="azure-repository" required></select></label><label>Agente<select name="agent"><option value="claude">Claude Code</option><option value="codex">Codex CLI</option></select></label><label>Branch<select name="branchType"><option value="feature">feature</option><option value="hotfix">hotfix</option></select></label><label class="wide">Origem<input name="release" required></label><label class="wide">Título<input name="title" maxlength="160"></label><label class="wide">Escopo<textarea name="scope" rows="6" required></textarea></label><fieldset class="wide component-picker"><legend>Repositórios complementares (opcional)</legend><p>O plano define quais repositórios poderão ser alterados.</p><div data-component-rows></div><input type="hidden" name="components" value="[]"></fieldset><label class="wide">Entrega<select name="delivery"><option value="local_commit_only">Commit local, sem push</option><option value="no_commit">Sem commit</option></select></label><fieldset class="wide"><legend>Anexos para baixar</legend><div id="azure-attachments" class="azure-attachments"></div></fieldset></div>
            </div><div id="azure-error" class="form-error" hidden></div><div class="dialog-actions"><button type="button" class="secondary" id="cancel-azure">Cancelar</button><button class="primary" type="submit" id="import-azure" disabled>Importar e cadastrar</button></div></form></dialog><script src="/dashboard.js" defer></script><script src="/app.js" defer></script>
            """;
        return Layout("Central de Tickets", content);
    }

    public static string Details(TicketDetails ticket)
    {
        var clientTicket = JsonSerializer.Serialize(new { id = ticket.Id, agent = ticket.Agent, branchType = ticket.BranchType, sprint = ticket.Sprint, release = ticket.Release, delivery = ticket.Delivery });
        var metrics = JsonSerializer.SerializeToElement(ticket.Metrics);
        var reviewCount = MetricInt(metrics, "reviews");
        var expansionCount = MetricInt(metrics, "scope_expansions");
        var notExecuted = MetricInt(metrics, "tests_not_executed");
        var blockedMinutes = MetricInt(metrics, "blocked_seconds") / 60;
        var escapedFindings = MetricInt(metrics, "escaped_findings");
        var riskReasons = ticket.Risk.Reasons.Length == 0 ? "Sem alerta registrado." : string.Join(" · ", ticket.Risk.Reasons);
        var files = ticket.AttachmentInfo.Names.Length == 0 ? "<li class=\"muted\">Nenhum anexo copiado.</li>" : string.Join("", ticket.AttachmentInfo.Names.Select(name => $"<li>{E(name)}</li>"));
        var events = ticket.Events.Count == 0 ? "<p class=\"muted\">Ainda não há eventos registrados.</p>" : string.Join("", ticket.Events.Take(12).Select(item => $"<li><time>{E(FormatDate(item.At))}</time><div><strong>{E(item.Type ?? "evento")}</strong><p>{E(item.Message ?? "")}</p></div></li>"));
        var content = $"""
            <header class="topbar"><a class="brand" href="/"><span class="brand-mark">CT</span><span>Central <small>EM C#</small></span></a><nav class="top-nav"><a href="/">Tickets</a><a href="/usage">Uso dos agentes</a></nav><a class="back-link" href="/">← Voltar</a></header>
            <main class="shell detail-shell"><p class="eyebrow">{E(ticket.Id)} · SPRINT {E(ticket.Sprint)}</p><div class="detail-title"><div><h1>{E(ticket.Title)}</h1><p class="subtitle">{E(ticket.RepositoryId ?? "Repositório não informado")} · {E(ticket.Agent)} · {E(ticket.Delivery)}</p></div><span class="status {StatusClass(ticket.Stage)}">{E(ticket.StageLabel)}</span></div>
              <section class="tracking-card"><div><strong>Acompanhamento</strong><p>Revisões: {reviewCount} · Expansões: {expansionCount} · Testes não executados: {notExecuted}</p><small>Tempo bloqueado: {blockedMinutes} min · Achados em review: {escapedFindings}</small></div><div class="tracking-status"><span class="risk-badge risk-{E(ticket.Risk.Level)}">{E(ticket.Risk.Label)}</span><span>Portão de entrega: {E(ticket.Quality.DeliveryReady ? "pronto" : "pendências")}</span><small>{E(riskReasons)}</small></div></section>
              <section class="detail-grid"><article class="detail-card scope-card"><div class="card-heading"><div><h2>Escopo do ticket</h2><p>Solicitação cadastrada</p></div></div><div class="scope-text">{E(ticket.Scope)}</div><div class="detail-actions"><button class="secondary content-action" data-kind="input">Entrada completa</button><button class="secondary content-action" data-kind="plan">Plano de análise</button><button class="secondary content-action" data-kind="diff">Alterações</button></div></article>
              <article class="detail-card"><div class="card-heading"><div><h2>Branch e entrega</h2><p>Informações de desenvolvimento</p></div></div><dl class="facts"><dt>Origem</dt><dd>{E(ticket.Release)}</dd><dt>Branch</dt><dd>{E(ticket.Branch ?? "Ainda não preparada")}</dd><dt>Agente</dt><dd>{E(ticket.Agent)}</dd><dt>Sessão</dt><dd>{E(ticket.Session)}</dd></dl><div class="detail-actions"><button class="secondary" id="prepare-worktree">Preparar worktree</button><button class="primary" id="start-session">Abrir / retomar conversa</button><button class="secondary" id="focus-session">Focar conversa</button><button class="secondary" data-open="worktree">Pasta de trabalho</button><button class="secondary" data-open="document">Documento final</button><button class="secondary" id="edit-ticket">Editar cadastro</button><button class="secondary" id="sync-memory">Atualizar conhecimento</button><button class="secondary" id="complete-ticket">Marcar entregue</button></div><div id="session-feedback" class="form-error" hidden></div></article>
              <article class="detail-card"><div class="card-heading"><div><h2>Anexos</h2><p>{ticket.AttachmentInfo.Names.Length} arquivo(s)</p></div></div><ul class="file-list">{files}</ul><div class="detail-actions"><button class="secondary" data-open="references">Abrir pasta de anexos</button><button class="secondary" id="add-attachments">Adicionar arquivos</button><input id="attachment-files" type="file" multiple hidden></div></article><article class="detail-card"><div class="card-heading"><div><h2>Atividade recente</h2><p>Eventos do ticket</p></div></div><ol class="event-list">{events}</ol></article></section><footer class="footer"><span>Central de Tickets · ASP.NET Core</span><span>Entrega manual preserva histórico e pendências.</span></footer></main>
            <dialog id="content-dialog" class="content-dialog"><div class="content-head"><div><p class="eyebrow" id="content-title">CONTEÚDO</p><p class="subtitle">Visualização formatada; o texto bruto continua disponível para conferência.</p></div><div class="content-actions"><button class="secondary active" id="show-rendered" type="button">Formatado</button><button class="secondary" id="show-raw" type="button">Texto bruto</button><button class="secondary" id="print-content" type="button">Imprimir</button><button class="secondary" id="close-content">Fechar</button></div></div><article id="content-rendered" class="markdown-view"></article><pre id="content-body" hidden></pre></dialog>
            <dialog id="registration-dialog"><form id="registration-form"><div class="dialog-head"><p class="eyebrow">CADASTRO</p><h2>Editar entrega de {E(ticket.Id)}</h2><p>Alterar agente ou entrega após iniciar requer cautela; não muda worktree existente.</p></div><div class="form-grid"><label>Agente<select name="agent"><option value="claude">Claude Code</option><option value="codex">Codex CLI</option></select></label><label>Tipo de branch<select name="branchType"><option value="feature">feature</option><option value="hotfix">hotfix</option></select></label><label>Sprint<input name="sprint" required></label><label>Origem<input name="release" required></label><label class="wide">Entrega<select name="delivery"><option value="local_commit_only">Commit local, sem push</option><option value="no_commit">Sem commit</option></select></label></div><div id="registration-error" class="form-error" hidden></div><div class="dialog-actions"><button type="button" class="secondary" id="cancel-edit">Cancelar</button><button class="primary">Salvar</button></div></form></dialog><script>window.centralTicket={clientTicket};</script><script src="/dashboard.js" defer></script><script src="/app.js" defer></script>
            """;
        return Layout(ticket.Id + " · Central de Tickets", content);
    }

    public static string NotFound(string id) => Layout("Ticket não encontrado", $"<header class=\"topbar\"><a class=\"brand\" href=\"/\">Central</a></header><main class=\"shell\"><div class=\"empty-state\"><h1>Ticket não encontrado</h1><p>Não localizei {E(id)}.</p><a class=\"primary-link\" href=\"/\">Voltar</a></div></main>");
    private static string TicketCard(TicketSummary ticket) => $"""<a class="ticket-row" href="/tickets/{Uri.EscapeDataString(ticket.Id)}"><div class="ticket-main"><span class="ticket-code">{E(ticket.Id)}</span><span class="status {StatusClass(ticket.Stage)}">{E(ticket.StageLabel)}</span><span class="risk-badge risk-{E(ticket.Risk.Level)}">{E(ticket.Risk.Label)}</span><h3>{E(ticket.Title)}</h3><p>{E(ticket.RepositoryId)} · sprint {E(ticket.Sprint)} · {E(ticket.Agent)}</p></div><div class="ticket-side"><span>Atualizado</span><time>{E(FormatDate(ticket.UpdatedAt))}</time></div></a>""";
    private static int MetricInt(JsonElement metrics, string name) => metrics.ValueKind == JsonValueKind.Object && metrics.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static string Layout(string title, string content) => $"""<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light"><title>{E(title)}</title><link rel="stylesheet" href="/style.css"></head><body><div class="app-frame">{content}</div></body></html>""";
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string FormatDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "—";
    private static string StatusClass(string stage) => stage switch { "concluido" => "status-done", "bloqueado" => "status-blocked", "aguardando_aprovacao" or "aguardando_validacao_manual" or "aguardando_decisao_revisao" => "status-waiting", _ => "status-active" };
}
