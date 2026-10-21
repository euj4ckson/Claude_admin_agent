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
            "none", null, null, [], new AttachmentDetails("C:/refs/ger5800", false, [], []),
            new RiskSummary("unknown", "Não avaliado", []),
            new QualitySummary(false, false, [], new QualityCounts(0, 0, 0)), null);

        var html = TicketPageRenderer.Details(ticket);

        Assert.Contains("id=\"prepare-worktree\"", html);
        Assert.Contains("id=\"start-session\"", html);
        Assert.Contains("id=\"session-feedback\"", html);
        Assert.Equal(1, html.Split("id=\"start-session\"", StringSplitOptions.None).Length - 1);
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

        var html = TicketPageRenderer.Index([ticket], [ticket], "active", null);

        Assert.Contains("href=\"/style.css\"", html);
        Assert.Contains("name=\"stage\" value=\"active\"", html);
        Assert.Contains("id=\"ticket-form\"", html);
        Assert.Contains("id=\"new-ticket\"", html);
        Assert.Contains("/app.js", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }
}
