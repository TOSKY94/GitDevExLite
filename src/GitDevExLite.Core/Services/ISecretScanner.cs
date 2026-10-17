using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface ISecretScanner
{
    IReadOnlyList<SecretMatch> Scan(IGitService git, IReadOnlyList<FileChange> files);
}