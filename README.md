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
- **Live updates**: each tab watches its repo, so edits, commits, checkouts and ref changes made outside the app show up on their own (debounced, ignoring `bin/`, `obj/`, `node_modules/`). Your selection and ticked diff lines are kept unless the diff itself changed.
- **Tabs and recents**: each repo opens in its own tab. The Recent menu lists the last 20 repos, and open tabs are restored on launch (stored in `%APPDATA%/GitDevExLite/settings.json`).
- **Remotes**: Fetch, Pull (merge, rebase or fast-forward only) and Push run through the `git` CLI with a progress bar and Cancel. The first push of a branch sets its upstream; "Set upstream..." in the graph menu changes it later.
- **Ahead/behind**: `↑n ↓m` next to local branches in the branch list and on graph badges.
- **Clone**: the Clone dialog shows progress and can be cancelled; the new repo opens in a tab.
- **Prune and auto-fetch**: Options menu toggles prune-on-fetch and a background fetch of the active tab every 5 minutes (`AutoFetchMinutes` in settings.json). Background fetch never opens sign-in windows and pauses itself if authentication is needed.
- **Tags**: create (lightweight or annotated), push, delete, and delete from the remote, via the graph menu or the Tags list.
- **Authentication**: HTTPS uses your configured credential helper, so Git Credential Manager works as usual. When a remote rejects you, the app offers a Git Credential Manager sign-in (GitHub, Azure DevOps and other hosts GCM supports) or a personal access token, stored through `git credential approve`. SSH remotes use your ssh-agent / `~/.ssh/config`; the app never stores keys or tokens itself.

Hunk and line staging, and all remote operations, run the `git` CLI, so `git` must be on your PATH.

## Run

```bash
dotnet test src/GitDevExLite.slnx
dotnet run --project src/GitDevExLite.App -- path/to/a/repo
```

## Roadmap
1. Merge, rebase and cherry-pick via the `git` CLI, conflict UI
2. Stash actions, async git I/O
