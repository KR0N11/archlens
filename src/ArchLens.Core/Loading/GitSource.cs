using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArchLens.Core.Loading;

// Where the code comes from: a local folder, or a public GitHub URL we shallow-clone.
public record SourceInfo(string Root, string Repo, string? Commit);

public static class GitSource
{
    private static readonly Regex GitHubUrl = new(@"^(?:https?://)?(?:www\.)?github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?/?$");

    // "https://github.com/dotnet/eShop.git" -> "github.com/dotnet/eShop"; anything else -> null.
    // Only public github.com URLs are accepted, so a request can't make us clone from an arbitrary host.
    public static string? NormalizeGitHub(string input)
    {
        var match = GitHubUrl.Match(input.Trim());
        return match.Success ? $"github.com/{match.Groups[1].Value}/{match.Groups[2].Value}" : null;
    }

    // Asks GitHub for the commit HEAD points at, without cloning. Used to check the cache first.
    public static string LatestCommit(string repo, TimeSpan timeout) =>
        Run("git", $"ls-remote https://{repo}.git HEAD", Directory.GetCurrentDirectory(), timeout).Split('\t')[0];

    // Shallow clone (latest commit only) into dir, replacing anything left there from an earlier run.
    public static string Clone(string repo, string dir, TimeSpan timeout)
    {
        // A leftover clone from an earlier run may be stale; start clean.
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Run("git", $"clone --depth 1 --quiet https://{repo}.git \"{dir}\"", Directory.GetCurrentDirectory(), timeout);
        return Run("git", "rev-parse HEAD", dir, timeout);
    }

    public static SourceInfo Resolve(string target)
    {
        var repo = NormalizeGitHub(target);
        if (repo != null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "archlens", repo.Replace("github.com/", "").Replace('/', '_'));
            var commit = Clone(repo, dir, TimeSpan.FromMinutes(5));
            return new SourceInfo(dir, repo, commit);
        }

        var root = Path.GetFullPath(target);
        // A mistyped path should fail here, with the path in the message.
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Not a folder or a github.com URL: {target}");
        // A local clone of a GitHub repo still gets source links, pinned to its current commit.
        var remote = TryRun("git", "remote get-url origin", root);
        var localCommit = TryRun("git", "rev-parse HEAD", root);
        var name = (remote == null ? null : NormalizeGitHub(remote)) ?? Path.GetFileName(root);
        return new SourceInfo(root, name, localCommit);
    }

    private static string? TryRun(string file, string args, string dir)
    {
        try
        {
            return Run(file, args, dir, TimeSpan.FromSeconds(30));
        }
        catch (InvalidOperationException)
        {
            // Not a git repo (or no origin): fine, links just won't be generated.
            return null;
        }
    }

    private static string Run(string file, string args, string dir, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Never let git stop and wait for a username/password on a private or missing repo.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // A huge repo or a hung network call must not block the worker forever.
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{file} {args} took longer than {timeout.TotalSeconds:0} s.");
        }
        // git reports failure only through the exit code.
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {args} failed: {error.Result.Trim()}");
        return output.Result.Trim();
    }
}
