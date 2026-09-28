using System.Text.RegularExpressions;
using ArchLens.Core.Loading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Core.Analysis;

// Pass 1: walks every file once and records facts (which types are DbSets, which are
// DI-registered, where the routes are...). Nothing is classified yet; that needs the
// whole repo's facts first, because a class's usage can be in a different file.
public static class SignalCollector
{
    private static readonly Dictionary<string, string> MapVerbs = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    private static readonly Dictionary<string, string> HttpAttributes = new()
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE", ["HttpPatch"] = "PATCH",
    };

    private static readonly HashSet<string> DiMethods = new()
    {
        "AddScoped", "AddTransient", "AddSingleton", "TryAddScoped", "TryAddTransient", "TryAddSingleton",
        "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton", "AddHttpClient",
    };

    private static readonly HashSet<string> SerializerAttributes = new()
    {
        "JsonProperty", "JsonPropertyName", "DataContract", "DataMember",
    };

    private static readonly Regex SqlCreateTable = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([\w\.\[\]""`]+)", RegexOptions.IgnoreCase);

    public static RepoIndex Collect(IReadOnlyList<LoadedProject> projects, string root)
    {
        var index = new RepoIndex { Root = root };
        foreach (var project in projects)
        {
            foreach (var tree in project.Compilation.SyntaxTrees)
            {
                var model = project.Compilation.GetSemanticModel(tree);
                var rootNode = tree.GetRoot();
                foreach (var decl in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
                    CollectType(index, project, decl, model);
                foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    CollectInvocation(index, project, invocation, model);
                foreach (var generic in rootNode.DescendantNodes().OfType<GenericNameSyntax>())
                {
                    // IRepository<Order> anywhere in the code means Order is kept in a repository.
                    if (generic.Identifier.Text.EndsWith("Repository", StringComparison.Ordinal))
                    {
                        foreach (var arg in generic.TypeArgumentList.Arguments)
                            AddTypeAndArguments(index.RepositoryTypes, model.GetTypeInfo(arg).Type);
                    }
                }
            }
        }

        // Controllers are known only after every type is seen, so their routes come last.
        foreach (var type in index.Types.Values.Where(t => index.Controllers.Contains(Keys.Of(t.Symbol))))
            CollectControllerRoutes(index, type);

        foreach (var sql in Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories))
        {
            foreach (Match match in SqlCreateTable.Matches(File.ReadAllText(sql)))
                index.SchemaTables.Add(CleanTableName(match.Groups[1].Value));
        }
        return index;
    }

    private static void CollectType(RepoIndex index, LoadedProject project, TypeDeclarationSyntax decl, SemanticModel model)
    {
        // Should not happen for a type declaration, but Roslyn's API allows null.
        if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol)
            return;
        var key = Keys.Of(symbol);

        // A repository's methods take and return the data it stores, interfaces included.
        if (symbol.Name.EndsWith("Repository", StringComparison.Ordinal))
        {
            foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
            {
                AddTypeAndArguments(index.RepositoryTypes, method.ReturnType);
                foreach (var p in method.Parameters)
                    AddTypeAndArguments(index.RepositoryTypes, p.Type);
            }
        }

        // Interfaces never become boxes; calls through them are followed to the implementation instead.
        if (symbol.TypeKind == TypeKind.Interface)
            return;
        // A partial class has several declarations; the first one found is where it "lives".
        if (index.Types.ContainsKey(key))
            return;
        index.Types[key] = new TypeInfo(symbol, project, decl, model);

        if (IsDbContext(symbol))
            index.DbContexts.Add(key);
        if (InheritsFrom(symbol, "ControllerBase") || InheritsFrom(symbol, "Controller") || HasAttribute(symbol, "ApiController"))
            index.Controllers.Add(key);

        foreach (var property in symbol.GetMembers().OfType<IPropertySymbol>())
        {
            // DbSet<Order> on a context: EF will map Order to a table.
            if (property.Type is INamedTypeSymbol { Name: "DbSet", TypeArguments.Length: 1 } dbSet)
                index.OrmTypes.Add(Keys.Of(dbSet.TypeArguments[0]));
        }

        var members = symbol.GetMembers();
        if (HasAnyAttribute(symbol, SerializerAttributes) || members.Any(m => HasAnyAttribute(m, SerializerAttributes)))
            index.SerializerAttributed.Add(key);
    }

    private static void CollectInvocation(RepoIndex index, LoadedProject project, InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var name = MethodName(invocation.Expression);
        // A call through a delegate or indexer has no simple name to match on.
        if (name == null)
            return;
        var typeArgs = TypeArguments(invocation.Expression, model);

        if (MapVerbs.TryGetValue(name.Identifier.Text, out var verb))
        {
            CollectMinimalApiRoute(index, project, invocation, model, verb);
            return;
        }

        switch (name.Identifier.Text)
        {
            // Only the generic form (AddScoped<IX, X>()) names the types in a way we can read statically.
            case var n when DiMethods.Contains(n) && typeArgs.Count > 0:
                foreach (var t in typeArgs)
                    index.DiRegistered.Add(Keys.Of(t));
                // Two type arguments means "interface, implementation".
                if (typeArgs.Count == 2)
                {
                    var iface = Keys.Of(typeArgs[0]);
                    if (!index.DiImplementations.TryGetValue(iface, out var impls))
                        index.DiImplementations[iface] = impls = new HashSet<string>();
                    impls.Add(Keys.Of(typeArgs[1]));
                }
                // AddHttpClient<T>(c => c.BaseAddress = new Uri("https://x")) tells us who T talks to.
                if (n == "AddHttpClient" && FindHost(invocation.ArgumentList) is { } host)
                {
                    foreach (var t in typeArgs)
                        index.HttpClientHosts[Keys.Of(t)] = host;
                }
                break;
            case "GetFromJsonAsync" or "ReadFromJsonAsync" or "GetFromJsonAsAsyncEnumerable" when typeArgs.Count == 1:
                AddTypeAndArguments(index.ExternalTypes, typeArgs[0]);
                AddTypeAndArguments(index.WireTypes, typeArgs[0]);
                break;
            // context.Set<Order>() is the other way EF exposes a table.
            case "Set" when typeArgs.Count == 1 && invocation.ArgumentList.Arguments.Count == 0:
                index.OrmTypes.Add(Keys.Of(typeArgs[0]));
                break;
            case "CreateTable":
                var tableArg = invocation.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "name")
                    ?? invocation.ArgumentList.Arguments.FirstOrDefault();
                // Only a table name we can read as a constant counts; a computed name tells us nothing.
                if (tableArg != null && model.GetConstantValue(tableArg.Expression) is { HasValue: true, Value: string table })
                    index.SchemaTables.Add(CleanTableName(table));
                break;
        }
    }

    private static void CollectMinimalApiRoute(RepoIndex index, LoadedProject project, InvocationExpressionSyntax invocation, SemanticModel model, string verb)
    {
        // Repos often wrap MapGet in their own helper with the arguments swapped, so the pattern and
        // handler are found by kind (a constant string, a lambda or method name), not by position.
        string? pattern = null;
        ExpressionSyntax? handler = null;
        IMethodSymbol? method = null;
        foreach (var arg in invocation.ArgumentList.Arguments.Select(a => a.Expression))
        {
            if (pattern == null && model.GetConstantValue(arg) is { HasValue: true, Value: string p })
                pattern = p;
            else if (handler == null && arg is AnonymousFunctionExpressionSyntax)
                handler = arg;
            else if (handler == null && SingleMethod(model.GetSymbolInfo(arg)) is { } m)
                (handler, method) = (arg, m);
        }
        // No visible handler (a helper passing a delegate variable along): not an endpoint itself.
        if (handler == null)
            return;

        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        var prefix = receiver == null ? "" : GroupPrefix(receiver, model, depth: 0);
        var path = JoinRoute(prefix, pattern ?? "");
        var declaringType = model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        index.RouteHandlerNodes.Add(handler);
        var lambda = handler as AnonymousFunctionExpressionSyntax;

        var route = new RouteInfo(verb, path, project, invocation, model, method, lambda, declaringType);
        index.Routes.Add(route);

        var signature = method ?? (lambda != null ? model.GetSymbolInfo(lambda).Symbol as IMethodSymbol : null);
        // The handler's parameters and return type are what crosses the wire.
        if (signature != null)
            AddSignatureToWire(index, signature);
    }

    private static void CollectControllerRoutes(RepoIndex index, TypeInfo controller)
    {
        var shortName = controller.Symbol.Name.EndsWith("Controller")
            ? controller.Symbol.Name[..^"Controller".Length]
            : controller.Symbol.Name;
        // No [Route] on the class means MVC's conventional route: {controller}/{action}.
        var conventional = RouteTemplate(controller.Symbol, "Route") == null;
        var classRoute = (RouteTemplate(controller.Symbol, "Route") ?? "").Replace("[controller]", shortName);

        foreach (var method in controller.Symbol.GetMembers().OfType<IMethodSymbol>())
        {
            // Only public instance methods are actions; [NonAction] opts a public method out.
            if (method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public
                || method.IsStatic || HasAttribute(method, "NonAction"))
                continue;
            var verb = "ANY";
            string? template = null;
            foreach (var attr in method.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name.Replace("Attribute", "") ?? "";
                // The first [HttpGet]/[HttpPost]/... decides the verb and the route suffix.
                if (HttpAttributes.TryGetValue(attrName, out var v))
                {
                    verb = v;
                    template = attr.ConstructorArguments.FirstOrDefault().Value as string;
                    break;
                }
            }
            // Conventional routing fills in the controller and action names when the attribute gives no template.
            if (conventional && string.IsNullOrEmpty(template))
                template = $"{shortName}/{method.Name}";
            template = (template ?? "").Replace("[action]", method.Name);
            // A template starting with "/" or "~/" ignores the controller's prefix.
            var path = template.StartsWith('/') || template.StartsWith("~/")
                ? JoinRoute("", template.TrimStart('~'))
                : JoinRoute(classRoute, template);

            var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            // A method we can't see the source of (compiler-generated) has nothing to walk.
            if (syntax == null)
                continue;
            var model = controller.Project.Compilation.GetSemanticModel(syntax.SyntaxTree);
            index.Routes.Add(new RouteInfo(verb, path, controller.Project, syntax, model, method, null, controller.Symbol));
            AddSignatureToWire(index, method);
        }
    }

    private static void AddSignatureToWire(RepoIndex index, IMethodSymbol method)
    {
        AddTypeAndArguments(index.WireTypes, method.ReturnType);
        foreach (var p in method.Parameters)
        {
            // [FromServices] parameters are injected services, not request data.
            if (!HasAttribute(p, "FromServices"))
                AddTypeAndArguments(index.WireTypes, p.Type);
        }
    }

    // Follows app.MapGroup("api").MapGroup("catalog") chains, and a local variable that holds
    // one, to build the route prefix. Depth-limited so a strange chain can't loop forever.
    private static string GroupPrefix(ExpressionSyntax expr, SemanticModel model, int depth)
    {
        if (depth > 10)
            return "";
        switch (expr)
        {
            case InvocationExpressionSyntax inv:
                var inner = (inv.Expression as MemberAccessExpressionSyntax)?.Expression;
                var outer = inner == null ? "" : GroupPrefix(inner, model, depth + 1);
                // Only MapGroup adds to the path; other calls in the chain (WithTags, HasApiVersion) pass through.
                if (MethodName(inv.Expression)?.Identifier.Text == "MapGroup" && inv.ArgumentList.Arguments.Count > 0
                    && model.GetConstantValue(inv.ArgumentList.Arguments[0].Expression) is { HasValue: true, Value: string g })
                    return JoinRoute(outer, g);
                return outer;
            case IdentifierNameSyntax id:
                // var api = app.MapGroup("api/catalog"); ... api.MapGet(...)
                if (model.GetSymbolInfo(id).Symbol is ILocalSymbol local
                    && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } init })
                    return GroupPrefix(init.Value, model, depth + 1);
                return "";
            default:
                return "";
        }
    }

    public static string JoinRoute(string prefix, string suffix)
    {
        var parts = (prefix + "/" + suffix).Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The site root has no segments; show it as "/" rather than an empty string.
        return parts.Length == 0 ? "/" : string.Join("/", parts);
    }

    private static string? RouteTemplate(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name.Replace("Attribute", "") == attributeName)
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static string? FindHost(ArgumentListSyntax args)
    {
        foreach (var literal in args.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            // The first absolute URL in the arguments is taken as the base address.
            if (literal.Token.Value is string text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                return uri.Host;
        }
        return null;
    }

    public static SimpleNameSyntax? MethodName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name,
        SimpleNameSyntax simple => simple,
        MemberBindingExpressionSyntax binding => binding.Name,
        _ => null,
    };

    private static List<ITypeSymbol> TypeArguments(ExpressionSyntax expression, SemanticModel model)
    {
        // Only a written-out Foo<T>() counts; inferred type arguments are skipped on purpose,
        // because they're only reliable when every package type resolves.
        if (MethodName(expression) is not GenericNameSyntax generic)
            return new List<ITypeSymbol>();
        return generic.TypeArgumentList.Arguments
            .Select(a => model.GetTypeInfo(a).Type)
            .Where(t => t != null)
            .Select(t => t!)
            .ToList();
    }

    // Records a type plus every type nested in it, so Task<ActionResult<List<OrderDto>>> yields OrderDto.
    public static void AddTypeAndArguments(HashSet<string> set, ITypeSymbol? type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                AddTypeAndArguments(set, array.ElementType);
                break;
            case INamedTypeSymbol named:
                set.Add(Keys.Of(named));
                foreach (var arg in named.TypeArguments)
                    AddTypeAndArguments(set, arg);
                break;
        }
    }

    public static IMethodSymbol? SingleMethod(SymbolInfo info)
    {
        // Resolved cleanly: use it.
        if (info.Symbol is IMethodSymbol m)
            return m;
        // Not resolved (often because a package type is missing), but only one method fits: use that.
        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static bool InheritsFrom(INamedTypeSymbol symbol, string baseName)
    {
        // Matched by name so it works even when the base class's package isn't loaded.
        for (var t = symbol.BaseType; t != null; t = t.BaseType)
        {
            if (t.Name == baseName)
                return true;
        }
        return false;
    }

    // DbContext, or a subclass shipped in a package we don't load (IdentityDbContext<TUser>).
    public static bool IsDbContext(INamedTypeSymbol symbol)
    {
        for (var t = symbol.BaseType; t != null; t = t.BaseType)
        {
            if (t.Name.EndsWith("DbContext", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name.Replace("Attribute", "") == name);

    private static bool HasAnyAttribute(ISymbol symbol, HashSet<string> names) =>
        symbol.GetAttributes().Any(a => names.Contains(a.AttributeClass?.Name.Replace("Attribute", "") ?? ""));

    private static string CleanTableName(string raw)
    {
        var last = raw.Split('.').Last();
        return last.Trim('[', ']', '"', '`').ToLowerInvariant();
    }
}
