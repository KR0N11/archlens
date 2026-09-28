using System.Collections.Concurrent;
using System.Threading.Channels;
using ArchLens.Core.Loading;
using ArchLens.Core.Model;

namespace ArchLens.Api;

public enum AnalysisStatus
{
    Queued,
    Cloning,
    Analyzing,
    Done,
    Failed,
}

// A snapshot of one job. It's a record so every update replaces the whole value:
// a request reading it never sees a half-updated job.
public record JobState(
    string Id,
    string RepoUrl,
    AnalysisStatus Status,
    string Message,
    DateTime StartedAt,
    string? Commit = null,
    string? Error = null,
    bool Cached = false,
    DateTime? FinishedAt = null)
{
    public long ElapsedMs => (long)((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalMilliseconds;
}

// Holds every job, the finished graphs, and the queue the worker reads from. In memory only:
// a restart forgets everything, which is fine for a single-user local tool.
public class JobStore
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly ConcurrentDictionary<string, ArchGraph> _graphs = new();
    // "github.com/owner/name@sha" -> graph, so the same commit is never analyzed twice.
    private readonly ConcurrentDictionary<string, ArchGraph> _byCommit = new();

    public Channel<string> Queue { get; } = Channel.CreateUnbounded<string>();

    public JobState Enqueue(string repoUrl)
    {
        var job = new JobState(Guid.NewGuid().ToString("N")[..12], repoUrl, AnalysisStatus.Queued, "Waiting for the analyzer", DateTime.UtcNow);
        _jobs[job.Id] = job;
        Queue.Writer.TryWrite(job.Id);
        return job;
    }

    public JobState? Get(string id) => _jobs.GetValueOrDefault(id);

    public void Update(JobState job) => _jobs[job.Id] = job;

    public ArchGraph? Graph(string id) => _graphs.GetValueOrDefault(id);

    public void SetGraph(string id, ArchGraph graph) => _graphs[id] = graph;

    public ArchGraph? Cached(string repo, string commit) => _byCommit.GetValueOrDefault($"{repo}@{commit}");

    public void Cache(string repo, string commit, ArchGraph graph) => _byCommit[$"{repo}@{commit}"] = graph;
}

// The two git operations the worker needs. An interface so tests can use a local folder
// instead of the network.
public interface IRepoSource
{
    string LatestCommit(string repo);
    string Clone(string repo, string dir);
}

public class GitRepoSource : IRepoSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public string LatestCommit(string repo) => GitSource.LatestCommit(repo, Timeout);

    public string Clone(string repo, string dir) => GitSource.Clone(repo, dir, Timeout);
}
