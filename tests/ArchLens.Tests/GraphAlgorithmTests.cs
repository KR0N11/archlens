using ArchLens.Core.Model;
using ArchLens.Core.Scoring;
using ArchLens.Core.Tracing;

namespace ArchLens.Tests;

// The tracer, Tarjan and the scorer, on small graphs built by hand.
public class GraphAlgorithmTests
{
    private static Element Fn(string id, string? parent = null, bool isPrivate = false) => new()
    {
        Id = id, Type = ElementType.ApplicationFunction, Layer = Layer.Application, Name = id, Parent = parent,
        IsPrivate = isPrivate ? true : null,
    };

    private static Element Cls(string id) => new() { Id = id, Type = ElementType.ApplicationService, Layer = Layer.Application, Name = id };

    private static Relationship Call(string from, string to, int n) => new()
    {
        Id = "rel:" + n, Type = RelationshipType.Serving, From = from, To = to, Evidence = new SourceRef("x.cs", n),
    };

    // a -> b -> a is a loop; the tracer must stop instead of walking it forever.
    [Fact]
    public void Tracer_survives_a_cycle()
    {
        var graph = new ArchGraph
        {
            Elements = { Fn("a"), Fn("b") },
            Relationships = { Call("a", "b", 1), Call("b", "a", 2) },
        };
        var view = CallFlowTracer.Trace(graph, "a");
        Assert.Equal(new[] { "a", "b" }, view.Elements);
        Assert.Equal(new[] { "rel:1", "rel:2" }, view.Relationships);
    }

    // With max depth 2, a chain a -> b -> c -> d shows a, b, c only.
    [Fact]
    public void Tracer_stops_at_max_depth()
    {
        var graph = new ArchGraph
        {
            Elements = { Fn("a"), Fn("b"), Fn("c"), Fn("d") },
            Relationships = { Call("a", "b", 1), Call("b", "c", 2), Call("c", "d", 3) },
        };
        Assert.Equal(new[] { "a", "b", "c" }, CallFlowTracer.Trace(graph, "a", maxDepth: 2).Elements);
    }

    // Asking for an entry point that doesn't exist is a mistake, not an empty diagram.
    [Fact]
    public void Tracer_rejects_unknown_entry()
    {
        var graph = new ArchGraph { Elements = { Fn("a") } };
        Assert.Throws<KeyNotFoundException>(() => CallFlowTracer.Trace(graph, "nope"));
    }

    // One 3-node loop, one 2-node loop, and a node outside any loop.
    [Fact]
    public void Tarjan_finds_each_loop_once()
    {
        var edges = new Dictionary<string, HashSet<string>>
        {
            ["a"] = new() { "b" }, ["b"] = new() { "c" }, ["c"] = new() { "a", "d" },
            ["d"] = new() { "e" }, ["e"] = new() { "d" }, ["f"] = new() { "a" },
        };
        var loops = Tarjan.StronglyConnected(edges).Where(c => c.Count > 1).Select(c => string.Join(",", c)).OrderBy(x => x);
        Assert.Equal(new[] { "a,b,c", "d,e" }, loops);
    }

    // A straight line has no loops.
    [Fact]
    public void Tarjan_finds_nothing_in_a_chain()
    {
        var edges = new Dictionary<string, HashSet<string>> { ["a"] = new() { "b" }, ["b"] = new() { "c" } };
        Assert.All(Tarjan.StronglyConnected(edges), c => Assert.Single(c));
    }

    // 15 dead private functions would cost 15, but the orphan check is capped at 10.
    [Fact]
    public void Penalties_are_capped_per_check()
    {
        var graph = new ArchGraph();
        for (var i = 0; i < 15; i++)
            graph.Elements.Add(Fn("f" + i, isPrivate: true));
        var score = ArchitectureScorer.Score(graph);
        Assert.Equal(15, score.Checks.Count);
        Assert.Equal(10, score.Checks.Sum(c => c.Penalty));
        Assert.Equal(90, score.Total);
    }

    // A function calling another function of its own class is not a cycle between classes.
    [Fact]
    public void Calls_inside_one_class_are_not_a_cycle()
    {
        var graph = new ArchGraph
        {
            Elements = { Cls("C"), Fn("x", "C"), Fn("y", "C") },
            Relationships = { Call("x", "y", 1), Call("y", "x", 2) },
        };
        Assert.DoesNotContain(ArchitectureScorer.Score(graph).Checks, c => c.Check == "Circular dependency");
    }

    // One class calling 12 functions while every other class calls 1 is flagged; the others aren't.
    [Fact]
    public void God_class_is_far_above_the_median()
    {
        var graph = new ArchGraph();
        graph.Elements.Add(Cls("Big"));
        graph.Elements.Add(Fn("big", "Big"));
        var n = 0;
        for (var i = 0; i < 12; i++)
        {
            graph.Elements.Add(Cls("C" + i));
            graph.Elements.Add(Fn("f" + i, "C" + i));
            graph.Elements.Add(Fn("g" + i, "C" + i));
            graph.Relationships.Add(Call("big", "f" + i, ++n));
            graph.Relationships.Add(Call("f" + i, "g" + (i + 1) % 12, ++n));
        }
        var god = Assert.Single(ArchitectureScorer.Score(graph).Checks, c => c.Check == "God class");
        Assert.Equal(new[] { "Big" }, god.Elements);
    }
}
