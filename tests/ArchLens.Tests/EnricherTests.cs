using ArchLens.Core.Analysis;
using ArchLens.Core.Enrichment;
using ArchLens.Core.Loading;
using ArchLens.Core.Model;

namespace ArchLens.Tests;

// The LLM step, with a fake LLM so the tests need no network and no key.
public class EnricherTests
{
    private class FakeLabeler : ILabelClient
    {
        private readonly Func<LabelRequest, LabelResponse?> _answer;
        public List<string> Asked { get; } = new();

        public FakeLabeler(Func<LabelRequest, LabelResponse?> answer)
        {
            _answer = answer;
        }

        public Task<LabelResponse?> LabelAsync(LabelRequest request, CancellationToken ct)
        {
            Asked.Add(request.ElementId);
            return Task.FromResult(_answer(request));
        }
    }

    private static (ArchGraph Graph, RepoIndex Index) Build()
    {
        var root = TestRepo.SampleShopRoot;
        var projects = RepoLoader.Load(root);
        var index = SignalCollector.Collect(projects, root);
        var (elements, relationships) = GraphBuilder.Build(projects, index);
        return (new ArchGraph { Elements = elements, Relationships = relationships }, index);
    }

    // Only the boxes the rules marked unsure (confidence 0.5) are sent to the LLM.
    [Fact]
    public async Task Asks_only_about_unsure_classes()
    {
        var (graph, index) = Build();
        var fake = new FakeLabeler(_ => null);
        await Enricher.EnrichAsync(graph, index, fake, CancellationToken.None);
        var expected = graph.Elements.Where(e => e.Confidence == 0.5 && (e.Id.StartsWith("cls:") || e.Id.StartsWith("do:"))).Select(e => e.Id);
        Assert.Equal(expected.OrderBy(x => x), fake.Asked.OrderBy(x => x));
        Assert.Contains("do:SampleShop.Data.ShippingOptions", fake.Asked);
        Assert.DoesNotContain("do:SampleShop.Data.Entities.Order", fake.Asked);
    }

    // A valid answer changes the box and marks it as the LLM's work; the arrows stay identical.
    [Fact]
    public async Task Applies_a_valid_label_and_never_touches_arrows()
    {
        var (graph, index) = Build();
        var arrowsBefore = GraphJson.Write(new ArchGraph { Relationships = graph.Relationships });
        var fake = new FakeLabeler(r => new LabelResponse("ApplicationService", "Holds shipping settings", 0.8));
        var applied = await Enricher.EnrichAsync(graph, index, fake, CancellationToken.None);

        var e = graph.Get("do:SampleShop.Data.ShippingOptions");
        Assert.Equal(ElementType.ApplicationService, e.Type);
        Assert.Equal(Origin.Llm, e.Origin);
        Assert.Equal("Holds shipping settings", e.Purpose);
        Assert.Equal(fake.Asked.Count, applied);
        Assert.Equal(arrowsBefore, GraphJson.Write(new ArchGraph { Relationships = graph.Relationships }));
    }

    // A made-up type is refused and the box is left exactly as it was.
    [Fact]
    public void Rejects_a_type_outside_the_fixed_list()
    {
        var element = new Element { Id = "cls:X", Type = ElementType.DataObject, Layer = Layer.Application, Name = "X", Confidence = 0.5 };
        var before = GraphJson.Write(new ArchGraph { Elements = { element } });

        Assert.False(Enricher.Apply(element, new LabelResponse("Microservice", "made up", 0.99)));
        Assert.False(Enricher.Apply(element, null));
        Assert.Equal(before, GraphJson.Write(new ArchGraph { Elements = { element } }));
    }

    // Layers follow from the type, so the LLM can't put a database in the Business layer.
    [Theory]
    [InlineData(ElementType.BusinessActor, Layer.Business)]
    [InlineData(ElementType.TechnologyService, Layer.Technology)]
    [InlineData(ElementType.DataObject, Layer.Application)]
    public void Layer_follows_type(ElementType type, Layer layer)
    {
        Assert.Equal(layer, Enricher.LayerOf(type));
    }
}
