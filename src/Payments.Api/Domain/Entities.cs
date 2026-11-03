namespace Payments.Api.Domain;

public enum ComponentKind { Maintenance, Staff, Basket, Profit, Tax }
public enum TaxLiability { None, Private, Public, Both }
public enum Ownership { Private, Public }

// Published definitions are append-only. A new business change gets a new version.
public class ComponentVersion
{
    public long Id { get; set; }
    public int ComponentId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public ComponentKind Kind { get; set; }
    public string TemplateVersion { get; set; } = "v1";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int? SupportLevel { get; set; }
    public string? InstitutionType { get; set; }
    public string? Administration { get; set; }
    public string? Population { get; set; }
    public decimal ProfitPercent { get; set; }
    public bool UseAgreement { get; set; }
    public TaxLiability ProfitTaxLiability { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public List<ComponentItem> Items { get; set; } = [];
    public List<DecisionRow> Rows { get; set; } = [];
}
public class ComponentItem
{
    public long Id { get; set; }
    public long ComponentVersionId { get; set; }
    public string PriceKey { get; set; } = "";
    public decimal Factor { get; set; } = 1;
    public bool ProfitRequired { get; set; }
    public TaxLiability TaxLiability { get; set; }
}
public class DecisionRow
{
    public long Id { get; set; }
    public long ComponentVersionId { get; set; }
    public string RowKey { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal ResultAmount { get; set; }
    public string? PriceKey { get; set; }
    public TaxLiability TaxLiability { get; set; }
    public List<DecisionCondition> Conditions { get; set; } = [];
}
public class DecisionCondition
{
    public long Id { get; set; }
    public long DecisionRowId { get; set; }
    public string Attribute { get; set; } = "";
    public string Operator { get; set; } = "=";
    public string? Value { get; set; }
    public string? ValueTo { get; set; }
}
public class ModelVersion
{
    public long Id { get; set; }
    public int ModelId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public List<ModelComponent> Components { get; set; } = [];
}
public class ModelComponent
{
    public long Id { get; set; }
    public long ModelVersionId { get; set; }
    public int ComponentId { get; set; }
    public int Layer { get; set; }
    public int Order { get; set; }
}
public class Consent
{
    public long Id { get; set; }
    public int Institution { get; set; }
    public int ModelId { get; set; }
    public int ModelVersion { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}
public class PriceVersion
{
    public long Id { get; set; }
    public string PriceKey { get; set; } = "";
    public int Version { get; set; }
    public string Description { get; set; } = "";
    public ComponentKind Kind { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal Amount { get; set; }
}
public class AgreementVersion
{
    public long Id { get; set; }
    public int Institution { get; set; }
    public int Version { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal ProfitPercent { get; set; }
}
public class TaxRateVersion
{
    public long Id { get; set; }
    public Ownership Ownership { get; set; }
    public ComponentKind SourceKind { get; set; }
    public int Version { get; set; }
    public string TaxName { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal Percent { get; set; }
}
public class CalculationRun
{
    public Guid Id { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int ModelId { get; set; }
    public int ModelVersion { get; set; }
    public DateOnly EventDate { get; set; }
    public int Institution { get; set; }
    public string Status { get; set; } = "Completed";
    public string EngineVersion { get; set; } = "engine-v1";
    public string RequestJson { get; set; } = "";
    public string PlanJson { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTime CompletedAtUtc { get; set; }
    public List<TransactionHeader> Headers { get; set; } = [];
}
public class TransactionHeader
{
    public Guid Id { get; set; }
    public Guid CalculationRunId { get; set; }
    public string PlacementId { get; set; } = "";
    public int ModelId { get; set; }
    public int ModelVersion { get; set; }
    public int Institution { get; set; }
    public DateOnly PaymentMonth { get; set; }
    public string Status { get; set; } = "Pending";
    public decimal Total { get; set; }
    public List<TransactionDetail> Details { get; set; } = [];
}
public class TransactionDetail
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Id { get; set; }
    public Guid TransactionHeaderId { get; set; }
    public int ComponentId { get; set; }
    public int ComponentVersion { get; set; }
    public string TemplateVersion { get; set; } = "v1";
    public ComponentKind Kind { get; set; }
    public int Layer { get; set; }
    public string ItemKey { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public bool ProfitRequired { get; set; }
    public TaxLiability TaxLiability { get; set; }
    public string Explanation { get; set; } = "";
}
