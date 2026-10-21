using System.Security.Cryptography;
using System.Text;

namespace CentralTickets.Api;

public sealed class LocalAccess(string bootstrapToken)
{
    private readonly byte[] _bootstrapToken = Encoding.UTF8.GetBytes(bootstrapToken);
    private readonly byte[] _sessionToken = RandomNumberGenerator.GetBytes(32);

    public string SessionToken => Convert.ToBase64String(_sessionToken);

    public bool Matches(string supplied)
    {
        var candidate = Encoding.UTF8.GetBytes(supplied);
        return candidate.Length == _bootstrapToken.Length
            && CryptographicOperations.FixedTimeEquals(candidate, _bootstrapToken);
    }

    public bool MatchesSession(string supplied)
    {
        byte[] candidate;
        try { candidate = Convert.FromBase64String(supplied); }
        catch (FormatException) { return false; }

        return candidate.Length == _sessionToken.Length
            && CryptographicOperations.FixedTimeEquals(candidate, _sessionToken);
    }
}
