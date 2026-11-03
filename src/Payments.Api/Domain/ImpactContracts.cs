namespace Payments.Api.Domain;

public class Placement
{
    public string Id { get; set; } = "";
    public int ModelId { get; set; }
    public int Institution { get; set; }
    public string InstitutionName { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public Ownership Ownership { get; set; }
    public string InstitutionType { get; set; } = "דירה";
    public int SupportLevel { get; set; } = 4;
    public string Administration { get; set; } = "מוגבלויות";
    public string Population { get; set; } = "";
}
public record ImpactRequest(int ModelId, int TargetVersion, DateOnly From, DateOnly To,
    List<int> Institutions, PublishModel? Draft = null);
public record ConsentBatch(int ModelId, int TargetVersion, DateOnly EffectiveFrom, List<int> Institutions);
public record ImpactLine(string PlacementId, int Institution, DateOnly Month, decimal Factor,
    int? CurrentVersion, int? TargetVersion, decimal? CurrentAmount, decimal? ProposedAmount,
    decimal? Difference, string? Error);
