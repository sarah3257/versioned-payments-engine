using System.Text.Json.Nodes;

namespace Rules.Domain;

public sealed record FieldDefinition(string Name, string Type, bool Required = true);
public sealed record TableOutputDefinition(string Name, string Type, string Role, bool Required = true);
public sealed record ParameterCollection(string Path, FieldDefinition[] Fields, string[] ReferenceFields);
public sealed record RuleStep(string Key, string Expression, bool Assert = false);
public sealed record ComponentTemplate(
    string Id, int Version, string Name, FieldDefinition[] Parameters,
    FieldDefinition[] Facts, RuleStep[] Steps, string OutputExpression,
    string Explanation, string? ForEach = null, string[]? GroupBy = null,
    TableOutputDefinition[]? TableOutputs = null, ParameterCollection[]? Collections = null);
public sealed record Condition(string Field, string Operator, JsonNode? Value = null, JsonNode? To = null);
public sealed record TableColumn(string Id, string Field, string Type);
public sealed record DecisionRow(string Id, Dictionary<string, Condition> Conditions, JsonObject Outputs, string Source);
public sealed record DecisionTable(TableColumn[] Columns, string HitPolicy, string NoMatch, DecisionRow[] Rows, bool AllowOverlap = false);
public sealed record ComponentVersion(
    string Id, int Version, string Name, string TemplateId, int TemplateVersion,
    DateOnly From, DateOnly To, JsonObject Parameters, string Source,
    DecisionTable? Table = null, int? Supersedes = null);
public sealed record ComponentUse(string Id, string ComponentId, int Layer, Condition[] Conditions);
public sealed record ModelVersion(string Id, int Version, string Name, string Agreement,
    DateOnly From, DateOnly To, ComponentUse[] Components, string Source, int? Supersedes = null);
public sealed record ReferenceVersion(string Id, int Version, string Kind, DateOnly From,
    DateOnly To, JsonObject Values, string Source, int? Supersedes = null);
public sealed record AgreementVersion(string InstitutionId, string ModelId, string Agreement,
    DateOnly From, DateOnly To, string Source, int Version = 1, int? Supersedes = null);
public sealed record PublicationPackage(string RequestId, string Publisher,
    ComponentTemplate[] Templates, ComponentVersion[] Components,
    ModelVersion[] Models, ReferenceVersion[] References, AgreementVersion[] Agreements);
public sealed record FactPeriod(DateOnly From, DateOnly To, JsonObject Facts);
public sealed record CalculationRequest(string EventId, string PlacementId, string InstitutionId,
    string ModelId, DateOnly From, DateOnly To, FactPeriod[] Periods,
    string? ForcedAgreement = null, long SourceRevision = 1);
public sealed record PublicationPreviewRequest(PublicationPackage Package, CalculationRequest[] Cases);
public sealed record PublishRequest(string Token);
public sealed record CodeError(string Code, string Message);
public sealed class BusinessException(string code, string message, int status = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
