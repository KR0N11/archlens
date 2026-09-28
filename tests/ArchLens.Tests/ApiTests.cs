using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArchLens.Api;
using ArchLens.Core.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArchLens.Tests;

// The web API end to end, in memory, with a fake git source that copies the SampleShop fixture
// instead of cloning over the network.
public class ApiTests : IClassFixture<ApiTests.Factory>
{
    public class FakeSource : IRepoSource
    {
        public int Clones;

        public string LatestCommit(string repo) => "abc123";

        public string Clone(string repo, string dir)
        {
            Interlocked.Increment(ref Clones);
            Copy(TestRepo.SampleShopRoot, dir);
            return "abc123";
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var sub in Directory.GetDirectories(from))
                Copy(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }

    public class Factory : WebApplicationFactory<Program>
    {
        public FakeSource Source { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s => s.AddSingleton<IRepoSource>(Source));
    }

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ApiTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> WaitForFinish(string id)
    {
        // The worker runs in the background; poll like the viewer does, with a time limit.
        for (var i = 0; i < 200; i++)
        {
            var job = await _client.GetFromJsonAsync<JsonElement>($"/api/analyses/{id}");
            if (job.GetProperty("status").GetString() is "done" or "failed")
                return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Analysis did not finish in 10 s.");
    }

    private async Task<string> Start(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/analyses", new { repoUrl = url });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // Paste a link, wait, get a graph: the whole flow the viewer uses.
    [Fact]
    public async Task Analyzes_a_repo_and_serves_its_graph()
    {
        var id = await Start("https://github.com/someone/sampleshop.git");
        var job = await WaitForFinish(id);
        Assert.Equal("done", job.GetProperty("status").GetString());
        Assert.Equal("github.com/someone/sampleshop", job.GetProperty("repoUrl").GetString());

        var graph = GraphJson.Read(await _client.GetStringAsync($"/api/analyses/{id}/graph"));
        Assert.Equal("abc123", graph.Commit);
        Assert.Contains(graph.Elements, e => e.Id == "api:POST api/orders");
    }

    // The same repo at the same commit is served from the cache: no second clone.
    [Fact]
    public async Task Same_commit_is_served_from_cache()
    {
        await WaitForFinish(await Start("github.com/cache/test"));
        var clonesBefore = _factory.Source.Clones;
        var second = await WaitForFinish(await Start("github.com/cache/test"));
        Assert.True(second.GetProperty("cached").GetBoolean());
        Assert.Equal(clonesBefore, _factory.Source.Clones);
    }

    // Anything that isn't a public github.com repo is refused before any work starts.
    [Theory]
    [InlineData("https://gitlab.com/a/b")]
    [InlineData("github.com/only-owner")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public async Task Rejects_non_github_urls(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/analyses", new { repoUrl = url });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Unknown ids are a 404, not an empty job.
    [Fact]
    public async Task Unknown_job_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/analyses/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/analyses/nope/graph")).StatusCode);
    }
}

// When git fails (private or deleted repo), the job ends as "failed" with git's reason, and the
// worker keeps running for the next job.
public class ApiFailureTests : IClassFixture<ApiFailureTests.Factory>
{
    public class BrokenSource : IRepoSource
    {
        public string LatestCommit(string repo) => throw new InvalidOperationException("git ls-remote failed: repository not found");

        public string Clone(string repo, string dir) => throw new InvalidOperationException("unreachable");
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s => s.AddSingleton<IRepoSource>(new BrokenSource()));
    }

    private readonly HttpClient _client;

    public ApiFailureTests(Factory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Failed_clone_is_reported_and_graph_is_a_conflict()
    {
        var post = await _client.PostAsJsonAsync("/api/analyses", new { repoUrl = "github.com/gone/repo" });
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        JsonElement job = default;
        for (var i = 0; i < 200; i++)
        {
            job = await _client.GetFromJsonAsync<JsonElement>($"/api/analyses/{id}");
            if (job.GetProperty("status").GetString() == "failed")
                break;
            await Task.Delay(50);
        }
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("repository not found", job.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync($"/api/analyses/{id}/graph")).StatusCode);
    }
}
