using ArchLens.Core.Loading;
using ArchLens.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Core.Analysis;

// Pass 3: turns the index into boxes (elements) and arrows (relationships).
// Every arrow comes from a line of code we can point at; nothing here is guessed.
public class GraphBuilder
{
    private static readonly HashSet<string> HttpMethods = new()
    {
        "GetAsync", "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync", "SendAsync",
        "GetStringAsync", "GetStreamAsync", "GetByteArrayAsync",
        "GetFromJsonAsync", "GetFromJsonAsAsyncEnumerable", "PostAsJsonAsync", "PutAsJsonAsync",
        "PatchAsJsonAsync", "DeleteFromJsonAsync",
    };

    // Calling one of these on a DbSet changes the table; anything else only reads it.
    private static readonly HashSet<string> DbWriteMethods = new()
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync", "Update", "UpdateRange",
        "Remove", "RemoveRange", "Attach", "AttachRange",
    };

    private static readonly HashSet<string> CollectionNames = new()
    {
        "List", "IList", "ICollection", "IEnumerable", "IReadOnlyCollection", "IReadOnlyList",
        "HashSet", "ISet", "Collection",
    };

    private readonly RepoIndex _index;
    private readonly List<Element> _elements = new();
    private readonly Dictionary<string, Element> _byId = new();
    private readonly List<Relationship> _relationships = new();
    private readonly HashSet<(string, string, RelationshipType, AccessKind?)> _edgeKeys = new();
    // Symbol key -> element id, for classes and for methods.
    private readonly Dictionary<string, string> _typeElements = new();
    private readonly Dictionary<string, string> _methodElements = new();
    private readonly Dictionary<string, List<(IMethodSymbol Method, double Confidence)>> _targetCache = new();
    private readonly Dictionary<string, List<string>> _handlerCache = new();
    // Bodies to walk once every box exists: (owner element id, code, semantic model).
    private readonly List<(string Owner, SyntaxNode Body, SemanticModel Model)> _bodies = new();

    private GraphBuilder(RepoIndex index)
    {
        _index = index;
    }

    public static (List<Element> Elements, List<Relationship> Relationships) Build(IReadOnlyList<LoadedProject> projects, RepoIndex index)
    {
        var builder = new GraphBuilder(index);
        builder.AddComponents(projects);
        builder.AddClassesAndFunctions();
        builder.AddRoutes();
        foreach (var (owner, body, model) in builder._bodies)
            builder.WalkBody(owner, body, model);
        builder.AddAggregations();
        return (builder._elements, builder._relationships);
    }

    private static string ComponentId(LoadedProject project) => "cmp:" + project.Name;

    private void AddComponents(IReadOnlyList<LoadedProject> projects)
    {
        foreach (var project in projects)
        {
            AddElement(new Element
            {
                Id = ComponentId(project),
                Type = ElementType.ApplicationComponent,
                Layer = Layer.Application,
                Name = project.Name,
                Source = new SourceRef(Path.Combine(project.Directory, project.Name + ".csproj").Replace('\\', '/'), 1),
            });
        }
    }

    private void AddClassesAndFunctions()
    {
        foreach (var (key, type) in _index.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            // Generated files (designer, source generators) are not the design.
            if (IsGenerated(type.Declaration.SyntaxTree.FilePath))
                continue;
            var result = TypeClassifier.Classify(type, _index);
            // null means "no code and no data signals": nothing worth drawing.
            if (result == null)
                continue;

            var isData = result.Type == ElementType.DataObject;
            var element = new Element
            {
                Id = (isData ? "do:" : "cls:") + key,
                Type = result.Type,
                Layer = result.Layer,
                Name = type.Symbol.Name,
                Parent = ComponentId(type.Project),
                Source = SourceOf(type.Declaration is TypeDeclarationSyntax t ? t.Identifier.GetLocation() : type.Declaration.GetLocation()),
                Confidence = result.Confidence,
                EntityKind = result.Entity?.Kind,
                EntityScore = result.Entity?.Score,
                EntitySignals = result.Entity?.Signals,
                Fields = isData ? FieldsOf(type.Symbol) : null,
            };
            AddElement(element);
            _typeElements[key] = element.Id;

            // Only classes that do work get function boxes; entities and the DbContext don't.
            if (result.Type is ElementType.ApplicationService or ElementType.ApplicationInterface)
                AddFunctions(type, element.Id);
        }
    }

    private void AddFunctions(TypeInfo type, string classId)
    {
        var isController = _index.Controllers.Contains(Keys.Of(type.Symbol));
        var methods = type.Symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared && !m.IsAbstract)
            .ToList();
        foreach (var method in methods)
        {
            // Controller actions become route boxes in AddRoutes instead.
            if (isController && IsControllerAction(method))
                continue;
            var syntax = method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(s => s.Body != null || s.ExpressionBody != null);
            // No body in source (a partial method's declaration half, an extern): nothing to walk.
            if (syntax == null)
                continue;
            // Overloads share a name, so they need the parameter list to stay unique.
            var overloaded = methods.Count(m => m.Name == method.Name) > 1;
            var id = "fn:" + (overloaded ? Keys.Of(method) : Keys.Of(type.Symbol) + "." + method.Name);
            // The same method written twice (per-platform folders compiled under #if, say) is one box.
            if (_byId.ContainsKey(id))
                continue;
            AddElement(new Element
            {
                Id = id,
                Type = ElementType.ApplicationFunction,
                Layer = Layer.Application,
                Name = type.Symbol.Name + "." + method.Name,
                Parent = classId,
                Source = SourceOf(syntax.Identifier.GetLocation()),
                Confidence = 1.0,
                IsPrivate = method.DeclaredAccessibility == Accessibility.Private ? true : null,
            });
            _methodElements[Keys.Of(method)] = id;
            _bodies.Add((id, (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody!, ModelFor(type.Project, syntax)));
        }

        // Constructors, property bodies and field initializers can call methods too. They have no
        // box of their own, so their calls are drawn from the class box.
        foreach (var part in type.Symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>())
        {
            foreach (var member in part.Members.Where(m => m is ConstructorDeclarationSyntax or PropertyDeclarationSyntax
                         or FieldDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax))
                _bodies.Add((classId, member, ModelFor(type.Project, member)));
        }
    }

    private static bool IsControllerAction(IMethodSymbol method) =>
        method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic
        && !SignalCollector.HasAttribute(method, "NonAction");

    private void AddRoutes()
    {
        foreach (var route in _index.Routes)
        {
            var baseId = $"api:{route.Verb} {route.Path}";
            var id = baseId;
            // The same route can be registered twice (two API versions, say); keep both, numbered.
            for (var n = 2; _byId.ContainsKey(id); n++)
                id = $"{baseId} #{n}";

            var parent = route.DeclaringType != null && _typeElements.TryGetValue(Keys.Of(route.DeclaringType), out var cls)
                ? cls
                : ComponentId(route.Project);
            var isAction = route.HandlerMethod != null && route.Site is MethodDeclarationSyntax;
            var location = route.Site is MethodDeclarationSyntax m ? m.Identifier.GetLocation() : route.Site.GetLocation();
            AddElement(new Element
            {
                Id = id,
                Type = ElementType.ApplicationInterface,
                Layer = Layer.Application,
                Name = isAction ? route.DeclaringType!.Name + "." + route.HandlerMethod!.Name : $"{route.Verb} {route.Path}",
                Route = $"{route.Verb} {route.Path}",
                Parent = parent,
                Source = SourceOf(location),
            });

            if (isAction)
            {
                // A controller action is its own entry point: walk its body as the route.
                var syntax = (MethodDeclarationSyntax)route.Site;
                _methodElements[Keys.Of(route.HandlerMethod!)] = id;
                // An abstract or bodiless action has nothing to walk.
                if (((SyntaxNode?)syntax.Body ?? syntax.ExpressionBody) is { } body)
                    _bodies.Add((id, body, route.Model));
            }
            else if (route.HandlerLambda != null)
            {
                _bodies.Add((id, route.HandlerLambda.Body, route.Model));
            }
            else if (route.HandlerMethod != null
                     && _methodElements.TryGetValue(Keys.Of(route.HandlerMethod), out var handlerId))
            {
                // MapGet("/items", GetItems): the route hands off to a named method.
                AddEdge(RelationshipType.Serving, id, handlerId, route.Site);
            }
        }
    }

    // Looks at every name in a body. A name that resolves to a method is a call (or a method
    // passed as a delegate, which will be called too); a name that resolves to a DbSet is data access.
    private void WalkBody(string owner, SyntaxNode body, SemanticModel model)
    {
        // Route lambdas nested in this body belong to their route, not to this method.
        var nodes = body.DescendantNodesAndSelf(n => n == body || !_index.RouteHandlerNodes.Contains(n)).ToList();
        foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
            OnInvocation(owner, invocation, model);
        foreach (var assignment in nodes.OfType<AssignmentExpressionSyntax>())
            OnAssignment(owner, assignment, model);
        foreach (var name in nodes.OfType<SimpleNameSyntax>())
        {
            // A method group handed to MapGet is the route's arrow, already drawn in AddRoutes.
            if (_index.RouteHandlerNodes.Contains(name) || _index.RouteHandlerNodes.Contains(name.Parent!))
                continue;
            var info = model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            switch (symbol)
            {
                case IMethodSymbol method:
                    OnMethod(owner, method, name);
                    break;
                case IPropertySymbol { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments.Length: 1 } set }:
                    OnDataAccess(owner, set.TypeArguments[0], ExpressionFor(name), name);
                    break;
                case IFieldSymbol { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments.Length: 1 } set }:
                    OnDataAccess(owner, set.TypeArguments[0], ExpressionFor(name), name);
                    break;
            }
        }
    }

    // HttpClient and EF calls are matched on the receiver's type and the method name, not on the
    // resolved method: EF Core and the JSON extensions are packages we don't load, so their
    // methods often don't resolve, but "this variable is an HttpClient / a DbContext" still does.
    private void OnInvocation(string owner, InvocationExpressionSyntax invocation, SemanticModel model)
    {
        // Only receiver.Method(...) calls have a receiver to look at.
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return;
        var name = member.Name.Identifier.Text;
        var receiver = model.GetTypeInfo(member.Expression).Type as INamedTypeSymbol;
        if (receiver == null)
            return;
        var typeArg = member.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic
            ? model.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type
            : null;

        // HttpClient calls leave the repo: draw a Flow arrow to the outside system.
        if (receiver.Name == "HttpClient" && HttpMethods.Contains(name))
        {
            AddEdge(RelationshipType.Flow, owner, ExternalFor(member.Name, model), member.Name);
            // GetFromJsonAsync<Order> also means this function reads an Order.
            if (typeArg != null)
                OnDataAccess(owner, typeArg, null, member.Name, AccessKind.Read);
            return;
        }

        // mediator.Send(command) is dispatched at runtime by the command's type. The pattern rule:
        // the handler is the class implementing IRequestHandler<ThatCommand, ...>.
        if (name is "Send" or "Publish" && receiver.Name is "IMediator" or "ISender" or "IPublisher" or "Mediator"
            && invocation.ArgumentList.Arguments.Count > 0
            && model.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type is INamedTypeSymbol message)
        {
            foreach (var handler in HandlersFor(message))
            {
                // Publish can reach several handlers, and all of them run, so each arrow is certain.
                if (_methodElements.TryGetValue(handler, out var handlerId))
                    AddEdge(RelationshipType.Serving, owner, handlerId, member.Name);
            }
            return;
        }

        var isContext = _index.DbContexts.Contains(Keys.Of(receiver)) || SignalCollector.IsDbContext(receiver);
        // Only DbContext calls are data access here; DbSet properties are handled by name in WalkBody.
        if (!isContext)
            return;
        // context.Set<Order>() is a table, same as a DbSet property.
        if (name == "Set" && typeArg != null)
            OnDataAccess(owner, typeArg, invocation, member.Name);
        // context.Remove(order) writes the entity passed in.
        else if (DbWriteMethods.Contains(name) && invocation.ArgumentList.Arguments.Count > 0
                 && model.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type is { } entity)
            OnDataAccess(owner, entity, null, member.Name, AccessKind.Write);
    }

    // item.Title = request.Title on a stored entity is a write: EF's change tracking saves it
    // on SaveChanges without any Update() call. Only stored entities count; setting a field
    // on a DTO changes nothing in the database.
    private void OnAssignment(string owner, AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        if (assignment.Left is MemberAccessExpressionSyntax member
            && model.GetTypeInfo(member.Expression).Type is { } target
            && _typeElements.TryGetValue(Keys.Of(target), out var id)
            && _byId[id].EntityKind == EntityKind.Stored)
            AddEdge(RelationshipType.Access, owner, id, assignment.Left, AccessKind.Write);
    }

    private void OnMethod(string owner, IMethodSymbol method, SimpleNameSyntax site)
    {
        var def = (method.ReducedFrom ?? method).OriginalDefinition;
        var containing = def.ContainingType;

        // Calling a method on a stored entity (order.AddItem(...)) touches that entity. A method
        // that returns nothing must be changing it; one that returns something is read as a read.
        if (containing != null && _typeElements.TryGetValue(Keys.Of(containing), out var typeId)
            && _byId[typeId].EntityKind == EntityKind.Stored)
        {
            var returnsNothing = def.ReturnsVoid || def.ReturnType is INamedTypeSymbol { Name: "Task", TypeArguments.Length: 0 };
            AddEdge(RelationshipType.Access, owner, typeId, site, returnsNothing ? AccessKind.Write : AccessKind.Read);
            return;
        }

        foreach (var (target, confidence) in ResolveTargets(def))
        {
            // Only calls into the repo's own code become arrows; framework calls are noise.
            if (_methodElements.TryGetValue(Keys.Of(target), out var targetId) && targetId != owner)
                AddEdge(RelationshipType.Serving, owner, targetId, site, confidence: confidence);
        }
    }

    // Handle(...) methods of every class that implements IRequestHandler<T, ...> or INotificationHandler<T>
    // for this message type. Matched by name because the MediatR package itself isn't loaded.
    private List<string> HandlersFor(INamedTypeSymbol message)
    {
        var messageKey = Keys.Of(message);
        if (_handlerCache.TryGetValue(messageKey, out var cached))
            return cached;
        var handlers = new List<string>();
        foreach (var type in _index.Types.Values)
        {
            // When MediatR isn't loaded, Roslyn can't tell IRequestHandler is an interface and files it
            // as the base class instead, so both lists are searched.
            var supertypes = type.Symbol.AllInterfaces.Concat(BaseTypes(type.Symbol));
            var handles = supertypes.Any(i =>
                i.Name is "IRequestHandler" or "INotificationHandler" && i.TypeArguments.Length > 0
                && Keys.Of(i.TypeArguments[0]) == messageKey);
            var handle = type.Symbol.GetMembers("Handle").OfType<IMethodSymbol>().FirstOrDefault();
            // A handler class without its own Handle method inherits it; the base class gets the arrow instead.
            if (handles && handle != null)
                handlers.Add(Keys.Of(handle));
        }
        _handlerCache[messageKey] = handlers;
        return handlers;
    }

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
            yield return t;
    }

    // A call through an interface (or abstract method) could land in any implementation.
    // If DI registrations name one, use it; otherwise draw every candidate at confidence 0.5.
    private List<(IMethodSymbol Method, double Confidence)> ResolveTargets(IMethodSymbol def)
    {
        var containing = def.ContainingType;
        var isInterface = containing?.TypeKind == TypeKind.Interface;
        // A normal method call on a class goes exactly where it says.
        if (containing == null || (!isInterface && !def.IsAbstract))
            return new List<(IMethodSymbol, double)> { (def, 1.0) };

        // A framework interface (IDisposable.Dispose) is implemented all over the repo; following
        // it would link unrelated projects. Only the repo's own interfaces are followed.
        if (containing.DeclaringSyntaxReferences.Length == 0)
            return new List<(IMethodSymbol, double)>();

        var cacheKey = Keys.Of(def);
        if (_targetCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var typeKey = Keys.Of(containing);
        var candidates = new List<IMethodSymbol>();
        foreach (var type in _index.Types.Values)
        {
            var impl = isInterface ? FindInterfaceImplementation(type.Symbol, typeKey, cacheKey) : FindOverride(type.Symbol, cacheKey);
            if (impl != null)
                candidates.Add(impl);
        }

        // AddScoped<IOrderService, OrderService>() narrows the candidates to what runs in production.
        if (_index.DiImplementations.TryGetValue(typeKey, out var registered))
        {
            var narrowed = candidates.Where(c => registered.Contains(Keys.Of(c.ContainingType))).ToList();
            // If the registration points outside the repo, keep all candidates rather than none.
            if (narrowed.Count > 0)
                candidates = narrowed;
        }

        var confidence = candidates.Count == 1 ? 1.0 : 0.5;
        var result = candidates.Select(c => (c, confidence)).ToList();
        _targetCache[cacheKey] = result;
        return result;
    }

    private static IMethodSymbol? FindInterfaceImplementation(INamedTypeSymbol type, string interfaceKey, string methodKey)
    {
        foreach (var iface in type.AllInterfaces.Where(i => Keys.Of(i) == interfaceKey))
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>().Where(m => Keys.Of(m) == methodKey))
            {
                // Skip default interface methods: the class didn't write its own.
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol impl
                    && !SymbolEqualityComparer.Default.Equals(impl.ContainingType, iface))
                    return impl;
            }
        }
        return null;
    }

    private static IMethodSymbol? FindOverride(INamedTypeSymbol type, string methodKey)
    {
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
        {
            // Walk up the override chain: C overrides B overrides the abstract A.
            for (var o = method.OverriddenMethod; o != null; o = o.OverriddenMethod)
            {
                if (Keys.Of(o) == methodKey)
                    return method;
            }
        }
        return null;
    }

    private void OnDataAccess(string owner, ITypeSymbol entity, ExpressionSyntax? tableExpr, SyntaxNode site, AccessKind? known = null)
    {
        // GetFromJsonAsync<Order[]> reads Orders: look through the array or list to the item.
        entity = CollectionItem(entity) ?? entity;
        // Only data the repo defines (and we classified as a Data Object) gets an arrow.
        if (!_typeElements.TryGetValue(Keys.Of(entity), out var id) || _byId[id].Type != ElementType.DataObject)
            return;
        var kind = known ?? (IsWrite(tableExpr) ? AccessKind.Write : AccessKind.Read);
        AddEdge(RelationshipType.Access, owner, id, site, kind);
    }

    // _db.Orders.Add(order) is a write; _db.Orders.Where(...) is a read.
    private static bool IsWrite(ExpressionSyntax? tableExpr) =>
        tableExpr?.Parent is MemberAccessExpressionSyntax member
        && member.Expression == tableExpr
        && DbWriteMethods.Contains(member.Name.Identifier.Text)
        && member.Parent is InvocationExpressionSyntax;

    // For "_db.Orders" the name node is "Orders"; the table expression is the whole "_db.Orders".
    private static ExpressionSyntax ExpressionFor(SimpleNameSyntax name) =>
        name.Parent is MemberAccessExpressionSyntax member && member.Name == name ? member : name;

    // Names the outside system: an absolute URL in the call wins, then the typed client's
    // registered BaseAddress, then the class that owns the HttpClient.
    private string ExternalFor(SimpleNameSyntax site, SemanticModel model)
    {
        string? host = null;
        var invocation = site.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        var firstArg = invocation?.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (firstArg != null && model.GetConstantValue(firstArg) is { HasValue: true, Value: string url }
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            host = uri.Host;

        var ownerType = model.GetEnclosingSymbol(site.SpanStart)?.ContainingType;
        if (host == null && ownerType != null && _index.HttpClientHosts.TryGetValue(Keys.Of(ownerType), out var registered))
            host = registered;
        var name = host ?? (ownerType?.Name ?? "unknown") + " HTTP target";

        var id = "ext:" + name;
        // One box per outside system, however many calls go to it.
        if (!_byId.ContainsKey(id))
        {
            AddElement(new Element
            {
                Id = id,
                Type = ElementType.ExternalSystem,
                Layer = Layer.Application,
                Name = name,
                Source = SourceOf(site.GetLocation()),
                // Named after the calling class, not a real address: worth a human look.
                Confidence = host == null ? TypeClassifier.Unsure : 1.0,
            });
        }
        return id;
    }

    // Order has List<OrderLine> Lines: Order aggregates OrderLine.
    private void AddAggregations()
    {
        foreach (var (key, id) in _typeElements.ToList())
        {
            // Only data points to data with this arrow.
            if (_byId[id].Type != ElementType.DataObject)
                continue;
            foreach (var property in _index.Types[key].Symbol.GetMembers().OfType<IPropertySymbol>())
            {
                var item = CollectionItem(property.Type);
                if (item != null && _typeElements.TryGetValue(Keys.Of(item), out var childId)
                    && _byId[childId].Type == ElementType.DataObject && childId != id
                    && property.Locations.FirstOrDefault(l => l.IsInSource) is { } location)
                    AddEdge(RelationshipType.Aggregation, id, childId, location);
            }
        }
    }

    private static ITypeSymbol? CollectionItem(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { TypeArguments.Length: 1 } named when CollectionNames.Contains(named.Name) => named.TypeArguments[0],
        _ => null,
    };

    private static List<Field> FieldsOf(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && !p.IsImplicitlyDeclared)
            .Select(p => new Field(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
            .ToList();

    private void AddElement(Element element)
    {
        _elements.Add(element);
        _byId[element.Id] = element;
    }

    private void AddEdge(RelationshipType type, string from, string to, SyntaxNode site, AccessKind? access = null, double confidence = 1.0) =>
        AddEdge(type, from, to, site.GetLocation(), access, confidence);

    private void AddEdge(RelationshipType type, string from, string to, Location site, AccessKind? access = null, double confidence = 1.0)
    {
        // The same call written twice in one method is still one arrow; the first line is the evidence.
        if (!_edgeKeys.Add((from, to, type, access)))
            return;
        _relationships.Add(new Relationship
        {
            Id = "rel:" + (_relationships.Count + 1),
            Type = type,
            From = from,
            To = to,
            Evidence = SourceOf(site),
            Access = access,
            Confidence = confidence,
        });
    }

    private static SourceRef SourceOf(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceRef(span.Path.Replace('\\', '/'), span.StartLinePosition.Line + 1);
    }

    private static SemanticModel ModelFor(LoadedProject project, SyntaxNode node) =>
        project.Compilation.GetSemanticModel(node.SyntaxTree);

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith('<');
}
