using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed class InMemoryProviderSessionStore : IProviderSessionStore
{
    private readonly Dictionary<(HostingProviderKind Provider, string Host), ProviderSession> _sessions = new();

    public ProviderSession? Get(HostingProviderKind provider, string host) =>
        _sessions.TryGetValue((provider, NormalizeHost(host)), out var session) ? session : null;

    public void Save(ProviderSession session) =>
        _sessions[(session.Provider, NormalizeHost(session.Host))] = session;

    public void Remove(HostingProviderKind provider, string host) =>
        _sessions.Remove((provider, NormalizeHost(host)));

    private static string NormalizeHost(string host) => host.Trim().ToLowerInvariant();
}
