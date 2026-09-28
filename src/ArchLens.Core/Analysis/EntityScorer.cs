using ArchLens.Core.Model;
using Microsoft.CodeAnalysis;

namespace ArchLens.Core.Analysis;

public record EntityResult(int Score, List<string> Signals, EntityKind? Kind);

// Decides "is this class a piece of data?" from several signals, strongest first.
// Names alone are wrong too often (a UserModel can be a view model), so a name is worth 1 point
// while an ORM mapping is worth 5.
public static class EntityScorer
{
    // 5 or more: a Data Object. 2 to 4: ambiguous, the LLM decides. Under 2: not an entity.
    public const int EntityThreshold = 5;
    public const int AmbiguousThreshold = 2;

    private static readonly string[] DataSuffixes = { "Entity", "Model", "Record", "Dto", "DTO" };
    private static readonly string[] LogicSuffixes = { "Service", "Controller", "Handler", "Helper", "Factory" };

    public static EntityResult Score(INamedTypeSymbol symbol, RepoIndex index)
    {
        var key = Keys.Of(symbol);
        var signals = new List<string>();
        var score = 0;

        void Add(int points, string reason)
        {
            score += points;
            signals.Add($"{reason} {(points > 0 ? "+" : "")}{points}");
        }

        var orm = index.OrmTypes.Contains(key) || SignalCollector.HasAttribute(symbol, "Table");
        if (orm)
            Add(5, "ORM mapping");
        var schema = MatchesTable(symbol.Name, index.SchemaTables);
        if (schema)
            Add(5, "Schema file match");
        if (index.RepositoryTypes.Contains(key))
            Add(3, "Used in a repository");
        // A DI-registered type in a handler's parameters is an injected service, not request data.
        var wire = (index.WireTypes.Contains(key) || index.SerializerAttributed.Contains(key))
            && !index.DiRegistered.Contains(key);
        if (wire)
            Add(2, "Serialized over the wire");
        if (MostlyProperties(symbol))
            Add(2, "Mostly properties");
        if (DataSuffixes.Any(s => symbol.Name.EndsWith(s, StringComparison.Ordinal)))
            Add(1, "Data name suffix");
        if (LogicSuffixes.Any(s => symbol.Name.EndsWith(s, StringComparison.Ordinal)))
            Add(-5, "Logic name suffix");

        EntityKind? kind = null;
        if (orm || schema)
            kind = EntityKind.Stored;
        else if (index.ExternalTypes.Contains(key))
            kind = EntityKind.External;
        else if (wire)
            kind = EntityKind.Dto;
        return new EntityResult(score, signals, kind);
    }

    // True when 80% or more of the members are data (properties or non-private fields).
    // Private fields don't count as data: in a service they are usually injected dependencies.
    public static bool MostlyProperties(INamedTypeSymbol symbol)
    {
        var data = 0;
        var methods = 0;
        foreach (var member in symbol.GetMembers())
        {
            // Compiler-made members (record Equals/ToString, backing fields) say nothing about intent.
            if (member.IsImplicitlyDeclared || member.IsStatic)
                continue;
            switch (member)
            {
                case IPropertySymbol:
                case IFieldSymbol { DeclaredAccessibility: not Accessibility.Private, IsConst: false }:
                    data++;
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary }:
                    methods++;
                    break;
            }
        }
        return data > 0 && data >= 0.8 * (data + methods);
    }

    // Tables are usually the plural of the class: Order -> Orders, Box -> Boxes, Category -> Categories.
    public static bool MatchesTable(string className, HashSet<string> tables)
    {
        var name = className.ToLowerInvariant();
        if (tables.Contains(name) || tables.Contains(name + "s") || tables.Contains(name + "es"))
            return true;
        return name.EndsWith('y') && tables.Contains(name[..^1] + "ies");
    }
}
