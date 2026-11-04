using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;
using Xunit;
namespace Payments.Tests;
public class BusinessCompletionTests
{
 static async Task<PublishModel> Setup(TestApp app,List<int>? institutions=null)
 {
  _=app.CreateClient();return await app.With(async sp=>{await sp.GetRequiredService<RetroService>().SeedAsync();var m=await sp.GetRequiredService<EngineDb>().ModelVersions.Include(x=>x.Components).SingleAsync(x=>x.ModelId==1&&x.Version==2);return new PublishModel(1,3,"ללא רווח",new(2026,11,1),new(2027,12,31),m.Components.Where(x=>x.ComponentId!=10).Select(x=>new PublishLink(x.ComponentId,x.Layer,x.Order)).ToList(),institutions);});
 }
 static async Task<string> Token(HttpClient c,PublishModel d){var r=await c.PostAsJsonAsync("/api/model-publication/simulate",d);r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("previewToken").GetString()!;}
 [Fact] public async Task PaidHeaderCreatesNegativeOriginalAndPositiveNewCalculation()
 {
  using var app=new TestApp();var draft=await Setup(app,[3404]);var c=app.CreateClient();
  var header=await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionHeaders.AsNoTracking().FirstAsync(x=>x.PlacementId=="demo-b-1"&&x.PaymentMonth==new DateOnly(2026,11,1)));
  (await c.PostAsJsonAsync($"/api/headers/{header.Id}/payment-status",new ChangePaymentStatus("Paid","Pending"))).EnsureSuccessStatusCode();
  string token=await Token(c,draft);(await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
  await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();var reversal=await db.TransactionHeaders.Include(x=>x.Details).SingleAsync(x=>x.ReversesHeaderId==header.Id);Assert.Equal(-header.Total,reversal.Total);Assert.Equal(reversal.Total,reversal.Details.Sum(x=>x.Amount));Assert.Equal("Paid",(await db.TransactionHeaders.SingleAsync(x=>x.Id==header.Id)).PaymentStatus);Assert.Equal(1,await db.RetroBatches.CountAsync());Assert.False(await db.Consents.AnyAsync(x=>x.Institution==12380&&x.ModelVersion==3));return true;});
  (await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).EnsureSuccessStatusCode();
  Assert.Equal(1,await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionHeaders.CountAsync(x=>x.EntryType=="Reversal")));
 }
 [Fact] public async Task PlannedHeadersAreReplacedWithoutReversalAndYearCapsForecast()
 {
  using var app=new TestApp();var draft=await Setup(app,[3404]);var c=app.CreateClient();
  var r=await c.PostAsJsonAsync("/api/model-publication/simulate",draft);r.EnsureSuccessStatusCode();var json=await r.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(4,json.GetProperty("monthCount").GetInt32());
  (await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,json.GetProperty("previewToken").GetString()!))).EnsureSuccessStatusCode();
  await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();Assert.Equal(0,await db.TransactionHeaders.CountAsync(x=>x.EntryType=="Reversal"));Assert.Equal(4,await db.TransactionHeaders.CountAsync(x=>x.PaymentStatus=="Superseded"));return true;});
  var forecast=await c.PostAsJsonAsync("/api/forecasts",new ForecastRequest("year-cap","demo-b-1",new(2026,11,1)));forecast.EnsureSuccessStatusCode();Assert.Equal("2026-12-31",(await forecast.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("end").GetString());
 }
 [Fact] public async Task PaymentStatusChangeInvalidatesPreviewAndConcurrentEditConflicts()
 {
  using var app=new TestApp();var draft=await Setup(app,[3404]);var c=app.CreateClient();string token=await Token(c,draft);
  var header=await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionHeaders.FirstAsync(x=>x.PlacementId=="demo-b-1"&&x.PaymentMonth==new DateOnly(2026,11,1)));
  var edits=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>c.PostAsJsonAsync($"/api/headers/{header.Id}/payment-status",new ChangePaymentStatus("Paid","Pending"))));Assert.Single(edits,x=>x.IsSuccessStatusCode);Assert.Single(edits,x=>x.StatusCode==HttpStatusCode.Conflict);
  Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/model-publication/execute",new RetroExecution(draft,token))).StatusCode);
 }
 [Fact] public async Task SplitTablesContainDistinctBusinessFieldsAndNoSharedMappedTables()
 {
  using var app=new TestApp();_=app.CreateClient();await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();Assert.True(await db.MaintenancePrices.AnyAsync());Assert.True(await db.HumanPrices.AnyAsync());Assert.DoesNotContain(db.Model.GetEntityTypes(),x=>x.GetTableName()=="PriceVersions"||x.GetTableName()=="ComponentItem");var components=await db.ComponentsAsync();Assert.All(components.Where(x=>x.Kind==ComponentKind.Maintenance),x=>Assert.NotEmpty(x.MaintenanceItems));Assert.All(components.Where(x=>x.Kind==ComponentKind.Staff),x=>Assert.NotEmpty(x.HumanItems));return true;});
 }
 [Fact] public async Task FiscalYearAndCommitmentUseEarlierDate()
 {
  using var app=new TestApp();_=app.CreateClient();await app.With(async sp=>{var db=sp.GetRequiredService<EngineDb>();db.Placements.Add(new Placement{Id="short",ModelId=1,Institution=3404,InstitutionName="ב",From=new(2026,11,10),To=new(2026,11,20),Ownership=Ownership.Private});await db.SaveChangesAsync();return true;});var c=app.CreateClient();var r=await c.PostAsJsonAsync("/api/forecasts",new ForecastRequest("short","short",new(2026,11,1)));r.EnsureSuccessStatusCode();var j=await r.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("2026-11-20",j.GetProperty("end").GetString());Assert.Single(j.GetProperty("items").EnumerateArray());
 }
 [Fact] public async Task ParallelSimulationsHaveDeterministicAmountsAndNoWrites()
 {
  using var app=new TestApp();var c=app.CreateClient();var request=new CalculateRequest("parallel",1,3404,"p",new(2026,9,15),Ownership.Private,"דירה",4);
  var responses=await Task.WhenAll(Enumerable.Range(0,32).Select(_=>c.PostAsJsonAsync("/api/simulations",request)));
  var totals=new List<decimal>();foreach(var r in responses){r.EnsureSuccessStatusCode();totals.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("header").GetProperty("total").GetDecimal());}Assert.Single(totals.Distinct());Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
 }
}
