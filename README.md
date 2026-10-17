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
- **Merge, rebase, cherry-pick, revert, reset**: right-click a commit in the graph. Merge shows a preview first (fast-forward, clean, or the files that would conflict; needs git 2.38+) and offers a squash variant. Reset has soft, mixed and hard modes, and hard asks for confirmation. When an operation stops for conflicts, a banner above the graph offers Continue, Skip (not for merges) and Abort, and the app switches to the working directory so you can resolve the conflicted files.
- **Conflict resolution**: selecting a conflicted file opens a 3-way editor with read-only Ours and Theirs panes above an editable Result. Step through conflicts, accept ours, theirs or both per block, edit freely, then Mark resolved (it stages the file). Whole-file Use ours / Use theirs handles binary and delete conflicts. Pane titles say what ours and theirs mean for the current operation.
- **Interactive rebase**: right-click a commit and choose "Interactive rebase of the commits after this one". Set each commit to pick, reword, edit, squash, fixup or drop, reorder with the arrows, and edit the messages for rewords and squashes. Edit and conflicts stop the rebase and show the Continue / Skip / Abort banner. Ranges containing merge commits are refused.
- **Stash**: the Stash button saves local changes (optionally including untracked files). Select a stash in the Stashes list to browse its files and diff; right-click to apply, pop or drop it. An apply that conflicts leaves the stash in place and opens the working directory.
- **File history and blame**: with a file open in the detail pane, History lists the commits that touched it (following renames) with a diff for each, and Blame shows who last changed every line. You can blame at an older commit or jump to a commit in the graph.
- **Undo**: the Undo button reverses the last operation recorded in the reflog (commit, merge, reset, pull, a whole rebase, or a branch switch) after showing exactly what it will do. It refuses when you have uncommitted changes or an operation is in progress, and pressing it again redoes the change. Tools... also lists the reflog so you can reset to any entry.
- **Submodules and worktrees**: Tools... lists submodules (initialise, update, open in a tab) and worktrees (add for a new or existing branch, remove, prune, open in a tab).

Hunk and line staging, and all remote operations, run the `git` CLI, so `git` must be on your PATH.

## Run

```bash
dotnet test src/GitDevExLite.slnx
dotnet run --project src/GitDevExLite.App -- path/to/a/repo
```

## Roadmap
1. Async git I/O for the remaining synchronous operations, multi-select in the graph
