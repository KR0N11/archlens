using System.Diagnostics;
using ArchLens.Cli;
using ArchLens.Core;
using ArchLens.Core.Model;

// archlens analyze <folder | github.com/owner/repo> [--out graph.json] [--include-tests] [--max-depth 8] [--enrich] [--model claude-opus-5]
// archlens sample-edges <graph.json> [--count 20] [--seed 1] [--out sample.md]
const string Usage =
    "usage:\n" +
    "  archlens analyze <folder | github.com/owner/repo> [--out graph.json] [--include-tests] [--max-depth N] [--enrich] [--model ID]\n" +
    "  archlens sample-edges <graph.json> [--count 20] [--seed 1] [--out sample.md]";

// Every command needs at least a verb and a target.
if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var options = Options.Parse(args.Skip(2).ToArray());
switch (args[0])
{
    case "analyze":
        return await Analyze(args[1], options);
    case "sample-edges":
        return SampleEdges(args[1], options);
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static async Task<int> Analyze(string target, Options options)
{
    var clock = Stopwatch.StartNew();
    var source = GitSource.Resolve(target);
    // --enrich costs money and needs a key, so it's off unless asked for.
    var labeler = options.Has("--enrich") ? new ClaudeLabelClient(options.Get("--model") ?? "claude-opus-5") : null;
    var graph = await Analyzer.AnalyzeAsync(source.Root, source.Repo, source.Commit, new AnalyzerOptions
    {
        IncludeTests = options.Has("--include-tests"),
        MaxDepth = int.Parse(options.Get("--max-depth") ?? "8"),
    }, labeler);

    var outPath = options.Get("--out") ?? "graph.json";
    File.WriteAllText(outPath, GraphJson.Write(graph));
    var counts = graph.Elements.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
    var rels = graph.Relationships.GroupBy(r => r.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
    Console.WriteLine($"{graph.Repo} @ {graph.Commit ?? "no commit"}");
    Console.WriteLine($"elements: {graph.Elements.Count} ({string.Join(", ", counts)})");
    Console.WriteLine($"relationships: {graph.Relationships.Count} ({string.Join(", ", rels)})");
    Console.WriteLine($"entry points: {graph.Views.Count}, score: {graph.Score.Total}, findings: {graph.Score.Checks.Count}");
    Console.WriteLine($"wrote {outPath} in {clock.Elapsed.TotalSeconds:0.0} s");
    return 0;
}

// Writes a checklist of random arrows with their evidence line, for checking precision by hand.
// A fixed seed means the same graph always gives the same sample, so the check is repeatable.
static int SampleEdges(string graphPath, Options options)
{
    var graph = GraphJson.Read(File.ReadAllText(graphPath));
    var byId = graph.Elements.ToDictionary(e => e.Id);
    var count = int.Parse(options.Get("--count") ?? "20");
    var random = new Random(int.Parse(options.Get("--seed") ?? "1"));
    var sample = graph.Relationships.OrderBy(_ => random.Next()).Take(count).ToList();

    var lines = new List<string>
    {
        $"# Edge sample: {graph.Repo} @ {graph.Commit}",
        "",
        "Open each evidence line and mark the arrow correct only if that line really makes the call / access / HTTP request shown.",
        "",
        "| # | Type | From | To | Evidence | Correct? |",
        "|---|---|---|---|---|---|",
    };
    for (var i = 0; i < sample.Count; i++)
    {
        var r = sample[i];
        var where = $"{r.Evidence.File}:{r.Evidence.Line}";
        // Link to the exact line when we know the GitHub repo and commit.
        if (graph.Repo.StartsWith("github.com/") && graph.Commit != null)
            where = $"[{where}](https://{graph.Repo}/blob/{graph.Commit}/{r.Evidence.File}#L{r.Evidence.Line})";
        var type = r.Access == null ? r.Type.ToString() : $"{r.Type} ({r.Access})";
        lines.Add($"| {i + 1} | {type} | {byId[r.From].Name} | {byId[r.To].Name} | {where} | [ ] |");
    }

    var outPath = options.Get("--out") ?? "edge-sample.md";
    File.WriteAllLines(outPath, lines);
    Console.WriteLine($"wrote {sample.Count} edges to {outPath}");
    return 0;
}

// Tiny flag parser: "--name value" pairs and bare "--flag" switches.
record Options(Dictionary<string, string?> Values)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            // A flag followed by a non-flag takes it as its value; otherwise it's a switch.
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            values[args[i]] = hasValue ? args[++i] : null;
        }
        return new Options(values);
    }

    public bool Has(string name) => Values.ContainsKey(name);

    public string? Get(string name) => Values.GetValueOrDefault(name);
}
