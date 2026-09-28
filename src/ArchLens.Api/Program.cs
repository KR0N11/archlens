using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Api;
using ArchLens.Core.Loading;
using ArchLens.Core.Model;

var builder = WebApplication.CreateBuilder(args);
// Fixed local port so the viewer's dev proxy knows where to find us; ASPNETCORE_URLS overrides it.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5080");
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<IRepoSource, GitRepoSource>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

// Starts an analysis and returns at once; the work happens in AnalysisWorker.
app.MapPost("/api/analyses", (AnalyzeRequest request, JobStore store) =>
{
    var repo = GitSource.NormalizeGitHub(request.RepoUrl ?? "");
    // Only public github.com repos: we never clone from a host the user typed freely.
    if (repo == null)
        return Results.BadRequest(new { error = "Paste a public GitHub repo URL, like github.com/dotnet/eShop." });
    var job = store.Enqueue(repo);
    return Results.Accepted($"/api/analyses/{job.Id}", new { id = job.Id, status = job.Status });
});

app.MapGet("/api/analyses/{id}", (string id, JobStore store) =>
    store.Get(id) is { } job ? Results.Ok(job) : Results.NotFound(new { error = $"No analysis with id {id}." }));

// The graph is written with the analyzer's own JSON settings, the same format the CLI saves.
app.MapGet("/api/analyses/{id}/graph", (string id, JobStore store) =>
{
    if (store.Get(id) == null)
        return Results.NotFound(new { error = $"No analysis with id {id}." });
    return store.Graph(id) is { } graph
        ? Results.Text(GraphJson.Write(graph), "application/json")
        : Results.Conflict(new { error = "The analysis hasn't finished yet." });
});

app.Run();

public record AnalyzeRequest(string? RepoUrl);

// Lets the test project start the API in memory with WebApplicationFactory<Program>.
public partial class Program;
