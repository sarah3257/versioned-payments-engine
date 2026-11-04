using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;
using Xunit;
namespace Payments.Tests;
public class GovernanceTests
{
 [Fact] public async Task AtomicPublicationAllocatesVersionAndRollsBackBrokenPackage()
 {
  using var app=new TestApp();var c=app.CreateClient();var model=new PublishModel(20,0,"חדש",new(2026,1,1),new(2027,12,31),[new(20,1,1)]);
  var component=new PublishComponent(20,0,"אחזקה חדשה",ComponentKind.Maintenance,new(2026,1,1),new(2027,12,31),[new("new-price",1,true,TaxLiability.Both)],[]);
  var price=new PublishPrice("new-price",1,"פריט",ComponentKind.Maintenance,new(2026,1,1),new(2027,12,31),50);
  var bad=await c.PostAsJsonAsync("/api/publications",new PublicationPackage(model,[component],[]));Assert.False(bad.IsSuccessStatusCode);
  var ok=await c.PostAsJsonAsync("/api/publications",new PublicationPackage(model,[component],[price]));ok.EnsureSuccessStatusCode();Assert.Equal(1,(await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionNumber").GetInt32());
 }
 [Fact] public async Task RollbackDisablesVersionWithoutMutatingPublishedRows()
 {
  using var app=new TestApp();var c=app.CreateClient();var command=new VersionCommand("Component",1,2,true,null,"בדיקה");
  (await c.PostAsJsonAsync("/api/versions/state",command)).EnsureSuccessStatusCode();
  var r=await c.PostAsJsonAsync("/api/simulations",new CalculateRequest("x",1,3404,"p",new(2026,9,15),Ownership.Private,"דירה",4));r.EnsureSuccessStatusCode();
  var details=(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("header").GetProperty("details").EnumerateArray();Assert.All(details.Where(x=>x.GetProperty("componentId").GetInt32()==1),x=>Assert.Equal(1,x.GetProperty("componentVersion").GetInt32()));
  Assert.Equal(3,await app.With(sp=>sp.GetRequiredService<EngineDb>().ComponentVersions.CountAsync(x=>x.ComponentId==1)));
 }
 [Fact] public async Task SchemaUpgradeImportsLegacyPricesIntoTwoSeparateTables()
 {
  string path=Path.Combine(Path.GetTempPath(),"legacy-"+Guid.NewGuid()+".db");
  await using var db=new EngineDb(new DbContextOptionsBuilder<EngineDb>().UseSqlite("Data Source="+path).Options);
  await db.Database.ExecuteSqlRawAsync("CREATE TABLE PriceVersions (Id INTEGER PRIMARY KEY,PriceKey TEXT,Version INTEGER,Description TEXT,Amount TEXT,Kind INTEGER,\"From\" TEXT,\"To\" TEXT)");
  await db.Database.ExecuteSqlRawAsync("INSERT INTO PriceVersions VALUES (1,'item',1,'אחזקה','94',0,'2026-01-01','2027-12-31'),(2,'doctor',1,'רופא','18000',1,'2026-01-01','2027-12-31')");
  await EngineDb.InitializeAsync(db);await EngineDb.InitializeAsync(db);
  Assert.Equal(1,await db.MaintenancePrices.CountAsync());Assert.Equal(1,await db.HumanPrices.CountAsync());
 }
 [Fact] public async Task AgeAndGenderConditionsAreEvaluatedFromEventDate()
 {
  var r=new CalculateRequest("x",1,3404,"p",new(2026,9,15),Ownership.Private,"דירה",4,BirthDate:new(2016,9,16),GenderId:2);
  Assert.True(BasketCalculator.Matches(new DecisionCondition{Attribute="age",Operator="=",Value="9"},r));
  Assert.True(BasketCalculator.Matches(new DecisionCondition{Attribute="gender",Operator="=",Value="2"},r));
 }
 [Fact] public async Task SamePriceCodeCanExistInMaintenanceAndHumanCatalogs()
 {
  using var app=new TestApp();_=app.CreateClient();await app.With(async sp=>{var service=sp.GetRequiredService<PublicationService>();await service.PublishPriceAsync(new("clothes",1,"מקצוע בשם זהה",ComponentKind.Staff,new(2026,1,1),new(2027,12,31),900));var prices=await sp.GetRequiredService<EngineDb>().PricesAsync();Assert.Equal(2,prices.Count(x=>x.PriceKey=="clothes"));return true;});
 }
}
