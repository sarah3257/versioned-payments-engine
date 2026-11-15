using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace Rules.Domain;

public sealed record StepEvidence(string Key, string Expression, JsonNode? Value);
public sealed record ConditionEvidence(string Field, string Operator, JsonNode? Actual,
    JsonNode? Expected, JsonNode? To, bool Matched);
public sealed record TableEvidence(string RowId, bool Matched, ConditionEvidence[] Conditions, string Source);
public sealed record ComponentTrace(string Id, string ComponentId, int ComponentVersion,
    string UseId, string TemplateId, int TemplateVersion, int Layer,
    DateOnly From, DateOnly To, string Status, JsonObject Input,
    StepEvidence[] Steps, TableEvidence[] Table, JsonNode? NativeTrace, string Source,
    ConditionEvidence[]? Assignment = null);
public sealed record PaymentLine(string Key, string ComponentId, int ComponentVersion,
    string UseId, int Layer, string ItemId, string Group, string Kind,
    bool ProfitRequired, string Tax, decimal RawAmount, decimal Amount,
    string Explanation, string TraceId, string[] BasisKeys);
public sealed record SegmentResult(DateOnly From, DateOnly To, string ModelId, int ModelVersion,
    string Agreement, PaymentLine[] Lines, ComponentTrace[] Traces, JsonObject? Selection = null);
public sealed record MonthResult(DateOnly Month, SegmentResult[] Segments)
{
    public decimal Total => Segments.SelectMany(x => x.Lines).Sum(x => x.Amount);
}
public sealed record CalculationResult(string RunId, string CatalogHash, string EngineVersion,
    string RulesVersion, CalculationRequest Input, MonthResult[] Months, DateTimeOffset CreatedAt)
{
    public decimal Total => Months.Sum(x => x.Total);
}
public sealed record LedgerEntry(string Id, string RunId, string PlacementId, DateOnly Month,
    string Key, decimal Amount, string Kind, string? OriginalEntryId, string TraceId);
public sealed record MonthlyPosition(string PlacementId, DateOnly Month, string RunId, bool Paid);
public sealed record Settlement(string Id, string[] LedgerIds, string Actor, DateTimeOffset At);
public sealed record StoredRun(CalculationResult Result, string RequestHash);
public sealed record LineDifference(string PlacementId, DateOnly Month, string Key,
    decimal Before, decimal After, decimal Delta);
public sealed record PublicationPreview(string Token, string CatalogHash, string PositionHash,
    PublicationPackage Package, CalculationRequest[] Cases, CalculationResult[] Results,
    LineDifference[] Differences, DateTimeOffset ExpiresAt, string[] AffectedModels);
public sealed record PublicationReceipt(string RequestId, string Hash, DateTimeOffset PublishedAt, string Publisher);
public sealed record AuditEntry(string Action, string Actor, DateTimeOffset At, string Subject);
public sealed record FailedEvent(string EventId, string Code, string Message, DateTimeOffset At);
public sealed class EngineState
{
    public List<ComponentTemplate> Templates { get; set; } = [];
    public List<ComponentVersion> Components { get; set; } = [];
    public List<ModelVersion> Models { get; set; } = [];
    public List<ReferenceVersion> References { get; set; } = [];
    public List<AgreementVersion> Agreements { get; set; } = [];
    public List<StoredRun> Runs { get; set; } = [];
    public List<LedgerEntry> Ledger { get; set; } = [];
    public List<MonthlyPosition> Positions { get; set; } = [];
    public List<Settlement> Settlements { get; set; } = [];
    public List<PublicationPreview> Previews { get; set; } = [];
    public List<PublicationReceipt> Publications { get; set; } = [];
    public List<AuditEntry> Audit { get; set; } = [];
    public List<FailedEvent> Failures { get; set; } = [];
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new BusinessException("INVALID_JSON", "נדרש תוכן JSON תקין");
    public static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Write(value))));
    public static string CatalogHash(EngineState s) => Hash(new { s.Templates, s.Components, s.Models, s.References, s.Agreements });
    public static string PositionHash(EngineState s) => Hash(s.Positions);
    public static EngineState Copy(EngineState s) => Read<EngineState>(Write(s));
}
