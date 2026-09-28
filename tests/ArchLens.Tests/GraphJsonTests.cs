using ArchLens.Core.Model;

namespace ArchLens.Tests;

public class GraphJsonTests
{
    // Enums are written as words and empty fields are left out, which is what the viewer expects.
    [Fact]
    public void Writes_enums_as_text_and_skips_nulls()
    {
        var graph = new ArchGraph
        {
            Elements = { new Element { Id = "fn:A", Type = ElementType.ApplicationFunction, Layer = Layer.Application, Name = "A" } },
        };
        var json = GraphJson.Write(graph);
        Assert.Contains("\"type\": \"ApplicationFunction\"", json);
        Assert.DoesNotContain("\"purpose\"", json);
    }

    // Reading back what we wrote gives the same document.
    [Fact]
    public void Round_trips()
    {
        var graph = TestRepo.AnalyzeSampleShop();
        var json = GraphJson.Write(graph);
        Assert.Equal(json, GraphJson.Write(GraphJson.Read(json)));
    }
}
