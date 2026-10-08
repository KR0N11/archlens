using ArchLens.Core;
using ArchLens.Core.Loading;
using ArchLens.Core.Model;

namespace ArchLens.Tests;

// The Python front end end to end: the C# pipeline detects a Python repo, runs python/archlens_py,
// then traces and scores its boxes and arrows with the same code it uses for C#. The arrow-by-arrow
// checks live in python/tests; these check the hand-off and what the C# side adds on top.
public class SamplePyShopTests
{
    private static string Root => Path.Combine(Path.GetDirectoryName(TestRepo.SampleShopRoot)!, "SamplePyShop");

    private static readonly ArchGraph Graph =
        Analyzer.AnalyzeAsync(Root, "SamplePyShop", null, new AnalyzerOptions()).Result;

    [Fact]
    public void Detects_the_language_from_the_files()
    {
        Assert.True(PythonFrontEnd.IsPythonRepo(Root));
        Assert.False(PythonFrontEnd.IsPythonRepo(TestRepo.SampleShopRoot));
    }

    // include_router(prefix="/api") + APIRouter(prefix="/orders") resolve to the same route ids C# uses.
    [Fact]
    public void Finds_every_route_with_its_full_path()
    {
        var routes = Graph.Elements.Where(e => e.Route != null).Select(e => e.Route).OrderBy(r => r);
        Assert.Equal(new[] { "GET api/catalog/items", "GET api/orders", "GET api/orders/count", "GET,DELETE api/catalog/items/{item_id}", "POST api/catalog/items", "POST api/orders" }, routes);
    }

    // POST api/orders: Depends provider first, then the service, its pricing client and the outside call.
    [Fact]
    public void Traces_the_place_order_flow()
    {
        var view = Graph.Views["callFlow:api:POST api/orders"];
        Assert.Equal(
            new[]
            {
                "api:POST api/orders", "fn:deps.get_order_service", "fn:db.get_session", "fn:OrderService.place_order",
                "fn:PricingClient.get_quote", "ext:pricing.example.com", "do:PriceQuote", "do:Order", "fn:OrderService._to_out",
            },
            view.Elements.Select(TestRepo.Short));
        Assert.Equal(8, view.Relationships.Count);
    }

    // Same checks and the same total as the C# SampleShop: 100 - 10 (tick/tock modules call each other)
    // - 5 - 5 (two routes touching tables) - 1 (_never_called) = 79.
    [Fact]
    public void Scores_the_architecture()
    {
        Assert.Equal(79, Graph.Score.Total);
        Assert.Equal(
            new[] { "Circular dependency", "Orphan element", "UI touches data directly", "UI touches data directly" },
            Graph.Score.Checks.Select(c => c.Check).OrderBy(c => c));
    }

    // Enums and confidences come through the JSON hand-off intact.
    [Fact]
    public void Keeps_types_and_confidence_across_the_hand_off()
    {
        var order = Graph.Elements.Single(e => e.Id == "do:shop.models.Order");
        Assert.Equal((ElementType.DataObject, EntityKind.Stored, 12), (order.Type, order.EntityKind!.Value, order.EntityScore!.Value));
        var unsure = Graph.Relationships.Where(r => r.From.EndsWith("OrderService.checkout")).Select(r => r.Confidence);
        Assert.Equal(new[] { 0.5, 0.5 }, unsure);
    }

    [Fact]
    public void Same_input_gives_the_same_graph()
    {
        var again = Analyzer.AnalyzeAsync(Root, "SamplePyShop", null, new AnalyzerOptions()).Result;
        string Strip(ArchGraph g) => GraphJson.Write(new ArchGraph
        {
            Repo = g.Repo, Elements = g.Elements, Relationships = g.Relationships, Views = g.Views, Score = g.Score,
        });
        Assert.Equal(Strip(Graph), Strip(again));
    }
}
