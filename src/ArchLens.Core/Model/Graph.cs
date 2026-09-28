namespace ArchLens.Core.Model;

public record SourceRef(string File, int Line);

public record Field(string Name, string Type);

// One box on the diagram.
public class Element
{
    public required string Id { get; init; }
    public required ElementType Type { get; set; }
    public required Layer Layer { get; set; }
    public required string Name { get; init; }
    public string? Parent { get; init; }
    public SourceRef? Source { get; init; }
    public string? Route { get; init; }
    public string? Purpose { get; set; }
    public double Confidence { get; set; } = 1.0;
    public Origin Origin { get; set; } = Origin.Rule;
    public EntityKind? EntityKind { get; set; }
    public int? EntityScore { get; set; }
    // Why the entity score is what it is, e.g. "ORM mapping +5". Shown in the side panel.
    public List<string>? EntitySignals { get; set; }
    public List<Field>? Fields { get; set; }
    // Set on private functions only: nothing outside the class can call them, so no callers means dead code.
    public bool? IsPrivate { get; init; }
}

// One arrow on the diagram. Evidence is required: no evidence, no arrow.
public class Relationship
{
    public required string Id { get; init; }
    public required RelationshipType Type { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required SourceRef Evidence { get; init; }
    public AccessKind? Access { get; init; }
    public double Confidence { get; init; } = 1.0;
    public Origin Origin { get; init; } = Origin.Static;
}

public class ViewDef
{
    public List<string> Elements { get; init; } = new();
    public List<string> Relationships { get; init; } = new();
}

public class Finding
{
    public required string Check { get; init; }
    public required string Message { get; init; }
    public required int Penalty { get; init; }
    public List<string> Elements { get; init; } = new();
    public List<string> Relationships { get; init; } = new();
}

public class ScoreResult
{
    public int Total { get; init; }
    public List<Finding> Checks { get; init; } = new();
}

// One document per repo per commit. Every view, the score and the export read this.
public class ArchGraph
{
    public string Repo { get; init; } = "";
    public string? Commit { get; init; }
    public DateTime AnalyzedAt { get; init; }
    public List<Element> Elements { get; init; } = new();
    public List<Relationship> Relationships { get; init; } = new();
    public Dictionary<string, ViewDef> Views { get; init; } = new();
    public ScoreResult Score { get; set; } = new();
}
