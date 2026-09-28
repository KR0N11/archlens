using ArchLens.Core.Analysis;
using ArchLens.Core.Model;

namespace ArchLens.Core.Enrichment;

// What we send the LLM about one unsure class.
public record LabelRequest(string ElementId, string Name, string CurrentGuess, string Source, List<string> Neighbors, List<string> Signals);

// What we accept back. Type is text on purpose: it is checked against the fixed list before use.
public record LabelResponse(string Type, string Purpose, double Confidence);

// The seam between ArchLens and any LLM. Tests use a fake; the CLI plugs in Claude.
public interface ILabelClient
{
    Task<LabelResponse?> LabelAsync(LabelRequest request, CancellationToken ct);
}

// Step 5 of the pipeline: asks the LLM about the classes the rules marked as unsure,
// and only those. The LLM can change a box's type, purpose and confidence. It is never shown
// the arrow list as something to edit, and nothing here writes to graph.Relationships.
public static class Enricher
{
    public const int MaxSourceChars = 6000;
    public const int MaxRequests = 200;

    public static async Task<int> EnrichAsync(ArchGraph graph, RepoIndex index, ILabelClient client, CancellationToken ct)
    {
        var byId = graph.Elements.ToDictionary(e => e.Id);
        var unsure = graph.Elements
            .Where(e => e.Confidence <= Analysis.TypeClassifier.Unsure && e.Origin == Origin.Rule)
            .Where(e => e.Id.StartsWith("cls:") || e.Id.StartsWith("do:"))
            .Take(MaxRequests)
            .ToList();

        var applied = 0;
        foreach (var element in unsure)
        {
            var typeKey = element.Id[(element.Id.IndexOf(':') + 1)..];
            // Every cls:/do: id comes from a type in the index, but check rather than crash.
            if (!index.Types.TryGetValue(typeKey, out var type))
                continue;

            var request = new LabelRequest(
                element.Id,
                element.Name,
                element.Type.ToString(),
                SourceOf(type),
                Neighbors(graph, byId, element.Id),
                element.EntitySignals ?? new List<string>());
            var response = await client.LabelAsync(request, ct);
            if (Apply(element, response))
                applied++;
        }
        return applied;
    }

    // Copies an answer onto the box, or refuses it. Returns false (and changes nothing)
    // when the LLM gave no answer or named a type outside the fixed list.
    public static bool Apply(Element element, LabelResponse? response)
    {
        if (response == null || !Enum.TryParse<ElementType>(response.Type, ignoreCase: false, out var type)
            || !Enum.IsDefined(type))
            return false;
        element.Type = type;
        element.Layer = LayerOf(type);
        element.Purpose = response.Purpose;
        element.Confidence = Math.Clamp(response.Confidence, 0, 1);
        element.Origin = Origin.Llm;
        return true;
    }

    public static Layer LayerOf(ElementType type) => type switch
    {
        ElementType.BusinessActor => Layer.Business,
        ElementType.Node or ElementType.TechnologyService => Layer.Technology,
        _ => Layer.Application,
    };

    // The class's own code, cut to a fixed size so one huge class can't blow up the cost.
    private static string SourceOf(Analysis.TypeInfo type)
    {
        var text = type.Declaration.ToFullString();
        return text.Length <= MaxSourceChars ? text : text[..MaxSourceChars] + "\n// ... (truncated)";
    }

    // Names of the boxes this class (or its functions) is connected to, both directions.
    private static List<string> Neighbors(ArchGraph graph, Dictionary<string, Element> byId, string classId)
    {
        var mine = graph.Elements.Where(e => e.Id == classId || e.Parent == classId).Select(e => e.Id).ToHashSet();
        return graph.Relationships
            .Where(r => mine.Contains(r.From) || mine.Contains(r.To))
            .Select(r => mine.Contains(r.From) ? $"{r.Type} -> {byId[r.To].Name}" : $"{byId[r.From].Name} -> {r.Type}")
            .Distinct()
            .Take(20)
            .ToList();
    }
}
