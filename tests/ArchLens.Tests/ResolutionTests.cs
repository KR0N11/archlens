using ArchLens.Core.Analysis;
using ArchLens.Core.Model;

namespace ArchLens.Tests;

// How calls through interfaces and abstract methods are resolved, on tiny repos written per test.
public class ResolutionTests
{
    private const string TwoImplementations = """
        namespace App;
        public interface IStore { void Save(); }
        public class SqlStore : IStore { public void Save() { } }
        public class FileStore : IStore { public void Save() { } }
        public class Saver
        {
            private readonly IStore _store;
            public Saver(IStore store) { _store = store; }
            public void Run() { _store.Save(); }
        }
        """;

    // With no DI registration, both implementations could run: draw both, at confidence 0.5.
    [Fact]
    public void Unregistered_interface_call_draws_every_candidate()
    {
        var graph = TestRepo.Analyze(new() { ["Store.cs"] = TwoImplementations });
        var calls = graph.Relationships.Where(r => r.From == "fn:App.Saver.Run").ToList();
        Assert.Equal(new[] { "fn:App.FileStore.Save", "fn:App.SqlStore.Save" }, calls.Select(r => r.To).OrderBy(x => x));
        Assert.All(calls, r => Assert.Equal(0.5, r.Confidence));
    }

    // AddScoped<IStore, SqlStore>() says which one runs: one arrow, full confidence.
    [Fact]
    public void Di_registration_narrows_to_one_candidate()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Store.cs"] = TwoImplementations,
            ["Setup.cs"] = """
                namespace App;
                public static class Setup
                {
                    public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection s)
                    {
                        s.AddScoped<IStore, SqlStore>();
                    }
                }
                """,
        });
        var call = Assert.Single(graph.Relationships, r => r.From == "fn:App.Saver.Run");
        Assert.Equal("fn:App.SqlStore.Save", call.To);
        Assert.Equal(1.0, call.Confidence);
    }

    // mediator.Send(new ShipOrder()) reaches ShipOrderHandler.Handle by the pattern rule.
    // The MediatR interfaces are stubbed here, the same way they are unresolved in a real repo.
    [Fact]
    public void Mediator_send_reaches_the_handler()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Orders.cs"] = """
                namespace App;
                public interface IMediator { Task<T> Send<T>(IRequest<T> request); }
                public interface IRequest<T> { }
                public interface IRequestHandler<TRequest, TResult> { }
                public record ShipOrder(int Id) : IRequest<bool>;
                public class ShipOrderHandler : IRequestHandler<ShipOrder, bool>
                {
                    public Task<bool> Handle(ShipOrder request) { return Task.FromResult(true); }
                }
                public class OrdersEndpoint
                {
                    private readonly IMediator _mediator;
                    public OrdersEndpoint(IMediator mediator) { _mediator = mediator; }
                    public Task<bool> Ship(int id) { return _mediator.Send(new ShipOrder(id)); }
                }
                """,
        });
        Assert.Contains(graph.Relationships, r => r.From == "fn:App.OrdersEndpoint.Ship" && r.To == "fn:App.ShipOrderHandler.Handle");
    }

    // IDisposable is the framework's interface: calling Dispose() must not link to every class that has one.
    [Fact]
    public void Framework_interface_calls_are_not_followed()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Disposables.cs"] = """
                namespace App;
                public class Conn : IDisposable { public void Dispose() { } }
                public class Unrelated : IDisposable { public void Dispose() { } }
                public class User { public void Close(IDisposable d) { d.Dispose(); } }
                """,
        });
        Assert.DoesNotContain(graph.Relationships, r => r.From == "fn:App.User.Close");
    }

    // A call to an abstract method lands on the override.
    [Fact]
    public void Abstract_call_goes_to_the_override()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Shapes.cs"] = """
                namespace App;
                public abstract class Shape { public abstract double Area(); }
                public class Square : Shape { public override double Area() { return 4; } }
                public class Report { public double Total(Shape s) { return s.Area(); } }
                """,
        });
        var call = Assert.Single(graph.Relationships, r => r.From == "fn:App.Report.Total");
        Assert.Equal("fn:App.Square.Area", call.To);
    }

    // Overloads share a name, so their ids carry the parameter list and stay unique.
    [Fact]
    public void Overloads_get_distinct_ids()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Math.cs"] = """
                namespace App;
                public class Calc
                {
                    public int Add(int a, int b) { return a + b; }
                    public double Add(double a, double b) { return a + b; }
                }
                """,
        });
        var ids = graph.Elements.Where(e => e.Type == ElementType.ApplicationFunction).Select(e => e.Id).OrderBy(x => x);
        Assert.Equal(new[] { "fn:App.Calc.Add(double, double)", "fn:App.Calc.Add(int, int)" }, ids);
    }

    // A private method called only from the constructor is not dead code.
    [Fact]
    public void Constructor_calls_count_as_callers()
    {
        var graph = TestRepo.Analyze(new()
        {
            ["Boot.cs"] = """
                namespace App;
                public class Boot
                {
                    public Boot() { Init(); }
                    public void Go() { }
                    private void Init() { }
                }
                """,
        });
        Assert.Contains(graph.Relationships, r => r.From == "cls:App.Boot" && r.To == "fn:App.Boot.Init");
        Assert.DoesNotContain(graph.Score.Checks, c => c.Check == "Orphan element");
    }

    // A class with only constants has no code and no data signals: no box at all.
    [Fact]
    public void Class_with_nothing_to_show_is_skipped()
    {
        var graph = TestRepo.Analyze(new() { ["Consts.cs"] = "namespace App; public static class Consts { public const int Max = 3; }" });
        Assert.DoesNotContain(graph.Elements, e => e.Name == "Consts");
    }

    // Plural table names: Order -> Orders, Box -> Boxes, Category -> Categories.
    [Theory]
    [InlineData("Order", "orders", true)]
    [InlineData("Box", "boxes", true)]
    [InlineData("Category", "categories", true)]
    [InlineData("Order", "orderlines", false)]
    public void Matches_table_names(string className, string table, bool expected)
    {
        Assert.Equal(expected, EntityScorer.MatchesTable(className, new HashSet<string> { table }));
    }

    // Route joining ignores extra slashes on either side.
    [Theory]
    [InlineData("api/catalog", "/items", "api/catalog/items")]
    [InlineData("/api/", "", "api")]
    [InlineData("", "", "/")]
    [InlineData("", "/health", "health")]
    public void Joins_routes(string prefix, string suffix, string expected)
    {
        Assert.Equal(expected, SignalCollector.JoinRoute(prefix, suffix));
    }
}
