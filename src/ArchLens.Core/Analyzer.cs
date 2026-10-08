using ArchLens.Core.Analysis;
using ArchLens.Core.Enrichment;
using ArchLens.Core.Loading;
using ArchLens.Core.Model;
using ArchLens.Core.Scoring;
using ArchLens.Core.Tracing;

namespace ArchLens.Core;

public class AnalyzerOptions
{
    public bool IncludeTests { get; init; }
    public int MaxDepth { get; init; } = CallFlowTracer.DefaultMaxDepth;
}

// The pipeline in one place: load, collect facts, build the graph, (optionally) let the
// LLM label the leftovers, trace the call flows, score. Only the LLM step is non-deterministic,
// so without it the same commit always gives the same graph. Python repos get their boxes and
// arrows from the Python front end instead of Roslyn; tracing and scoring are shared.
public static class Analyzer
{
    public static async Task<ArchGraph> AnalyzeAsync(string root, string repo, string? commit, AnalyzerOptions options,
        ILabelClient? labeler = null, CancellationToken ct = default)
    {
        List<Element> elements;
        List<Relationship> relationships;
        RepoIndex? index = null;
        if (PythonFrontEnd.IsPythonRepo(root))
        {
            (elements, relationships) = await PythonFrontEnd.RunAsync(root, options.IncludeTests, ct);
        }
        else
        {
            var projects = RepoLoader.Load(root, options.IncludeTests);
            index = SignalCollector.Collect(projects, root);
            (elements, relationships) = GraphBuilder.Build(projects, index);
        }
        var graph = new ArchGraph
        {
            Repo = repo,
            Commit = commit,
            AnalyzedAt = DateTime.UtcNow,
            Elements = elements,
            Relationships = relationships,
        };

        // The LLM only runs when asked for, and only on boxes the rules marked as unsure. It reads
        // Roslyn's index for context, so it is C#-only for now.
        if (labeler != null && index != null)
            await Enricher.EnrichAsync(graph, index, labeler, ct);

        foreach (var (key, view) in CallFlowTracer.TraceAllEntryPoints(graph, options.MaxDepth))
            graph.Views[key] = view;
        graph.Score = ArchitectureScorer.Score(graph);
        return graph;
    }
}
