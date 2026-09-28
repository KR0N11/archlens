using ArchLens.Core.Model;
using Microsoft.CodeAnalysis;

namespace ArchLens.Core.Analysis;

public record Classification(ElementType Type, Layer Layer, double Confidence, EntityResult? Entity);

// Pass 2: picks one of the fixed box types for each class, using rules only.
// Anything the rules can't place gets confidence 0.5, which is what the LLM step looks for.
public static class TypeClassifier
{
    public const double Unsure = 0.5;

    // Returns null for classes that carry no architecture (no code and no data signals).
    public static Classification? Classify(TypeInfo type, RepoIndex index)
    {
        var symbol = type.Symbol;
        var key = Keys.Of(symbol);

        // EF migrations and model snapshots are generated code: huge, and not the design.
        if (SignalCollector.InheritsFrom(symbol, "Migration") || SignalCollector.InheritsFrom(symbol, "ModelSnapshot"))
            return null;
        if (index.DbContexts.Contains(key))
            return new Classification(ElementType.TechnologyService, Layer.Technology, 1.0, null);
        if (index.Controllers.Contains(key))
            return new Classification(ElementType.ApplicationInterface, Layer.Application, 1.0, null);

        // A static class can't be instantiated, so it can't be a piece of data.
        // An exception carries data, but it is an error signal, not part of the data model.
        if (!symbol.IsStatic && !SignalCollector.InheritsFrom(symbol, "Exception"))
        {
            var entity = EntityScorer.Score(symbol, index);
            if (entity.Score >= EntityScorer.EntityThreshold)
                return new Classification(ElementType.DataObject, Layer.Application, 1.0, entity);
            if (entity.Score >= EntityScorer.AmbiguousThreshold)
                return new Classification(ElementType.DataObject, Layer.Application, Unsure, entity);
        }

        if (!HasMethodBodies(symbol))
            return null;
        // "Injected via DI, has an interface" is the recognized shape of a service.
        var recognized = index.DiRegistered.Contains(key)
            || symbol.AllInterfaces.Any(i => i.DeclaringSyntaxReferences.Length > 0);
        return new Classification(ElementType.ApplicationService, Layer.Application, recognized ? 0.9 : Unsure, null);
    }

    public static bool HasMethodBodies(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared && !m.IsAbstract);
}
