using ArchLens.Core.Model;

namespace ArchLens.Core.Scoring;

// 100 minus penalties from fixed graph checks. Every finding lists the boxes and arrows
// that caused it, so the UI can point at them. No LLM is involved in the number.
// Penalty values are starting points to tune, not truths.
public static class ArchitectureScorer
{
    public static ScoreResult Score(ArchGraph graph)
    {
        var byId = graph.Elements.ToDictionary(e => e.Id);
        var findings = new List<Finding>();
        findings.AddRange(Capped(CircularDependencies(graph, byId), 30));
        findings.AddRange(Capped(UiTouchesData(graph, byId), 30));
        findings.AddRange(Capped(GodClasses(graph, byId), 15));
        findings.AddRange(Capped(Orphans(graph), 10));
        findings.AddRange(Capped(EntityWrittenFromManyPlaces(graph, byId), 15));
        var total = Math.Max(0, 100 - findings.Sum(f => f.Penalty));
        return new ScoreResult { Total = total, Checks = findings };
    }

    // Keeps one check from sinking the whole score: once its penalties reach the cap,
    // later findings of that check are still listed but cost nothing.
    private static IEnumerable<Finding> Capped(IEnumerable<Finding> findings, int cap)
    {
        var spent = 0;
        foreach (var f in findings)
        {
            var penalty = Math.Min(f.Penalty, cap - spent);
            spent += penalty;
            yield return new Finding { Check = f.Check, Message = f.Message, Penalty = penalty, Elements = f.Elements, Relationships = f.Relationships };
        }
    }

    // The class a box belongs to: a function or route belongs to its class; a class is itself.
    public static string ClassOf(Element element, Dictionary<string, Element> byId)
    {
        var isMember = element.Type == ElementType.ApplicationFunction
            || (element.Type == ElementType.ApplicationInterface && element.Route != null);
        // Minimal API routes in Program.cs have a component as parent, not a class.
        if (isMember && element.Parent != null && byId.TryGetValue(element.Parent, out var parent)
            && parent.Type != ElementType.ApplicationComponent)
            return parent.Id;
        return element.Id;
    }

    public static string? ComponentOf(Element element, Dictionary<string, Element> byId)
    {
        for (var e = element; e != null; e = e.Parent != null && byId.TryGetValue(e.Parent, out var p) ? p : null)
        {
            if (e.Type == ElementType.ApplicationComponent)
                return e.Id;
        }
        return null;
    }

    // Classes that call each other in a loop. Checked per class, not per project:
    // MSBuild refuses project reference cycles, so a project-level check would always pass.
    private static IEnumerable<Finding> CircularDependencies(ArchGraph graph, Dictionary<string, Element> byId)
    {
        var calls = graph.Relationships.Where(r => r.Type == RelationshipType.Serving).ToList();
        var edges = new Dictionary<string, HashSet<string>>();
        foreach (var r in calls)
        {
            var from = ClassOf(byId[r.From], byId);
            var to = ClassOf(byId[r.To], byId);
            // A class calling its own methods is normal, not a cycle.
            if (from == to)
                continue;
            if (!edges.TryGetValue(from, out var targets))
                edges[from] = targets = new HashSet<string>();
            targets.Add(to);
        }

        foreach (var scc in Tarjan.StronglyConnected(edges).Where(c => c.Count > 1))
        {
            var members = scc.ToHashSet();
            var inside = calls
                .Where(r => members.Contains(ClassOf(byId[r.From], byId)) && members.Contains(ClassOf(byId[r.To], byId)))
                .Where(r => ClassOf(byId[r.From], byId) != ClassOf(byId[r.To], byId))
                .Select(r => r.Id)
                .ToList();
            var names = string.Join(" -> ", scc.Select(id => byId[id].Name));
            yield return new Finding
            {
                Check = "Circular dependency",
                Message = $"{scc.Count} classes call each other in a loop: {names}",
                Penalty = 10,
                Elements = scc,
                Relationships = inside,
            };
        }
    }

    // An entry point reading or writing a stored entity itself, with no service in between.
    private static IEnumerable<Finding> UiTouchesData(ArchGraph graph, Dictionary<string, Element> byId)
    {
        foreach (var r in graph.Relationships.Where(r => r.Type == RelationshipType.Access))
        {
            var from = byId[r.From];
            var to = byId[r.To];
            if (from.Type == ElementType.ApplicationInterface && to.EntityKind == EntityKind.Stored)
            {
                yield return new Finding
                {
                    Check = "UI touches data directly",
                    Message = $"{from.Name} {(r.Access == AccessKind.Write ? "writes" : "reads")} {to.Name} with no service in between",
                    Penalty = 5,
                    Elements = new List<string> { from.Id, to.Id },
                    Relationships = new List<string> { r.Id },
                };
            }
        }
    }

    // "Far more calls than the median": more than 3x the median class and at least 10 targets.
    private static IEnumerable<Finding> GodClasses(ArchGraph graph, Dictionary<string, Element> byId)
    {
        var targetsByClass = graph.Relationships
            .Where(r => r.Type == RelationshipType.Serving)
            .GroupBy(r => ClassOf(byId[r.From], byId))
            .ToDictionary(g => g.Key, g => g.Select(r => r.To).Distinct().Count());
        // No calls at all means nothing to compare against.
        if (targetsByClass.Count == 0)
            yield break;
        var median = Median(targetsByClass.Values.ToList());
        foreach (var (classId, count) in targetsByClass.OrderByDescending(kv => kv.Value))
        {
            if (count >= 10 && count > 3 * median)
            {
                yield return new Finding
                {
                    Check = "God class",
                    Message = $"{byId[classId].Name} calls {count} different functions (repo median {median:0.#})",
                    Penalty = 3,
                    Elements = new List<string> { classId },
                };
            }
        }
    }

    // A private function nobody calls is dead code: nothing outside the class can reach it.
    private static IEnumerable<Finding> Orphans(ArchGraph graph)
    {
        var called = graph.Relationships.Select(r => r.To).ToHashSet();
        foreach (var e in graph.Elements.Where(e => e.Type == ElementType.ApplicationFunction && e.IsPrivate == true))
        {
            if (!called.Contains(e.Id))
            {
                yield return new Finding
                {
                    Check = "Orphan element",
                    Message = $"{e.Name} is private and never called",
                    Penalty = 1,
                    Elements = new List<string> { e.Id },
                };
            }
        }
    }

    // The same entity changed from 3 or more projects: nobody clearly owns it.
    private static IEnumerable<Finding> EntityWrittenFromManyPlaces(ArchGraph graph, Dictionary<string, Element> byId)
    {
        var writes = graph.Relationships.Where(r => r.Type == RelationshipType.Access && r.Access == AccessKind.Write);
        foreach (var group in writes.GroupBy(r => r.To))
        {
            var components = group.Select(r => ComponentOf(byId[r.From], byId)).Where(c => c != null).Distinct().ToList();
            if (components.Count >= 3)
            {
                yield return new Finding
                {
                    Check = "Entity written from many places",
                    Message = $"{byId[group.Key].Name} is written from {components.Count} projects",
                    Penalty = 3,
                    Elements = new List<string> { group.Key },
                    Relationships = group.Select(r => r.Id).ToList(),
                };
            }
        }
    }

    private static double Median(List<int> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
