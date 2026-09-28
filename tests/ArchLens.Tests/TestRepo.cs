using ArchLens.Core;
using ArchLens.Core.Model;

namespace ArchLens.Tests;

// Helpers to analyze either the SampleShop fixture or a tiny repo written on the fly.
public static class TestRepo
{
    public static string SampleShopRoot
    {
        get
        {
            // Walk up from bin/Debug/net10.0 until we find the repo's samples folder.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "SampleShop")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "samples", "SampleShop");
        }
    }

    public static ArchGraph AnalyzeSampleShop(bool includeTests = false) =>
        Analyzer.AnalyzeAsync(SampleShopRoot, "SampleShop", null, new AnalyzerOptions { IncludeTests = includeTests }).Result;

    // Writes one project called "App" with the given files into a fresh temp folder.
    public static string Write(Dictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "archlens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        foreach (var (name, text) in files)
            File.WriteAllText(Path.Combine(root, "App", name), text);
        return root;
    }

    public static ArchGraph Analyze(Dictionary<string, string> files) =>
        Analyzer.AnalyzeAsync(Write(files), "test", null, new AnalyzerOptions()).Result;

    public static Element Get(this ArchGraph graph, string id) => graph.Elements.Single(e => e.Id == id);

    // "Type From -> To" strings make edge sets easy to compare and easy to read in a failure message.
    public static HashSet<string> EdgeSet(this ArchGraph graph) =>
        graph.Relationships
            .Select(r => $"{r.Type}{(r.Access == null ? "" : "/" + r.Access)} {Short(r.From)} -> {Short(r.To)}")
            .ToHashSet();

    // Drops the namespace so "fn:SampleShop.Api.Services.OrderService.ToDto" reads "fn:OrderService.ToDto".
    public static string Short(string id)
    {
        var colon = id.IndexOf(':');
        var prefix = id[..(colon + 1)];
        var rest = id[(colon + 1)..];
        // Routes and external systems have no namespace to drop.
        if (prefix is "api:" or "ext:" or "cmp:")
            return id;
        var parts = rest.Split('.');
        return prefix + (prefix == "fn:" ? string.Join(".", parts[^2..]) : parts[^1]);
    }
}
