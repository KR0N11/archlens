using ArchLens.Core;

namespace ArchLens.Api;

// Runs queued jobs one at a time in the background, so a POST returns right away and
// two big repos never compete for memory. The browser polls the job for progress.
public class AnalysisWorker : BackgroundService
{
    private readonly JobStore _store;
    private readonly IRepoSource _source;
    private readonly ILogger<AnalysisWorker> _log;

    public AnalysisWorker(JobStore store, IRepoSource source, ILogger<AnalysisWorker> log)
    {
        _store = store;
        _source = source;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _store.Queue.Reader.ReadAllAsync(stoppingToken))
        {
            var job = _store.Get(id)!;
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad repo must not stop the worker; the job records why it failed.
                _log.LogWarning(ex, "Analysis of {Repo} failed", job.RepoUrl);
                _store.Update(_store.Get(id)! with { Status = AnalysisStatus.Failed, Message = "Failed", Error = ex.Message, FinishedAt = DateTime.UtcNow });
            }
        }
    }

    public async Task RunAsync(JobState job, CancellationToken ct)
    {
        job = job with { Status = AnalysisStatus.Cloning, Message = "Looking up the latest commit" };
        _store.Update(job);
        var commit = _source.LatestCommit(job.RepoUrl);

        // Same repo, same commit: the graph can't have changed, so skip the clone and the analysis.
        if (_store.Cached(job.RepoUrl, commit) is { } cached)
        {
            _store.SetGraph(job.Id, cached);
            _store.Update(job with { Status = AnalysisStatus.Done, Message = "Done (cached)", Commit = commit, Cached = true, FinishedAt = DateTime.UtcNow });
            return;
        }

        _store.Update(job = job with { Message = "Cloning the repository", Commit = commit });
        var dir = Path.Combine(Path.GetTempPath(), "archlens-api", job.Id);
        var clonedCommit = _source.Clone(job.RepoUrl, dir);
        try
        {
            _store.Update(job = job with { Status = AnalysisStatus.Analyzing, Message = "Building the graph with Roslyn", Commit = clonedCommit });
            var graph = await Analyzer.AnalyzeAsync(dir, job.RepoUrl, clonedCommit, new AnalyzerOptions(), ct: ct);
            // A repo with no .csproj parses to nothing; say so instead of showing an empty canvas.
            if (graph.Elements.Count == 0)
                throw new InvalidOperationException("No C# projects (.csproj) were found in this repository.");
            _store.Cache(job.RepoUrl, clonedCommit, graph);
            _store.SetGraph(job.Id, graph);
            _store.Update(job with
            {
                Status = AnalysisStatus.Done,
                Message = $"Done: {graph.Elements.Count} boxes, {graph.Relationships.Count} arrows",
                FinishedAt = DateTime.UtcNow,
            });
        }
        finally
        {
            // The clone is only needed while analyzing; the graph keeps everything the viewer shows.
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
