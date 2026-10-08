using System.Diagnostics;
using System.Text.Json;
using ArchLens.Core.Model;

namespace ArchLens.Core.Loading;

// Python repos are read by the Python front end (python/archlens_py, standard library only). It prints
// the same boxes and arrows the C# passes build, so tracing, scoring, the API and the viewer run unchanged.
public static class PythonFrontEnd
{
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".venv", "venv", "__pycache__",
    };

    // Any .csproj means C# (the Roslyn passes are the deeper analysis); otherwise any .py file means Python.
    public static bool IsPythonRepo(string root)
    {
        var sawPython = false;
        foreach (var file in Files(root))
        {
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return false;
            sawPython |= file.EndsWith(".py", StringComparison.Ordinal);
        }
        return sawPython;
    }

    public static async Task<(List<Element> Elements, List<Relationship> Relationships)> RunAsync(
        string root, bool includeTests, CancellationToken ct = default)
    {
        var packageDir = FindPackageDir();
        var python = Environment.GetEnvironmentVariable("ARCHLENS_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = packageDir,
        };
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("archlens_py");
        start.ArgumentList.Add(Path.GetFullPath(root));
        if (includeTests)
            start.ArgumentList.Add("--include-tests");
        start.Environment["PYTHONPATH"] = packageDir;

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {python}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The Python analyzer failed: {(await stderr).Trim()}");

        var output = JsonSerializer.Deserialize<FrontEndOutput>(await stdout, GraphJson.Options)
                     ?? throw new InvalidDataException("The Python analyzer printed nothing.");
        return (output.Elements, output.Relationships);
    }

    private record FrontEndOutput(List<Element> Elements, List<Relationship> Relationships);

    // ARCHLENS_PY_DIR wins; otherwise walk up from the binary to the repo's python/ folder.
    private static string FindPackageDir()
    {
        if (Environment.GetEnvironmentVariable("ARCHLENS_PY_DIR") is { Length: > 0 } configured)
            return configured;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "python");
            if (File.Exists(Path.Combine(candidate, "archlens_py", "__main__.py")))
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not find python/archlens_py; set ARCHLENS_PY_DIR.");
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirs.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
            }
        }
    }
}
