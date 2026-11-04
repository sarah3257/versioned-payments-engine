using System.Text.Json;
using System.Text.Json.Serialization;

namespace Payments.Api.Domain;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, WriteIndented = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
public record PublishItem(string PriceKey, decimal Factor, bool ProfitRequired, TaxLiability TaxLiability);
public record PublishCondition(string Attribute, string Operator, string? Value, string? ValueTo = null);
public record PublishRow(string RowKey, string Description, decimal ResultAmount, string? PriceKey,
    TaxLiability TaxLiability, List<PublishCondition> Conditions);
public record PublishComponent(int ComponentId, int Version, string Name, ComponentKind Kind,
    DateOnly From, DateOnly To, List<PublishItem> Items, List<PublishRow> Rows,
    int? SupportLevel = null, string? InstitutionType = null, string? Administration = null,
    string? Population = null, decimal ProfitPercent = 0, bool UseAgreement = false,
    TaxLiability ProfitTaxLiability = TaxLiability.None, string TemplateVersion = "v1", string SourceReference="POC", string PublishedBy="poc-user");
public record PublishLink(int ComponentId, int Layer, int Order);
public record PublishModel(int ModelId, int Version, string Name, DateOnly From, DateOnly To,
    List<PublishLink> Components, List<int>? Institutions = null, DateOnly? EndCommitment = null, string SourceReference="POC", string PublishedBy="poc-user");
public record PublishConsent(int Institution, int ModelId, int ModelVersion, DateOnly EffectiveFrom);
public record PublishPrice(string PriceKey, int Version, string Description, ComponentKind Kind,
    DateOnly From, DateOnly To, decimal Amount, int UnitMeasureId=1, decimal HourAmount=0);
public record PublishAgreement(int Institution, int Version, DateOnly From, DateOnly To, decimal ProfitPercent);
public record PublishTax(Ownership Ownership, ComponentKind SourceKind, int Version, string TaxName,
    DateOnly From, DateOnly To, decimal Percent);

// MonthlyFactor is explicit: daily/monthly proration policy needs business sign-off.
public record CalculateRequest(string RequestKey, int ModelId, int Institution, string PlacementId,
    DateOnly EventDate, Ownership Ownership, string InstitutionType, int SupportLevel,
    string Administration = "מוגבלויות", string Population = "", decimal MonthlyFactor = 1,
    int? SimulationModelVersion = null, DateOnly? BirthDate=null, int? GenderId=null);
public record SelectedComponent(ComponentVersion Definition, int Layer, int Order, string SelectionReason);
public record ExecutionPlan(CalculateRequest Request, int ModelVersion, string ModelName,
    List<SelectedComponent> Components, List<PriceVersion> Prices,
    List<TaxRateVersion> TaxRates, List<AgreementVersion> Agreements, DateTime SelectedAtUtc);
public record TraceEntry(int ComponentId, int ComponentVersion, int Layer, string Message);
public record CalculationResult(Guid RunId, TransactionHeader Header, List<TraceEntry> Trace, bool Simulation);
public sealed class BusinessException(string message, int status = 422) : Exception(message)
{ public int Status { get; } = status; public string ErrorCode {get;init;}="BUSINESS_RULE"; public int? ComponentId {get;init;} public int? Layer {get;init;} }
