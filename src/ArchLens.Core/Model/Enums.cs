namespace ArchLens.Core.Model;

// The 10 fixed box types, borrowed from ArchiMate. The LLM may only pick from this list.
public enum ElementType
{
    BusinessActor,
    ApplicationComponent,
    ApplicationInterface,
    ApplicationService,
    ApplicationFunction,
    ApplicationProcess,
    DataObject,
    ExternalSystem,
    Node,
    TechnologyService,
}

// The 8 fixed arrow types. v1 emits Serving, Access, Flow and Aggregation;
// Composition and Assignment are shown as nesting (the "parent" field) instead of arrows.
public enum RelationshipType
{
    Composition,
    Aggregation,
    Assignment,
    Realization,
    Serving,
    Access,
    Flow,
    Triggering,
}

public enum Layer
{
    Business,
    Application,
    Technology,
}

// Who produced a fact, so the UI can show what was guessed.
public enum Origin
{
    Rule,
    Static,
    Llm,
    User,
}

public enum EntityKind
{
    Stored,
    Dto,
    External,
}

public enum AccessKind
{
    Read,
    Write,
}
