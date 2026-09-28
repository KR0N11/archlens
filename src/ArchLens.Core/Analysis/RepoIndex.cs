using ArchLens.Core.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Core.Analysis;

// A class, record or struct declared in the repo, with where it lives.
public record TypeInfo(INamedTypeSymbol Symbol, LoadedProject Project, SyntaxNode Declaration, SemanticModel Model);

// An HTTP entry point found in the code. The handler is either a named method
// (MapGet("/items", GetItems) or a controller action) or an inline lambda.
public record RouteInfo(
    string Verb,
    string Path,
    LoadedProject Project,
    SyntaxNode Site,
    SemanticModel Model,
    IMethodSymbol? HandlerMethod,
    AnonymousFunctionExpressionSyntax? HandlerLambda,
    INamedTypeSymbol? DeclaringType);

// Everything the first pass learns about the repo. Later passes only read it.
public class RepoIndex
{
    public string Root { get; init; } = "";
    public Dictionary<string, TypeInfo> Types { get; } = new();
    public List<RouteInfo> Routes { get; } = new();

    // Types used as DbSet<T> or context.Set<T>() (the ORM signal).
    public HashSet<string> OrmTypes { get; } = new();
    // Classes that derive from DbContext: the database gateway.
    public HashSet<string> DbContexts { get; } = new();
    public HashSet<string> Controllers { get; } = new();
    // Table names read from migrations and .sql files, lower case.
    public HashSet<string> SchemaTables { get; } = new();
    public HashSet<string> RepositoryTypes { get; } = new();
    // Types that cross an HTTP boundary as a parameter or return value.
    public HashSet<string> WireTypes { get; } = new();
    // Types read out of an HTTP response body (GetFromJsonAsync<T>).
    public HashSet<string> ExternalTypes { get; } = new();
    // Types that carry [JsonProperty], [JsonPropertyName] or [DataContract].
    public HashSet<string> SerializerAttributed { get; } = new();
    public HashSet<string> DiRegistered { get; } = new();
    // Interface -> implementations registered with AddScoped<IX, X>() and friends.
    public Dictionary<string, HashSet<string>> DiImplementations { get; } = new();
    // Typed HttpClient class -> host of its BaseAddress, from AddHttpClient<T>(c => c.BaseAddress = ...).
    public Dictionary<string, string> HttpClientHosts { get; } = new();
    // Lambdas and method-group arguments that belong to a route, so the enclosing
    // method (the one calling MapGet) doesn't get their calls too.
    public HashSet<SyntaxNode> RouteHandlerNodes { get; } = new();
}

public static class Keys
{
    // A stable text key for a symbol, e.g. "Shop.OrderService.GetOrders(int)".
    // Text keys, not symbol objects, because the same type seen from two projects
    // can be two different symbol objects.
    public static string Of(ISymbol symbol)
    {
        // An extension method called as x.Foo() shows up in "reduced" form; key the original.
        if (symbol is IMethodSymbol { ReducedFrom: { } original })
            symbol = original;
        return symbol.OriginalDefinition.ToDisplayString();
    }
}
