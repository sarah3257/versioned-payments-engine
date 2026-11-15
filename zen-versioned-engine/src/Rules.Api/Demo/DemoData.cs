using Rules.Domain;

namespace Rules.Api.Demo;

// Business formulas live in examples/templates/*.json, not C# handlers.
public static class DemoData
{
    private static T Load<T>(string relativePath) => Json.Read<T>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "examples", relativePath)));

    public static PublicationPreviewRequest Initial()
    {
        var request = Load<PublicationPreviewRequest>("01-initial-publication.json");
        var templates = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "examples", "templates"), "*.json")
            .Order(StringComparer.Ordinal).Select(path => Json.Read<ComponentTemplate>(File.ReadAllText(path))).ToArray();
        return request with { Package = request.Package with { Templates = templates } };
    }

    public static CalculationRequest Event(string eventId = "event-1", long revision = 1) =>
        Load<CalculationRequest>("02-event.json") with { EventId = eventId, SourceRevision = revision };

    public static PublicationPreviewRequest Emergency(EngineState state)
    {
        if (!state.Models.Any(x => x.Id == "housing")) throw new BusinessException("DEMO_INITIALIZE", "יש לטעון קודם את תרחיש הדיור");
        var request = Load<PublicationPreviewRequest>("03-emergency-publication.json");
        var current = state.Models.Where(x => x.Id == "housing").OrderByDescending(x => x.Version).First();
        var model = current with
        {
            Version = current.Version + 1,
            Supersedes = current.Version,
            Components = [.. current.Components, new("emergency", "emergency", 2, [])]
        };
        return request with { Package = request.Package with { Models = [model] } };
    }
}
