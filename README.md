# GitDevExLite

A GitKraken-style git client in C# (Avalonia + LibGit2Sharp).

```
src/GitDevExLite.Core           git service (LibGit2Sharp), diff/patch building, commit-graph layout, settings store; no UI deps
src/GitDevExLite.App            Avalonia MVVM desktop app (AvaloniaEdit + TextMate for diffs)
src/tests/GitDevExLite.Core.Tests   unit tests (graph layout, patch builder, git service, search, settings)
```

## Features

- **Commit details**: changed-files list per commit, with inline or side-by-side diffs (two synced editors) and syntax highlighting.
- **Working directory**: staged and unstaged lists, per-file and bulk stage/unstage, commit message box, and amend last commit.
- **Hunk and line staging**: click a hunk's `+`/`−` marker in the gutter, or tick individual lines and apply them. Staged diffs can be unstaged the same way.
- **Branch actions**: right-click a commit in the graph to create a branch (optionally checking it out), check out a commit or branch, rename or delete a local branch, or copy the SHA.
- **Graph search**: matches message, author, SHA prefix and branch name. Non-matching rows are dimmed and the arrows jump between matches.
- **Tabs and recents**: each repo opens in its own tab. The Recent menu lists the last 20 repos, and open tabs are restored on launch (stored in `%APPDATA%/GitDevExLite/settings.json`).

Hunk and line staging runs `git apply --cached`, so `git` must be on your PATH.

## Run

```bash
dotnet test src/GitDevExLite.slnx
dotnet run --project src/GitDevExLite.App -- path/to/a/repo
```

## Roadmap
1. Fetch/pull/push + credentials
2. Merge, rebase and cherry-pick via the `git` CLI, conflict UI
3. Stash actions, file watcher for external changes, async git I/O
