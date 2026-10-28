namespace CentralTickets.SessionRunner;

public static class ClaudeSessionPolicy
{
    public static bool ShouldSendEntryPrompt(bool sessionStarted, bool firstPromptSubmitted) =>
        !sessionStarted || !firstPromptSubmitted;
}
