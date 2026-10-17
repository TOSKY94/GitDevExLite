using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class ForcePushTests
{
    [Fact]
    public async Task Force_push_with_lease_updates_diverged_remote()
    {
        var root = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "r.git");
        var dir = Path.Combine(root, "a");
        Directory.CreateDirectory(dir);
        Repository.Init(bare, isBare: true);
        Repository.Init(dir);
        try
        {
            using (var repo = new Repository(dir))
            {
                repo.Config.Set("user.name", "T");
                repo.Config.Set("user.email", "t@e.com");
                repo.Network.Remotes.Add("origin", bare);
            }
            using var git = new GitService();
            git.Open(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "1\n");
            git.Stage("f.txt");
            git.Commit("one");
            File.WriteAllText(Path.Combine(dir, "f.txt"), "2\n");
            git.Stage("f.txt");
            git.Commit("two");
            await git.PushAsync(null, CancellationToken.None);

            await git.ResetAsync(git.GetCommits()[1].Sha, GitDevExLite.Core.Services.ResetMode.Hard, null, CancellationToken.None);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "3\n");
            git.Stage("f.txt");
            git.Commit("three");

            await Assert.ThrowsAnyAsync<Exception>(() => git.PushAsync(null, CancellationToken.None));
            await git.PushAsync(null, CancellationToken.None, forceWithLease: true);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
