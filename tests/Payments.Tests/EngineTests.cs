using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Services;
using Xunit;

namespace Payments.Tests;

public sealed class TestApp : WebApplicationFactory<Program>
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "payments-engine-tests", Guid.NewGuid().ToString());
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(folder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["DatabasePath"] = Path.Combine(folder, "test.db"), ["Logging:LogLevel:Default"] = "Warning" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<EngineDb>>();
            services.RemoveAll<EngineDb>();
            services.AddDbContext<EngineDb>(o => o.UseSqlite($"Data Source={Path.Combine(folder, "test.db")};Foreign Keys=True;Default Timeout=30"));
        });
    }
    public async Task<T> With<T>(Func<IServiceProvider, Task<T>> action)
    { using var scope = Services.CreateScope(); return await action(scope.ServiceProvider); }
}

public class EngineTests
{
    static CalculateRequest Request(string date = "2026-08-15", int institution = 12380, int support = 4) =>
        new(Guid.NewGuid().ToString(), 1, institution, "placement-1", DateOnly.Parse(date), institution == 12380 ? Ownership.Public : Ownership.Private, "דירה", support);
    static Task<CalculationResult> Run(TestApp app, CalculateRequest r, bool simulation = false, bool fail = false) =>
        app.With(sp => sp.GetRequiredService<RunService>().CalculateAsync(r, simulation, fail));

    [Theory]
    [InlineData("2026-08-15",12380,2,1)]
    [InlineData("2026-08-15",3404,1,1)]
    [InlineData("2026-03-15",12380,1,3)]
    [InlineData("2026-09-15",3404,1,2)]
    [InlineData("2026-09-15",12380,2,2)]
    [InlineData("2026-10-15",3404,2,2)]
    [InlineData("2026-09-01",3404,1,2)]
    [InlineData("2026-06-30",12380,1,3)]
    public async Task VersionSelectionMatchesBusinessScenarios(string date,int institution,int modelVersion,int componentVersion)
    {
        using var app = new TestApp(); var result = await Run(app, Request(date, institution));
        Assert.Equal(modelVersion, result.Header.ModelVersion);
        Assert.All(result.Header.Details.Where(d => d.ComponentId == 1), d => Assert.Equal(componentVersion, d.ComponentVersion));
    }
    [Fact] public async Task IndependentGoldenTotalForBaseline()
    {
        using var app = new TestApp(); var result = await Run(app, Request("2026-08-15",3404));
        // 94+187+1800=2081; profit 83.240; VAT 374.580. Profit is not tax liable in seed.
        Assert.Equal(2538.820m, result.Header.Total);
        Assert.Equal(6, result.Header.Details.Count); // Two tax bases: maintenance and staff.
    }
    [Fact] public async Task ReferencePriceChangesWithoutModelPublication()
    {
        using var app = new TestApp(); var result = await Run(app, Request("2026-09-15",3404));
        Assert.Equal(1, result.Header.ModelVersion);
        Assert.Equal(1850m,result.Header.Details.Single(d=>d.Kind==ComponentKind.Staff).Amount);
        Assert.Equal(2577.006m,result.Header.Total);
    }
    [Fact] public async Task DuplicateRequestReturnsIdenticalOriginal()
    {
        using var app = new TestApp(); var r=Request(); var a=await Run(app,r);var b=await Run(app,r);
        Assert.Equal(JsonDefaults.Write(a),JsonDefaults.Write(b));
        Assert.Equal(1,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
    }
    [Fact] public async Task SameKeyDifferentContentIsConflict()
    {
        using var app = new TestApp();var r=Request();await Run(app,r);
        var e=await Assert.ThrowsAsync<BusinessException>(()=>Run(app,r with {SupportLevel=5}));Assert.Equal(409,e.Status);
    }
    [Fact] public async Task ConcurrentDuplicateCreatesOnlyOneRun()
    {
        using var app=new TestApp();var r=Request();var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Run(app,r)));
        Assert.Single(results.Select(x=>x.RunId).Distinct());
        Assert.Equal(1,await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionHeaders.CountAsync()));
    }
    [Fact] public async Task CommitFailureRollsBackAllTablesAndRetryWorks()
    {
        using var app=new TestApp();var r=Request();await Assert.ThrowsAsync<BusinessException>(()=>Run(app,r,fail:true));
        Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
        Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().TransactionDetails.CountAsync()));
        await Run(app,r);Assert.Equal(1,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));
    }
    [Fact] public async Task ReplayUsesOriginalPricesAfterNewPublication()
    {
        using var app=new TestApp();var r=Request();var original=await Run(app,r);
        await app.With(async sp=>{await sp.GetRequiredService<PublicationService>().PublishPriceAsync(new("doctor",3,"מחיר מתוקן",ComponentKind.Staff,new(2026,1,1),new(2027,12,31),20000));return true;});
        var replay=await app.With(sp=>sp.GetRequiredService<RunService>().ReplayAsync(original.RunId));
        Assert.Contains("\"matchesOriginal\": true",JsonDefaults.Write(replay));
        var fresh=await Run(app,r with {RequestKey=Guid.NewGuid().ToString()});Assert.NotEqual(original.Header.Total,fresh.Header.Total);
    }
    [Fact] public async Task RetroComponentAppliesToBothModelsWithoutRepublishing()
    {
        using var app=new TestApp();await app.With(async sp=>{var pub=sp.GetRequiredService<PublicationService>();
            await pub.PublishComponentAsync(new(1,4,"תיקון",ComponentKind.Maintenance,new(2026,1,1),new(2027,12,31),[new("clothes",2,true,TaxLiability.Both)],[]));return true;});
        var a=await Run(app,Request("2026-09-15",12380));var b=await Run(app,Request("2026-09-15",3404));
        Assert.Equal(2,a.Header.ModelVersion);Assert.Equal(1,b.Header.ModelVersion);
        Assert.Equal(188m,a.Header.Details.Single(d=>d.ComponentId==1).Amount);
        Assert.Equal(188m,b.Header.Details.Single(d=>d.ComponentId==1).Amount);
        Assert.Equal(2,await app.With(sp=>sp.GetRequiredService<EngineDb>().ModelVersions.CountAsync()));
    }
    [Fact] public async Task AssociationSkipsNonMatchingStaff()
    {using var app=new TestApp();var r=await Run(app,Request(support:5));Assert.DoesNotContain(r.Header.Details,d=>d.ComponentId==12);Assert.Contains(r.Header.Details,d=>d.ComponentId==4);}
    [Fact] public async Task SimulationDoesNotPersistAndCanOverrideConsent()
    {using var app=new TestApp();var r=await Run(app,Request(institution:3404) with {SimulationModelVersion=2},true);Assert.Equal(2,r.Header.ModelVersion);Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));}
    [Fact] public async Task NoValidModelFailsBeforeWrite()
    {using var app=new TestApp();await Assert.ThrowsAsync<BusinessException>(()=>Run(app,Request("2028-01-15")));Assert.Equal(0,await app.With(sp=>sp.GetRequiredService<EngineDb>().CalculationRuns.CountAsync()));}
    [Fact] public async Task DecisionCollectsAllRowsIncludingWildcardAndPriceFallback()
    {
        using var app=new TestApp();await app.With(async sp=>{await sp.GetRequiredService<PublicationService>().PublishComponentAsync(new(7,2,"סל",ComponentKind.Basket,new(2026,1,1),new(2027,12,31),[],
            [new("a","א",10,null,TaxLiability.None,[new("supportLevel","BETWEEN","3","5")]),
             new("b","ב",0,"clothes",TaxLiability.None,[new("institutionType","=",null)]),
             new("c","ג",999,null,TaxLiability.None,[new("supportLevel","=","5")])]));return true;});
        var r=await Run(app,Request());Assert.Equal(2,r.Header.Details.Count(d=>d.Kind==ComponentKind.Basket));
        Assert.Equal(104m,r.Header.Details.Where(d=>d.Kind==ComponentKind.Basket).Sum(d=>d.Amount));
        Assert.Contains(r.Trace,t=>t.Message.Contains("לא התאימה"));
    }
    [Fact] public async Task ModelPublicationRejectsCoverageGap()
    {using var app=new TestApp();await Assert.ThrowsAsync<BusinessException>(()=>app.With(sp=>sp.GetRequiredService<PublicationService>().PublishModelAsync(new(9,1,"חסר",new(2026,1,1),new(2028,1,1),[new(1,1,1)]))));}
    [Fact] public async Task PublishedRowsCannotBeUpdatedEvenBySql()
    {using var app=new TestApp();await Assert.ThrowsAnyAsync<Exception>(()=>app.With(async sp=>await sp.GetRequiredService<EngineDb>().Database.ExecuteSqlRawAsync("UPDATE ComponentVersions SET Name='changed' WHERE ComponentId=1")));}
    [Fact] public async Task UnknownDecisionAttributeIsRejected()
    {using var app=new TestApp();await Assert.ThrowsAsync<BusinessException>(()=>app.With(sp=>sp.GetRequiredService<PublicationService>().PublishComponentAsync(new(7,2,"סל",ComponentKind.Basket,new(2026,1,1),new(2027,1,1),[],[new("a","",1,null,TaxLiability.None,[new("unknown","=","x")])]))));}
    [Fact] public async Task HttpEndpointsReturnStructuredErrorsAndResult()
    {
        using var app=new TestApp();var client=app.CreateClient();
        var success=await client.PostAsJsonAsync("/api/calculations",Request(),JsonDefaults.Options);Assert.Equal(HttpStatusCode.OK,success.StatusCode);
        var bad=await client.PostAsJsonAsync("/api/calculations",Request() with {MonthlyFactor=2},JsonDefaults.Options);Assert.Equal(HttpStatusCode.UnprocessableEntity,bad.StatusCode);
        Assert.Contains("error",await bad.Content.ReadAsStringAsync());
    }
    [Fact] public async Task FortyComponentsAndManyVersionsUseSameCalculationCode()
    {
        using var app = new TestApp();
        await app.With(async sp =>
        {
            var pub = sp.GetRequiredService<PublicationService>();
            for (int id = 100; id < 140; id++)
                await pub.PublishComponentAsync(new(id,1,"רכיב עומס לימודי",ComponentKind.Maintenance,new(2026,1,1),new(2027,12,31),[new("clothes",1,false,TaxLiability.None)],[]));
            for (int version = 2; version <= 11; version++)
                await pub.PublishComponentAsync(new(100,version,"אותו מחשב בגרסה נוספת",ComponentKind.Maintenance,new(2026,9,1),new(2027,12,31),[new("clothes",1.1m,false,TaxLiability.None)],[]));
            await pub.PublishModelAsync(new(90,1,"40 רכיבים",new(2026,1,1),new(2027,12,31),Enumerable.Range(100,40).Select((id,i)=>new PublishLink(id,1,i+1)).ToList()));
            return true;
        });
        var r = await Run(app, Request("2026-09-15") with { ModelId = 90 });
        Assert.Equal(40,r.Header.Details.Count);
        Assert.Equal(3769.4m,r.Header.Total);
        Assert.Equal(11,r.Header.Details.Single(d=>d.ComponentId==100).ComponentVersion);
    }
    [Fact] public async Task AgreementProfitAndTaxOnProfitUseAccumulatedDetails()
    {
        using var app = new TestApp();
        await app.With(async sp =>
        {
            await sp.GetRequiredService<PublicationService>().PublishComponentAsync(new(10,2,"רווח לפי הסכם וחבות מס",ComponentKind.Profit,new(2026,1,1),new(2027,12,31),[],[],UseAgreement:true,ProfitTaxLiability:TaxLiability.Both));
            return true;
        });
        var r = await Run(app, Request("2026-09-15"));
        Assert.Equal(106.306m,r.Header.Details.Single(d=>d.Kind==ComponentKind.Profit).Amount);
        Assert.Equal(19.135m,r.Header.Details.Single(d=>d.ItemKey=="tax:Profit").Amount);
    }
}
