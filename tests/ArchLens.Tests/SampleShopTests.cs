using ArchLens.Core.Model;

namespace ArchLens.Tests;

// End-to-end checks against the hand-written SampleShop fixture. Each expected value can be
// checked by reading the fixture's source; nothing here is copied from a snapshot.
public class SampleShopTests
{
    private static readonly ArchGraph Graph = TestRepo.AnalyzeSampleShop();

    // The exact arrow list we expect. Comparing sets checks precision and recall at once:
    // an extra arrow or a missing arrow both fail.
    [Fact]
    public void Finds_exactly_the_expected_arrows()
    {
        var expected = new HashSet<string>
        {
            "Serving api:GET api/orders -> fn:OrderService.GetOpenOrders",
            "Serving api:POST api/orders -> fn:OrderService.PlaceOrder",
            "Access/Read api:GET api/orders/count -> do:Order",
            "Serving api:GET api/catalog/items -> fn:CatalogEndpoints.GetItems",
            "Access/Write api:POST api/catalog/items -> do:Product",
            "Access/Read fn:CatalogEndpoints.GetItems -> do:Product",
            "Access/Read fn:OrderService.GetOpenOrders -> do:Order",
            "Serving fn:OrderService.GetOpenOrders -> fn:OrderService.ToDto",
            "Serving fn:OrderService.PlaceOrder -> fn:PricingClient.GetQuote",
            "Access/Write fn:OrderService.PlaceOrder -> do:Order",
            "Serving fn:OrderService.PlaceOrder -> fn:OrderService.ToDto",
            "Access/Write fn:OrderService.CloseOrder -> do:Order",
            "Flow fn:PricingClient.GetQuote -> ext:pricing.example.com",
            "Access/Read fn:PricingClient.GetQuote -> do:PriceQuote",
            "Serving fn:Tick.Hit -> fn:Tock.Hit",
            "Serving fn:Tock.Hit -> fn:Tick.Hit",
            "Aggregation do:Order -> do:OrderLine",
        };
        Assert.Equal(expected.OrderBy(x => x), Graph.EdgeSet().OrderBy(x => x));
    }

    // Every arrow must point at a real line in a real file: no evidence, no arrow.
    [Fact]
    public void Every_arrow_has_evidence_that_exists()
    {
        foreach (var r in Graph.Relationships)
        {
            var path = Path.Combine(TestRepo.SampleShopRoot, r.Evidence.File);
            Assert.True(File.Exists(path), $"{r.Id}: missing file {r.Evidence.File}");
            Assert.InRange(r.Evidence.Line, 1, File.ReadAllLines(path).Length);
        }
    }

    // Controller routes and minimal API routes, including the MapGroup("api/catalog") prefix.
    [Fact]
    public void Finds_every_route_with_its_full_path()
    {
        var routes = Graph.Elements.Where(e => e.Route != null).Select(e => e.Route).OrderBy(r => r);
        Assert.Equal(new[] { "GET api/catalog/items", "GET api/orders", "GET api/orders/count", "POST api/catalog/items", "POST api/orders" }, routes);
    }

    // DI registers OrderService for IOrderService, so the unregistered LegacyOrderService gets no calls.
    [Fact]
    public void Di_registration_picks_the_implementation()
    {
        Assert.DoesNotContain(Graph.Relationships, r => r.To.Contains("LegacyOrderService"));
        var call = Graph.Relationships.Single(r => r.From == "api:GET api/orders");
        Assert.Equal(1.0, call.Confidence);
    }

    // Entity scores follow the signal table: each total and kind here is worked out by hand.
    [Theory]
    [InlineData("do:SampleShop.Data.Entities.Order", 7, EntityKind.Stored, 1.0)]
    [InlineData("do:SampleShop.Data.Entities.OrderLine", 7, EntityKind.Stored, 1.0)]
    [InlineData("do:SampleShop.Data.Entities.Product", 9, EntityKind.Stored, 1.0)]
    [InlineData("do:SampleShop.Api.Contracts.OrderDto", 5, EntityKind.Dto, 1.0)]
    [InlineData("do:SampleShop.Api.Contracts.CreateOrderRequest", 4, EntityKind.Dto, 0.5)]
    [InlineData("do:SampleShop.Api.Contracts.PriceQuote", 4, EntityKind.External, 0.5)]
    public void Scores_entities(string id, int score, EntityKind kind, double confidence)
    {
        var e = Graph.Get(id);
        Assert.Equal(ElementType.DataObject, e.Type);
        Assert.Equal(score, e.EntityScore);
        Assert.Equal(kind, e.EntityKind);
        Assert.Equal(confidence, e.Confidence);
    }

    // OrderLine has no DbSet; it counts as stored only because the migration creates "OrderLines".
    [Fact]
    public void Schema_match_uses_the_plural_table_name()
    {
        Assert.Contains("Schema file match +5", Graph.Get("do:SampleShop.Data.Entities.OrderLine").EntitySignals!);
    }

    // ShippingOptions is only "mostly properties" (+2): unsure, with no kind, waiting for the LLM.
    [Fact]
    public void Weak_signals_land_in_the_unsure_band()
    {
        var e = Graph.Get("do:SampleShop.Data.ShippingOptions");
        Assert.Equal(2, e.EntityScore);
        Assert.Null(e.EntityKind);
        Assert.Equal(0.5, e.Confidence);
    }

    // Box types for the non-data classes.
    [Theory]
    [InlineData("cls:SampleShop.Data.ShopDbContext", ElementType.TechnologyService, 1.0)]
    [InlineData("cls:SampleShop.Api.Controllers.OrdersController", ElementType.ApplicationInterface, 1.0)]
    [InlineData("cls:SampleShop.Api.Services.OrderService", ElementType.ApplicationService, 0.9)]
    [InlineData("cls:SampleShop.Api.Clients.PricingClient", ElementType.ApplicationService, 0.9)]
    [InlineData("cls:SampleShop.Api.Helpers.Tick", ElementType.ApplicationService, 0.5)]
    public void Classifies_classes(string id, ElementType type, double confidence)
    {
        var e = Graph.Get(id);
        Assert.Equal(type, e.Type);
        Assert.Equal(confidence, e.Confidence);
    }

    // The test project and Program.cs's top-level code produce no boxes.
    [Fact]
    public void Skips_test_projects_by_default()
    {
        Assert.DoesNotContain(Graph.Elements, e => e.Id.Contains("Tests"));
        var withTests = TestRepo.AnalyzeSampleShop(includeTests: true);
        Assert.Contains(withTests.Elements, e => e.Id == "cmp:SampleShop.Api.Tests");
    }

    // The traced flow for POST api/orders: controller -> service -> client -> outside, plus the data it touches.
    [Fact]
    public void Traces_the_place_order_flow()
    {
        var view = Graph.Views["callFlow:api:POST api/orders"];
        Assert.Equal(
            new[] { "api:POST api/orders", "fn:OrderService.PlaceOrder", "fn:PricingClient.GetQuote", "ext:pricing.example.com", "do:PriceQuote", "do:Order", "fn:OrderService.ToDto" },
            view.Elements.Select(TestRepo.Short));
        Assert.Equal(6, view.Relationships.Count);
    }

    // 100 - 10 (Tick/Tock cycle) - 5 - 5 (two routes touching tables) - 1 (NeverCalled) = 79.
    [Fact]
    public void Scores_the_architecture()
    {
        Assert.Equal(79, Graph.Score.Total);
        Assert.Equal(
            new[] { "Circular dependency", "Orphan element", "UI touches data directly", "UI touches data directly" },
            Graph.Score.Checks.Select(c => c.Check).OrderBy(c => c));
    }

    // Steps 1-4 have no randomness: two runs give the same JSON apart from the timestamp.
    [Fact]
    public void Same_input_gives_the_same_graph()
    {
        var again = TestRepo.AnalyzeSampleShop();
        string Strip(ArchGraph g) => GraphJson.Write(new ArchGraph
        {
            Repo = g.Repo, Elements = g.Elements, Relationships = g.Relationships, Views = g.Views, Score = g.Score,
        });
        Assert.Equal(Strip(Graph), Strip(again));
    }
}
