using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchLens.Core.Model;

// Reads and writes the graph JSON: camelCase keys, enums as text, nulls left out.
public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(ArchGraph graph) => JsonSerializer.Serialize(graph, Options);

    public static ArchGraph Read(string json) =>
        JsonSerializer.Deserialize<ArchGraph>(json, Options)
        ?? throw new InvalidDataException("Graph JSON was empty.");
}
