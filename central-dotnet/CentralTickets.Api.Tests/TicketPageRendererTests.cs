using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class TicketPageRendererTests
{
    [Fact]
    public void Details_exposes_worktree_and_session_actions_once()
    {
        var ticket = new TicketDetails(
            "GER5800", "GER5800", "166", "main", "Escopo seguro para o teste.", "Ticket de teste",
            "no_commit", "feature", "C:/git/ss_erp_tag1", "ss_erp_tag1", null, null, "claude", [],
            null, null, false, null, [], null, null, null, "cadastrado", "Cadastrado", null, null,
            "none", null, null, null, [], new AttachmentDetails("C:/refs/ger5800", false, [], []),
            new RiskSummary("unknown", "Não avaliado", []),
            new QualitySummary(false, false, [], new QualityCounts(0, 0, 0)), null);

        var html = TicketPageRenderer.Details(ticket, antiforgeryToken: "csrf-test-token");

        Assert.Contains("id=\"start-session\"", html);
        Assert.DoesNotContain("id=\"prepare-worktree\"", html);
        Assert.Contains("Preparada automaticamente ao iniciar", html);
        Assert.Contains("id=\"session-feedback\"", html);
        Assert.Contains("data-ticket-id=\"GER5800\"", html);
        Assert.Contains("data-agent=\"claude\"", html);
        Assert.DoesNotContain("window.centralTicket", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<script src=\"/dashboard.js\" defer></script>", html);
        Assert.Contains("<script src=\"/app.js\" defer></script>", html);
        Assert.Contains("id=\"start-session\" type=\"submit\"", html);
        Assert.Contains("id=\"session-feedback\" class=\"field-help\" role=\"status\" aria-live=\"polite\"", html);
        Assert.Contains("id=\"start-session-form\" method=\"post\" action=\"/tickets/GER5800/start\"", html);
        Assert.Contains("id=\"sync-memory-form\" method=\"post\" action=\"/tickets/GER5800/memory-sync\"", html);
        Assert.Contains("action=\"/tickets/GER5800/start\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"csrf-test-token\">", html);
        Assert.Contains("action=\"/tickets/GER5800/memory-sync\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"csrf-test-token\">", html);
        Assert.Contains("id=\"show-rendered\"", html);
        Assert.Contains("id=\"show-raw\"", html);
        Assert.Contains("id=\"print-content\"", html);
        Assert.Contains("id=\"content-rendered\"", html);
        Assert.Contains("<details class=\"scope-accordion\" open>", html);
        Assert.Contains("<summary class=\"card-heading scope-accordion-summary\">", html);
        Assert.Contains("<div class=\"scope-text\">Escopo seguro para o teste.</div>", html);
        Assert.Contains("id=\"delete-ticket\" type=\"button\">Excluir ticket</button>", html);
        Assert.Contains("href=\"/ticket-details.css\"", html);
        Assert.Equal(1, html.Split("id=\"start-session\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Continuar em sessão nova", html);

        var stoppedHtml = TicketPageRenderer.Details(ticket with { Session = "stopped" }, antiforgeryToken: "csrf-test-token");
        Assert.Contains("Continuar em sessão nova", stoppedHtml);
        Assert.Contains("action=\"/tickets/GER5800/start?fresh=1\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"csrf-test-token\">", stoppedHtml);
    }

    [Fact]
    public void Index_renders_filter_navigation_and_escapes_ticket_content()
    {
        var ticket = new TicketSummary(
            "GER5800", "GER5800", "166", "main", "<script>alert(1)</script>",
            "no_commit", "claude", "ss_erp_tag1", false, null, null, null,
            "analise", "Em análise", null, "none", null,
            new RiskSummary("unknown", "Não avaliado", []),
            new QualitySummary(false, false, [], new QualityCounts(0, 0, 0)),
            null, new AttachmentSummary([], 0, false, null));

        var html = TicketPageRenderer.Index([ticket], [ticket], "active", null, [], null, null, antiforgeryToken: "csrf-test-token");

        Assert.Contains("href=\"/style.css\"", html);
        Assert.Contains("name=\"stage\" value=\"active\"", html);
        Assert.Contains("id=\"ticket-form\"", html);
        Assert.Contains("id=\"new-ticket\"", html);
        Assert.Contains("id=\"sidebar-toggle\"", html);
        Assert.Contains("id=\"sync-memory-home\"", html);
        Assert.Contains("id=\"sync-memory-home-form\" method=\"post\" action=\"/memory-sync\"", html);
        Assert.Contains("action=\"/memory-sync\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"csrf-test-token\">", html);
        Assert.Contains("id=\"environment\"", html);
        Assert.Contains("/?stage=attention", html);
        Assert.Contains("name=\"repository\"", html);
        Assert.Contains("name=\"agent\"", html);
        Assert.Contains("/app.js", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public void Index_preserves_selected_filters_and_renders_all_sidebar_counts()
    {
        var ticket = new TicketSummary(
            "FVA0351", "FVA0351", "166", "main", "Ticket de estoque",
            "local_commit_only", "codex", "ss_fva", false, null, null, null,
            "aguardando_aprovacao", "Aguardando aprovação", null, "running", null,
            new RiskSummary("unknown", "Não avaliado", []),
            new QualitySummary(false, false, [], new QualityCounts(0, 0, 0)), null,
            new AttachmentSummary([], 0, false, null));

        var html = TicketPageRenderer.Index([ticket], [ticket], "attention", "FVA", [new("ss_fva", "ss_fva", "C:/git/ss_fva", true, null)], "ss_fva", "codex");

        Assert.Contains("<option value=\"ss_fva\" selected>", html);
        Assert.Contains("<option value=\"codex\" selected>", html);
        Assert.Contains("<strong>1</strong><span class=\"stat-note\">sessões identificadas</span>", html);
        Assert.Contains("<b>1</b></a>", html);
    }
}
