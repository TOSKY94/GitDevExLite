namespace GitDevExLite.Core.Models;

public sealed record SecretMatch(
    string Path,
    bool IsStaged,
    int LineNumber,
    string Rule,
    string Preview);