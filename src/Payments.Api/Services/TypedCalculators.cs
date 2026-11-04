using Payments.Api.Domain;
namespace Payments.Api.Services;
public class MaintenanceCalculator : IComponentCalculator
{
 public ComponentKind Kind=>ComponentKind.Maintenance;
 public void Calculate(SelectedComponent s,CalculationContext c)
 {
  var specs=s.Definition.MaintenanceItems.Count>0?s.Definition.MaintenanceItems:s.Definition.Items.Select(i=>new MaintenanceSpecification{PriceKey=i.PriceKey,PaymentPercent=i.Factor*100,IsProfitRequired=i.ProfitRequired,TaxCalcId=i.TaxLiability}).ToList();
  foreach(var item in specs.OrderBy(i=>i.PriceKey,StringComparer.Ordinal))
  {
   var price=c.Price(item.PriceKey,Kind);if(price.Kind!=Kind)throw new BusinessException("פריט אינו במחירון אחזקה");if(price.UnitMeasureId!=1)throw new BusinessException("נדרש מחיר אחזקה חודשי");
   var factor=item.PaymentPercent/100;var amount=price.Amount*factor*c.Plan.Request.MonthlyFactor;
   c.Add(s,item.PriceKey,price.Description,amount,item.IsProfitRequired,item.TaxCalcId,$"מחיר {price.Amount} (שורת מחירון {price.Id}, גרסה {price.Version}) × מקדם {factor} × חלק חודש {c.Plan.Request.MonthlyFactor}");
  }
 }
}
public class HumanCalculator : IComponentCalculator
{
 public ComponentKind Kind=>ComponentKind.Staff;
 public void Calculate(SelectedComponent s,CalculationContext c)
 {
  var specs=s.Definition.HumanItems.Count>0?s.Definition.HumanItems:s.Definition.Items.Select(i=>new HumanSpecification{PriceKey=i.PriceKey,TotalCountPosition=i.Factor,IsProfitRequired=i.ProfitRequired,TaxCalcId=i.TaxLiability}).ToList();
  foreach(var item in specs.OrderBy(i=>i.PriceKey,StringComparer.Ordinal))
  {
   var price=c.Price(item.PriceKey,Kind);if(price.Kind!=Kind)throw new BusinessException("מקצוע אינו במחירון כוח אדם");
   var amount=price.Amount*item.TotalCountPosition*c.Plan.Request.MonthlyFactor;
   c.Add(s,item.PriceKey,price.Description,amount,item.IsProfitRequired,item.TaxCalcId,$"מחיר {price.Amount} (שורת מחירון {price.Id}, גרסה {price.Version}) × מקדם {item.TotalCountPosition} × חלק חודש {c.Plan.Request.MonthlyFactor}");
  }
 }
}
