using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;
namespace Payments.Api.Infrastructure;
// DDL identifiers below come only from the compiled EF model, never request input.
#pragma warning disable EF1003
public static class SchemaUpgrade
{
 public static async Task ApplyAsync(EngineDb db)
 {
  await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_Consents_Institution_ModelId_EffectiveFrom");
  var script=db.Database.GenerateCreateScript();
  script=script.Replace("CREATE TABLE ","CREATE TABLE IF NOT EXISTS ").Replace("CREATE UNIQUE INDEX ","CREATE UNIQUE INDEX IF NOT EXISTS ").Replace("CREATE INDEX ","CREATE INDEX IF NOT EXISTS ");
  // First create new tables, then add columns, then indexes that may depend on new columns.
  var statements=script.Split(';',StringSplitOptions.RemoveEmptyEntries);
  foreach(var sql in statements.Where(s=>!s.Contains("CREATE INDEX")&&!s.Contains("CREATE UNIQUE INDEX")))await db.Database.ExecuteSqlRawAsync(sql);
  await db.Database.OpenConnectionAsync();
  try
  {
   foreach(var entity in db.Model.GetEntityTypes())
   {
    string table=entity.GetTableName()!;var store=StoreObjectIdentifier.Table(table,entity.GetSchema());
    await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="PRAGMA table_info(\""+table.Replace("\"","\"\"")+"\")";
    var cols=new HashSet<string>();await using(var reader=await command.ExecuteReaderAsync())while(await reader.ReadAsync())cols.Add(reader.GetString(1));
    foreach(var p in entity.GetProperties())
    {
     string name=p.GetColumnName(store)!;if(cols.Contains(name))continue;
     string type=p.GetColumnType()??"TEXT",value=p.ClrType==typeof(string)?"''":p.ClrType==typeof(DateTime)?"'0001-01-01 00:00:00'":p.ClrType==typeof(DateOnly)?"'0001-01-01'":"0";
     if(name=="PaymentStatus")value="'Pending'";if(name=="EntryType")value="'Calculation'";
     await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{name}\" {type}"+(p.IsNullable?"":$" NOT NULL DEFAULT {value}"));
    }
   }
  }
  finally{await db.Database.CloseConnectionAsync();}
  foreach(var sql in statements.Where(s=>s.Contains("CREATE INDEX")||s.Contains("CREATE UNIQUE INDEX")))await db.Database.ExecuteSqlRawAsync(sql);
  // Import the earlier POC layout once. Legacy tables are retained as backup, never used by the engine.
  await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS __SchemaVersions (Id TEXT NOT NULL PRIMARY KEY)");
  await db.Database.OpenConnectionAsync();bool migrated;bool oldPrices;bool oldItems;
  try
  {
   await using var c=db.Database.GetDbConnection().CreateCommand();c.CommandText="SELECT COUNT(*) FROM __SchemaVersions WHERE Id='split-business-v2'";migrated=Convert.ToInt32(await c.ExecuteScalarAsync())>0;
   c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PriceVersions'";oldPrices=Convert.ToInt32(await c.ExecuteScalarAsync())>0;
   c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ComponentItem'";oldItems=Convert.ToInt32(await c.ExecuteScalarAsync())>0;
  }finally{await db.Database.CloseConnectionAsync();}
  if(!migrated)
  {
   await using var tx=await db.Database.BeginTransactionAsync();
   if(oldPrices)
   {
    await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO TAB_PriceListMaintenanceItem (Id,PriceKey,Version,ItemName,Amount,UnitMeasureId,IsVat,IsProfit,\"From\",\"To\",UpdateDate) SELECT Id,PriceKey,Version,Description,Amount,1,1,1,\"From\",\"To\",datetime('now') FROM PriceVersions WHERE Kind=0");
    await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO TAB_PriceListHuman (Id,PriceKey,Version,JobTypeName,MonthlyAmount,HourAmount,IsProfit,\"From\",\"To\",UpdateDate) SELECT Id,PriceKey,Version,Description,Amount,'0',1,\"From\",\"To\",datetime('now') FROM PriceVersions WHERE Kind=1");
   }
   if(oldItems)
   {
    await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO APP_MaintenanceItemComponent (Id,ComponentVersionId,PriceKey,PaymentPercent,IsProfitRequired,TaxCalcId) SELECT i.Id,i.ComponentVersionId,i.PriceKey,CAST(ROUND(CAST(i.Factor AS REAL)*100,3) AS TEXT),i.ProfitRequired,i.TaxLiability FROM ComponentItem i JOIN ComponentVersions c ON c.Id=i.ComponentVersionId WHERE c.Kind=0");
    await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO APP_HumanComponent (Id,ComponentVersionId,PriceKey,TotalCountPosition,IsProfitRequired,TaxCalcId) SELECT i.Id,i.ComponentVersionId,i.PriceKey,i.Factor,i.ProfitRequired,i.TaxLiability FROM ComponentItem i JOIN ComponentVersions c ON c.Id=i.ComponentVersionId WHERE c.Kind=1");
   }
   await db.Database.ExecuteSqlRawAsync("INSERT INTO __SchemaVersions (Id) VALUES ('split-business-v2')");await tx.CommitAsync();
  }
  await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS immutable_TransactionHeaders_UPDATE");
  await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS protect_HeaderFinancials");
  await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS protect_HeaderFinancials BEFORE UPDATE ON TransactionHeaders WHEN NEW.Id<>OLD.Id OR NEW.Total<>OLD.Total OR NEW.PlacementId<>OLD.PlacementId OR NEW.ModelId<>OLD.ModelId OR NEW.ModelVersion<>OLD.ModelVersion OR NEW.PaymentMonth<>OLD.PaymentMonth OR NEW.CalculationRunId<>OLD.CalculationRunId OR NEW.EntryType<>OLD.EntryType OR NEW.Institution<>OLD.Institution OR NEW.ReversesHeaderId IS NOT OLD.ReversesHeaderId OR NEW.ReplacedHeaderId IS NOT OLD.ReplacedHeaderId BEGIN SELECT RAISE(ABORT,'Financial history is immutable'); END");
  await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS immutable_TransactionHeaders_DELETE BEFORE DELETE ON TransactionHeaders BEGIN SELECT RAISE(ABORT,'Financial history is immutable'); END");
 }
}
