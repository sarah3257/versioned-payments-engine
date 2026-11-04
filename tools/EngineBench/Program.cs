using System.Diagnostics;
using System.Text.Json;
using Payments.Api.Domain;
using Payments.Api.Services;
int placements=args.Length>0?int.Parse(args[0]):1000;int months=args.Length>1?int.Parse(args[1]):12;
var request=new CalculateRequest("bench",1,3404,"synthetic",new(2026,9,15),Ownership.Private,"דירה",4);
var from=new DateOnly(2026,1,1);var to=new DateOnly(2027,12,31);
var components=new List<SelectedComponent>();
for(int i=0;i<37;i++)components.Add(new(new ComponentVersion{ComponentId=100+i,Version=1,Kind=ComponentKind.Maintenance,TemplateVersion="v1",From=from,To=to,MaintenanceItems=[new(){PriceKey="clothes",PaymentPercent=100,IsProfitRequired=true,TaxCalcId=TaxLiability.Both},new(){PriceKey="municipal",PaymentPercent=90,IsProfitRequired=true,TaxCalcId=TaxLiability.Both}]},1,i,"synthetic"));
components.Add(new(new ComponentVersion{ComponentId=200,Version=1,Kind=ComponentKind.Basket,From=from,To=to,Rows=Enumerable.Range(1,200).Select(i=>new DecisionRow{RowKey=i.ToString(),Description="synthetic",ResultAmount=1,Conditions=[new(){Attribute="supportLevel",Operator="BETWEEN",Value="3",ValueTo="5"},new(){Attribute="institutionType",Operator="=",Value="דירה"}]}).ToList()},2,1,"synthetic"));
components.Add(new(new ComponentVersion{ComponentId=201,Version=1,Kind=ComponentKind.Profit,From=from,To=to,ProfitPercent=4},6,1,"synthetic"));
components.Add(new(new ComponentVersion{ComponentId=202,Version=1,Kind=ComponentKind.Tax,From=from,To=to},7,1,"synthetic"));
var plan=new ExecutionPlan(request,1,"40 component synthetic",components,[new(){Id=1,PriceKey="clothes",Kind=ComponentKind.Maintenance,Version=1,Amount=94,From=from,To=to,Description="clothes"},new(){Id=2,PriceKey="municipal",Kind=ComponentKind.Maintenance,Version=1,Amount=187,From=from,To=to,Description="municipal"}],[new(){SourceKind=ComponentKind.Maintenance,Ownership=Ownership.Private,Percent=18,Version=1,TaxName="VAT",From=from,To=to}],[],DateTime.UtcNow);
var engine=new CalculationEngine([new MaintenanceCalculator(),new HumanCalculator(),new BasketCalculator(),new ProfitCalculator(),new TaxCalculator()]);
for(int i=0;i<30;i++)engine.Execute(plan,true);
var reports=new List<object>();
foreach(int degree in new[]{1,Math.Min(4,Environment.ProcessorCount) }.Distinct())
{
 int count=checked(placements*months),errors=0;var samples=new double[count];var clock=Stopwatch.StartNew();
 Parallel.For(0,count,new ParallelOptions{MaxDegreeOfParallelism=degree},i=>{var timer=Stopwatch.StartNew();var result=engine.Execute(plan,true);if(result.Header.Total!=12040.222m||result.Header.Details.Count!=276)Interlocked.Increment(ref errors);samples[i]=timer.Elapsed.TotalMilliseconds;});
 clock.Stop();Array.Sort(samples);var report=new{placements,months,calculations=count,components=40,decisionRows=200,parallelism=degree,seconds=clock.Elapsed.TotalSeconds,calculationsPerSecond=count/clock.Elapsed.TotalSeconds,p95ComputeMs=samples[(int)(count*.95)],errors,scope="in-memory only; full trace; excludes HTTP and DB; synthetic repeated template"};reports.Add(report);Console.WriteLine(JsonSerializer.Serialize(report));
}
Directory.CreateDirectory("test-results");File.WriteAllText("test-results/compute-load.json",JsonSerializer.Serialize(new{timestamp=DateTime.UtcNow,cpu=Environment.ProcessorCount,serverGc=System.Runtime.GCSettings.IsServerGC,reports},new JsonSerializerOptions{WriteIndented=true}));
