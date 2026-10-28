using CentralTickets.SessionRunner;

namespace CentralTickets.Api.Tests;

public sealed class ClaudeSessionPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Sends_entry_prompt_only_until_a_prompt_was_accepted(bool sessionStarted, bool firstPromptSubmitted, bool expected)
    {
        Assert.Equal(expected, ClaudeSessionPolicy.ShouldSendEntryPrompt(sessionStarted, firstPromptSubmitted));
    }
}
