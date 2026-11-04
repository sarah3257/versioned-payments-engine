namespace Payments.Api.Domain;
public class MonthlyRevision
{
    public Guid Id {get;set;}
    public string PlacementId {get;set;}="";
    public DateOnly Month {get;set;}
    public Guid RunId {get;set;}
    public Guid? PreviousId {get;set;}
    public string? BatchToken {get;set;}
    public DateTime CreatedAtUtc {get;set;}
}
public class RetroBatch
{
    public string Token {get;set;}="";
    public string InstitutionsJson {get;set;}="";
    public string RequestJson {get;set;}="";
    public string ResultJson {get;set;}="";
    public int ModelId {get;set;}
    public int ModelVersion {get;set;}
    public DateOnly From {get;set;}
    public DateOnly To {get;set;}
    public DateTime CreatedAtUtc {get;set;}
}
public record RetroExecution(PublishModel Draft,string PreviewToken);
