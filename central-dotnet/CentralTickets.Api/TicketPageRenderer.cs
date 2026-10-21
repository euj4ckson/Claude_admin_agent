using System.Net;

namespace CentralTickets.Api;

public static class TicketPageRenderer
{
    public static string Index(IReadOnlyList<TicketSummary> tickets, IReadOnlyCollection<TicketSummary> all, string stage, string? query)
    {
        var active = all.Count(ticket => ticket.Stage != "concluido");
        var done = all.Count - active;
        var cards = tickets.Count == 0
            ? "<div class=\"empty-state\"><span class=\"empty-icon\">⌕</span><h2>Nenhum ticket encontrado</h2><p>Tente mudar o filtro ou a busca.</p></div>"
            : string.Join(Environment.NewLine, tickets.Select(TicketCard));

        var content = $"""
            <header class="topbar"><a class="brand" href="/"><span class="brand-mark">CT</span><span>Central <small>EM C#</small></span></a><span class="connection"><i></i> API local · somente leitura</span></header>
            <main class="shell">
              <div class="page-heading"><div><p class="eyebrow">ACOMPANHAMENTO</p><h1>Central de tickets</h1><p class="subtitle">Uma primeira versão em ASP.NET Core, lendo os registros já existentes.</p></div><span class="migration-pill">Migração em andamento</span></div>
              <section class="stats" aria-label="Resumo dos tickets">
                <a class="stat-card {(stage == "active" ? "selected" : "")}" href="/?stage=active"><span class="stat-label">Em andamento</span><strong>{active}</strong><span class="stat-note">tickets ativos</span></a>
                <a class="stat-card {(stage == "done" ? "selected" : "")}" href="/?stage=done"><span class="stat-label">Entregues</span><strong>{done}</strong><span class="stat-note">histórico concluído</span></a>
                <a class="stat-card {(stage == "all" ? "selected" : "")}" href="/?stage=all"><span class="stat-label">Total</span><strong>{all.Count}</strong><span class="stat-note">tickets cadastrados</span></a>
              </section>
              <section class="list-panel">
                <div class="list-heading"><div><h2>{(stage == "done" ? "Tickets entregues" : stage == "all" ? "Todos os tickets" : "Em andamento")}</h2><p>{tickets.Count} resultado(s)</p></div>
                  <form class="search" method="get" action="/"><input type="hidden" name="stage" value="{E(stage)}"><label class="sr-only" for="q">Buscar tickets</label><span aria-hidden="true">⌕</span><input id="q" name="q" value="{E(query ?? "")}" placeholder="Buscar por código ou título"><button type="submit">Buscar</button></form>
                </div>
                <div class="ticket-list">{cards}</div>
              </section>
              <footer class="footer"><span>Central de Tickets · .NET 10</span><span>Esta etapa não executa ações nem altera os dados.</span></footer>
            </main>
            """;
        return Layout("Central de Tickets", content);
    }

    public static string Details(TicketDetails ticket)
    {
        var files = ticket.AttachmentInfo.Names.Length == 0
            ? "<li class=\"muted\">Nenhum arquivo copiado para a pasta de referência.</li>"
            : string.Join("", ticket.AttachmentInfo.Names.Select(name => $"<li><span class=\"file-icon\">↳</span>{E(name)}</li>"));
        var events = ticket.Events.Count == 0
            ? "<p class=\"muted\">Ainda não há eventos registrados.</p>"
            : string.Join("", ticket.Events.Take(12).Select(item => $"<li><time>{E(FormatDate(item.At))}</time><div><strong>{E(item.Type ?? "evento")}</strong><p>{E(item.Message ?? "")}</p></div></li>"));
        var content = $"""
            <header class="topbar"><a class="brand" href="/"><span class="brand-mark">CT</span><span>Central <small>EM C#</small></span></a><a class="back-link" href="/">← Voltar para tickets</a></header>
            <main class="shell detail-shell">
              <p class="eyebrow">{E(ticket.Id)} · SPRINT {E(ticket.Sprint)}</p>
              <div class="detail-title"><div><h1>{E(ticket.Title)}</h1><p class="subtitle">{E(ticket.RepositoryId ?? "Repositório não informado")} · {E(ticket.Agent)} · {E(ticket.Delivery)}</p></div><span class="status {StatusClass(ticket.Stage)}">{E(ticket.StageLabel)}</span></div>
              <section class="detail-grid">
                <article class="detail-card scope-card"><div class="card-heading"><span class="card-icon">≡</span><div><h2>Escopo do ticket</h2><p>Solicitação cadastrada</p></div></div><div class="scope-text">{E(ticket.Scope)}</div></article>
                <article class="detail-card"><div class="card-heading"><span class="card-icon">⌘</span><div><h2>Branch e entrega</h2><p>Informações de desenvolvimento</p></div></div><dl class="facts"><dt>Origem</dt><dd>{E(ticket.Release)}</dd><dt>Branch</dt><dd>{E(ticket.Branch ?? "Ainda não preparada")}</dd><dt>Agente</dt><dd>{E(ticket.Agent)}</dd><dt>Sessão</dt><dd>{E(ticket.Session)}</dd></dl></article>
                <article class="detail-card"><div class="card-heading"><span class="card-icon">▱</span><div><h2>Anexos</h2><p>{ticket.AttachmentInfo.Names.Length} arquivo(s) disponível(is)</p></div></div><ul class="file-list">{files}</ul></article>
                <article class="detail-card"><div class="card-heading"><span class="card-icon">◷</span><div><h2>Atividade recente</h2><p>Últimos eventos registrados</p></div></div><ol class="event-list">{events}</ol></article>
              </section>
              <footer class="footer"><span>Central de Tickets · .NET 10</span><span>Somente leitura · Nenhuma alteração será feita por esta tela.</span></footer>
            </main>
            """;
        return Layout(ticket.Id + " · Central de Tickets", content);
    }

    public static string NotFound(string id)
    {
        var content = $"<header class=\"topbar\"><a class=\"brand\" href=\"/\"><span class=\"brand-mark\">CT</span><span>Central <small>EM C#</small></span></a></header><main class=\"shell\"><div class=\"empty-state\"><span class=\"empty-icon\">!</span><h1>Ticket não encontrado</h1><p>Não localizei o cadastro {E(id)}.</p><a class=\"primary-link\" href=\"/\">Voltar para a lista</a></div></main>";
        return Layout("Ticket não encontrado", content);
    }

    private static string TicketCard(TicketSummary ticket) => $"""
        <a class="ticket-row" href="/tickets/{Uri.EscapeDataString(ticket.Id)}">
          <div class="ticket-main"><span class="ticket-code">{E(ticket.Id)}</span><span class="status {StatusClass(ticket.Stage)}">{E(ticket.StageLabel)}</span><h3>{E(ticket.Title)}</h3><p>{E(ticket.RepositoryId ?? "Repositório não informado")} <span>·</span> sprint {E(ticket.Sprint)} <span>·</span> {E(ticket.Agent)}</p></div>
          <div class="ticket-side"><span>Atualizado</span><time>{E(FormatDate(ticket.UpdatedAt))}</time><span class="arrow">↗</span></div>
        </a>
        """;

    private static string Layout(string title, string content) => $"""
        <!doctype html>
        <html lang="pt-BR">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light"><title>{E(title)}</title><link rel="stylesheet" href="/style.css"></head>
        <body><div class="app-frame">{content}</div></body>
        </html>
        """;

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string FormatDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "—";
    private static string StatusClass(string stage) => stage switch
    {
        "concluido" => "status-done",
        "bloqueado" => "status-blocked",
        "aguardando_aprovacao" or "aguardando_validacao_manual" or "aguardando_decisao_revisao" => "status-waiting",
        _ => "status-active"
    };
}
