using ArchLens.Core.Model;

namespace ArchLens.Core.Tracing;

// Answers "what happens when X is called?": starting from one entry point, follow calls
// depth first until we reach data, an outside system, or the depth limit.
public static class CallFlowTracer
{
    public const int DefaultMaxDepth = 8;

    private static readonly HashSet<RelationshipType> Followed = new()
    {
        RelationshipType.Serving, RelationshipType.Flow, RelationshipType.Access,
    };

    public static ViewDef Trace(ArchGraph graph, string entryId, int maxDepth = DefaultMaxDepth)
    {
        var byId = graph.Elements.ToDictionary(e => e.Id);
        var outgoing = graph.Relationships
            .Where(r => Followed.Contains(r.Type))
            .ToLookup(r => r.From);
        var view = new ViewDef();
        var visited = new HashSet<string>();

        void Visit(string id, int depth)
        {
            visited.Add(id);
            view.Elements.Add(id);
            // Data and outside systems are the end of a path: nothing in the repo runs past them.
            if (byId[id].Type is ElementType.DataObject or ElementType.ExternalSystem)
                return;
            // Big repos have very deep chains; the cap keeps the diagram readable.
            if (depth >= maxDepth)
                return;
            foreach (var rel in outgoing[id])
            {
                view.Relationships.Add(rel.Id);
                // Recursion or a cycle: draw the arrow back, but don't walk it again.
                if (!visited.Contains(rel.To))
                    Visit(rel.To, depth + 1);
            }
        }

        // An unknown entry point is a caller mistake; fail loudly instead of returning an empty view.
        if (!byId.ContainsKey(entryId))
            throw new KeyNotFoundException($"No element with id '{entryId}'.");
        Visit(entryId, 0);
        return view;
    }

    // One call flow view per HTTP entry point, keyed "callFlow:<entry id>".
    public static Dictionary<string, ViewDef> TraceAllEntryPoints(ArchGraph graph, int maxDepth = DefaultMaxDepth) =>
        graph.Elements
            .Where(e => e.Type == ElementType.ApplicationInterface && e.Route != null)
            .ToDictionary(e => "callFlow:" + e.Id, e => Trace(graph, e.Id, maxDepth));
}
