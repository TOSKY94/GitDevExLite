# GitDevExLite

A GitKraken-style git client in C# (Avalonia + LibGit2Sharp).

```
src/GitDevExLite.Core           git service (LibGit2Sharp) + commit-graph lane layout, no UI deps
src/GitDevExLite.App            Avalonia MVVM desktop app
tests/GitDevExLite.Core.Tests   graph layout unit tests
```

## First run

```bash
dotnet new sln -n GitDevExLite
dotnet sln add src/GitDevExLite.Core src/GitDevExLite.App tests/GitDevExLite.Core.Tests
dotnet test
dotnet run --project src/GitDevExLite.App -- path/to/a/repo
```

## Roadmap
1. Diff viewer (AvaloniaEdit) + changed-files list for a commit
2. Stage/unstage + commit panel (GitService already exposes the calls)
3. Branch create/merge/delete, fetch/pull/push + credentials
4. Rebase / cherry-pick via the `git` CLI, conflict UI, multi-repo tabs
