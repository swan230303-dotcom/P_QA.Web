using System.Collections.Concurrent;
using System.Security.Cryptography;
using PQA.Web.Models;

namespace PQA.Web.Services;

public sealed class SessionService
{
    private readonly ConcurrentDictionary<string, (UserSession User, DateTimeOffset Expires)> sessions = new();
    private readonly TimeSpan lifetime;

    public SessionService(IConfiguration configuration) =>
        lifetime = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("SessionHours", 8), 1, 24));

    public string Create(UserSession user)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[token] = (user, DateTimeOffset.UtcNow.Add(lifetime));
        return token;
    }

    public UserSession? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var entry)) return null;
        if (entry.Expires <= DateTimeOffset.UtcNow) { sessions.TryRemove(token, out _); return null; }
        return entry.User;
    }

    public void Remove(string? token) { if (!string.IsNullOrWhiteSpace(token)) sessions.TryRemove(token, out _); }
}
