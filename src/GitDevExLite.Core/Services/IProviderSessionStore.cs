using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface IProviderSessionStore
{
    ProviderSession? Get(HostingProviderKind provider, string host);
    void Save(ProviderSession session);
    void Remove(HostingProviderKind provider, string host);
}
