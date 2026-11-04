namespace Payments.Api.Domain;
public class MaintenancePrice
{
 public long Id {get;set;} public string PriceKey {get;set;}=""; public int Version {get;set;} public string ItemName {get;set;}="";
 public decimal Amount {get;set;} public int UnitMeasureId {get;set;}=1; public bool IsVat {get;set;}=true; public bool IsProfit {get;set;}=true;
 public DateOnly From {get;set;} public DateOnly To {get;set;} public DateTime UpdateDate {get;set;}=DateTime.UtcNow;
}
public class HumanPrice
{
 public long Id {get;set;} public string PriceKey {get;set;}=""; public int Version {get;set;} public string JobTypeName {get;set;}="";
 public decimal MonthlyAmount {get;set;} public decimal HourAmount {get;set;} public bool IsProfit {get;set;}=true;
 public DateOnly From {get;set;} public DateOnly To {get;set;} public DateTime UpdateDate {get;set;}=DateTime.UtcNow;
}
public class MaintenanceSpecification
{
 public long Id {get;set;} public long ComponentVersionId {get;set;} public string PriceKey {get;set;}="";
 public decimal PaymentPercent {get;set;} public bool IsProfitRequired {get;set;} public TaxLiability TaxCalcId {get;set;}
}
public class HumanSpecification
{
 public long Id {get;set;} public long ComponentVersionId {get;set;} public string PriceKey {get;set;}="";
 public decimal TotalCountPosition {get;set;} public bool IsProfitRequired {get;set;} public TaxLiability TaxCalcId {get;set;}
}
public class Institution
{
 public int Id {get;set;} public string Name {get;set;}=""; public Ownership Ownership {get;set;}
 public string InstitutionType {get;set;}="דירה"; public int BusinessYearEndMonth {get;set;}=12;
 public int BusinessYearEndDay {get;set;}=31; public bool IsActive {get;set;}=true;
}
public class PaymentStatusEvent
{
 public long Id {get;set;} public Guid HeaderId {get;set;} public string PreviousStatus {get;set;}="";
 public string NewStatus {get;set;}=""; public DateTime ChangedAtUtc {get;set;} public string Reason {get;set;}="";
}
public class VersionAction
{
 public long Id {get;set;} public string EntityType {get;set;}=""; public int BusinessId {get;set;} public int Version {get;set;}
 public bool Disabled {get;set;} public DateOnly? ClosedAfter {get;set;} public string Reason {get;set;}="";
 public DateTime ChangedAtUtc {get;set;}=DateTime.UtcNow;
}
public class AuditEvent
{
 public long Id {get;set;} public string Action {get;set;}=""; public string Subject {get;set;}="";
 public string Actor {get;set;}="poc-user"; public string Payload {get;set;}=""; public DateTime CreatedAtUtc {get;set;}=DateTime.UtcNow;
}
