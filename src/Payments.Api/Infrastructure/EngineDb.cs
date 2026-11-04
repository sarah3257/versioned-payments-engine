using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;

namespace Payments.Api.Infrastructure;

public class EngineDb(DbContextOptions<EngineDb> options) : DbContext(options)
{
    public DbSet<MonthlyRevision> MonthlyRevisions => Set<MonthlyRevision>();
    public DbSet<RetroBatch> RetroBatches => Set<RetroBatch>();
    public DbSet<Placement> Placements => Set<Placement>();
    public DbSet<ComponentVersion> ComponentVersions => Set<ComponentVersion>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<MaintenancePrice> MaintenancePrices => Set<MaintenancePrice>();
    public DbSet<HumanPrice> HumanPrices => Set<HumanPrice>();
    public DbSet<Institution> Institutions => Set<Institution>();
    public DbSet<PaymentStatusEvent> PaymentStatusEvents => Set<PaymentStatusEvent>();
    public DbSet<VersionAction> VersionActions => Set<VersionAction>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<TaxRateVersion> TaxRateVersions => Set<TaxRateVersion>();
    public DbSet<AgreementVersion> AgreementVersions => Set<AgreementVersion>();
    public DbSet<CalculationRun> CalculationRuns => Set<CalculationRun>();
    public DbSet<TransactionHeader> TransactionHeaders => Set<TransactionHeader>();
    public DbSet<TransactionDetail> TransactionDetails => Set<TransactionDetail>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RetroBatch>().HasKey(x => x.Token);
        b.Entity<MonthlyRevision>().HasIndex(x => x.RunId).IsUnique();
        b.Entity<MonthlyRevision>().HasIndex(x => new {x.PlacementId, x.Month});
        b.Entity<ComponentVersion>().HasIndex(x => new { x.ComponentId, x.Version }).IsUnique();
        b.Entity<ModelVersion>().HasIndex(x => new { x.ModelId, x.Version }).IsUnique();
        b.Entity<ModelVersion>().HasMany(x => x.Components).WithOne().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ModelComponent>().HasIndex(x => new { x.ModelVersionId, x.ComponentId }).IsUnique();
        b.Ignore<ComponentItem>(); b.Ignore<PriceVersion>();
        b.Entity<ComponentVersion>().HasMany(x=>x.MaintenanceItems).WithOne().HasForeignKey(x=>x.ComponentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ComponentVersion>().HasMany(x=>x.HumanItems).WithOne().HasForeignKey(x=>x.ComponentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MaintenancePrice>().ToTable("TAB_PriceListMaintenanceItem");
        b.Entity<HumanPrice>().ToTable("TAB_PriceListHuman");
        b.Entity<MaintenanceSpecification>().ToTable("APP_MaintenanceItemComponent");
        b.Entity<HumanSpecification>().ToTable("APP_HumanComponent");
        b.Entity<ComponentVersion>().HasMany(x => x.Rows).WithOne().HasForeignKey(x => x.ComponentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DecisionRow>().HasMany(x => x.Conditions).WithOne().HasForeignKey(x => x.DecisionRowId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MaintenancePrice>().HasIndex(x=>new{x.PriceKey,x.Version}).IsUnique();
        b.Entity<HumanPrice>().HasIndex(x=>new{x.PriceKey,x.Version}).IsUnique();
        b.Entity<TransactionHeader>().HasIndex(x=>x.ReversesHeaderId).IsUnique();
        b.Entity<TransactionHeader>().Property(x=>x.PaymentStatus).IsConcurrencyToken();
        b.Entity<Consent>().HasIndex(x => new { x.Institution, x.ModelId, x.EffectiveFrom, x.ModelVersion }).IsUnique();
        b.Entity<TaxRateVersion>().HasIndex(x => new { x.Ownership, x.SourceKind, x.Version }).IsUnique();
        b.Entity<AgreementVersion>().HasIndex(x => new { x.Institution, x.Version }).IsUnique();
        b.Entity<CalculationRun>().HasIndex(x => x.RequestKey).IsUnique();
        b.Entity<CalculationRun>().HasMany(x => x.Headers).WithOne().HasForeignKey(x => x.CalculationRunId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TransactionHeader>().HasMany(x => x.Details).WithOne().HasForeignKey(x => x.TransactionHeaderId).OnDelete(DeleteBehavior.Restrict);
        // SQLite stores decimal as TEXT: no float conversion. Arithmetic is in C# decimal.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(decimal)))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<decimal, string>(
                    value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    value => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static async Task InitializeAsync(EngineDb db)
    {
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrade.ApplyAsync(db);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS Placements (Id TEXT NOT NULL PRIMARY KEY, ModelId INTEGER NOT NULL, Institution INTEGER NOT NULL, InstitutionName TEXT NOT NULL, \"From\" TEXT NOT NULL, \"To\" TEXT NOT NULL, Ownership INTEGER NOT NULL, InstitutionType TEXT NOT NULL, SupportLevel INTEGER NOT NULL, Administration TEXT NOT NULL, Population TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS MonthlyRevisions (Id TEXT NOT NULL PRIMARY KEY, PlacementId TEXT NOT NULL, Month TEXT NOT NULL, RunId TEXT NOT NULL, PreviousId TEXT NULL, BatchToken TEXT NULL, CreatedAtUtc TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS RetroBatches (Token TEXT NOT NULL PRIMARY KEY, RequestJson TEXT NOT NULL, ResultJson TEXT NOT NULL, ModelId INTEGER NOT NULL, ModelVersion INTEGER NOT NULL, \"From\" TEXT NOT NULL, \"To\" TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MonthlyRevisions_RunId ON MonthlyRevisions (RunId)");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_MonthlyRevisions_PlacementId_Month ON MonthlyRevisions (PlacementId, Month)");
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        // Extra DB guard: even direct SQL cannot silently rewrite published history.
        string[] tables = ["ComponentVersions", "APP_MaintenanceItemComponent", "APP_HumanComponent", "DecisionRow", "DecisionCondition",
            "ModelVersions", "ModelComponent", "Consents", "TAB_PriceListMaintenanceItem", "TAB_PriceListHuman", "TaxRateVersions", "AgreementVersions",
            "CalculationRuns", "TransactionDetails", "MonthlyRevisions", "RetroBatches", "PaymentStatusEvents", "AuditEvents", "VersionActions"];
        foreach (var table in tables)
            foreach (var op in new[] { "UPDATE", "DELETE" })
            {
                // Identifiers come exclusively from the closed, hard-coded table/op lists above.
                var sql = $"CREATE TRIGGER IF NOT EXISTS immutable_{table}_{op} BEFORE {op} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Published history is immutable'); END;";
                await db.Database.ExecuteSqlRawAsync(sql);
            }
    }
}
