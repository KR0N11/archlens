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
// so without it the same commit always gives the same graph.
public static class Analyzer
{
    public static async Task<ArchGraph> AnalyzeAsync(string root, string repo, string? commit, AnalyzerOptions options,
        ILabelClient? labeler = null, CancellationToken ct = default)
    {
        var projects = RepoLoader.Load(root, options.IncludeTests);
        var index = SignalCollector.Collect(projects, root);
        var (elements, relationships) = GraphBuilder.Build(projects, index);
        var graph = new ArchGraph
        {
            Repo = repo,
            Commit = commit,
            AnalyzedAt = DateTime.UtcNow,
            Elements = elements,
            Relationships = relationships,
        };

        // The LLM only runs when asked for, and only on boxes the rules marked as unsure.
        if (labeler != null)
            await Enricher.EnrichAsync(graph, index, labeler, ct);

        foreach (var (key, view) in CallFlowTracer.TraceAllEntryPoints(graph, options.MaxDepth))
            graph.Views[key] = view;
        graph.Score = ArchitectureScorer.Score(graph);
        return graph;
    }
}
