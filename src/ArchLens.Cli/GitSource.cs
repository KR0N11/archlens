using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArchLens.Cli;

// Where the code comes from: a local folder, or a public GitHub URL we shallow-clone.
public record SourceInfo(string Root, string Repo, string? Commit);

public static class GitSource
{
    private static readonly Regex GitHubUrl = new(@"^(?:https?://)?github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?/?$");

    public static SourceInfo Resolve(string target)
    {
        var match = GitHubUrl.Match(target);
        if (match.Success)
        {
            var repo = $"github.com/{match.Groups[1].Value}/{match.Groups[2].Value}";
            var dir = Path.Combine(Path.GetTempPath(), "archlens", match.Groups[1].Value + "_" + match.Groups[2].Value);
            // A leftover clone from an earlier run may be stale; start clean.
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Run("git", $"clone --depth 1 --quiet https://{repo}.git \"{dir}\"", Directory.GetCurrentDirectory());
            return new SourceInfo(dir, repo, Run("git", "rev-parse HEAD", dir));
        }

        var root = Path.GetFullPath(target);
        // A mistyped path should fail here, with the path in the message.
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Not a folder or a github.com URL: {target}");
        // A local clone of a GitHub repo still gets source links, pinned to its current commit.
        var remote = TryRun("git", "remote get-url origin", root);
        var commit = TryRun("git", "rev-parse HEAD", root);
        var remoteMatch = remote == null ? Match.Empty : GitHubUrl.Match(remote);
        var name = remoteMatch.Success ? $"github.com/{remoteMatch.Groups[1].Value}/{remoteMatch.Groups[2].Value}" : Path.GetFileName(root);
        return new SourceInfo(root, name, commit);
    }

    private static string? TryRun(string file, string args, string dir)
    {
        try
        {
            return Run(file, args, dir);
        }
        catch (InvalidOperationException)
        {
            // Not a git repo (or no origin): fine, links just won't be generated.
            return null;
        }
    }

    private static string Run(string file, string args, string dir)
    {
        var info = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        // git reports failure only through the exit code.
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {args} failed: {error.Trim()}");
        return output.Trim();
    }
}
