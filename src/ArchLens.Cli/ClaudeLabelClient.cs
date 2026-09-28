using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using ArchLens.Core.Enrichment;
using ArchLens.Core.Model;

namespace ArchLens.Cli;

// Asks Claude to label one unsure class. The reply is forced into a JSON schema whose
// "type" field is an enum of exactly the 10 element types, so it can't invent a new box kind.
public class ClaudeLabelClient : ILabelClient
{
    private readonly AnthropicClient _client = new();
    private readonly string _model;

    public ClaudeLabelClient(string model)
    {
        _model = model;
    }

    private const string Instructions =
        "You label one class from a C# codebase for an ArchiMate-style architecture diagram. " +
        "Pick the single element type that best describes the class, write a one-line purpose " +
        "(under 15 words, plain English, no class name), and give your confidence from 0 to 1. " +
        "DataObject means a class that mainly holds data that is stored or sent. " +
        "ApplicationService means a class whose main job is logic other code calls. " +
        "Judge from the code and its neighbors, not from the name alone.";

    public async Task<LabelResponse?> LabelAsync(LabelRequest request, CancellationToken ct)
    {
        var prompt =
            $"Class: {request.Name}\n" +
            $"Rule-based guess: {request.CurrentGuess}\n" +
            $"Entity signals: {(request.Signals.Count == 0 ? "none" : string.Join(", ", request.Signals))}\n" +
            $"Connected to: {(request.Neighbors.Count == 0 ? "nothing" : string.Join("; ", request.Neighbors))}\n\n" +
            $"Source:\n{request.Source}";

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 1024,
                System = Instructions,
                OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = Schema() } },
                Messages = [new() { Role = Role.User, Content = prompt }],
            }, ct);

            // A refusal or a cut-off answer has no usable JSON: keep the rule's label.
            if (response.StopReason != "end_turn")
                return null;
            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            return JsonSerializer.Deserialize<LabelResponse>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (AnthropicRateLimitException)
        {
            // The SDK already retried; one class staying unlabeled is better than failing the whole run.
            return null;
        }
    }

    private static Dictionary<string, JsonElement> Schema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            type = new { type = "string", @enum = Enum.GetNames<ElementType>() },
            purpose = new { type = "string" },
            confidence = new { type = "number" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "type", "purpose", "confidence" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };
}
