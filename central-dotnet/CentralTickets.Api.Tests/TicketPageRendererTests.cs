using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class TicketPageRendererTests
{
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
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }
}
