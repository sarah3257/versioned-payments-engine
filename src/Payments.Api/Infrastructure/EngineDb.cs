using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;

namespace Payments.Api.Infrastructure;

public class EngineDb(DbContextOptions<EngineDb> options) : DbContext(options)
{
    public DbSet<ComponentVersion> ComponentVersions => Set<ComponentVersion>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<PriceVersion> PriceVersions => Set<PriceVersion>();
    public DbSet<TaxRateVersion> TaxRateVersions => Set<TaxRateVersion>();
    public DbSet<AgreementVersion> AgreementVersions => Set<AgreementVersion>();
    public DbSet<CalculationRun> CalculationRuns => Set<CalculationRun>();
    public DbSet<TransactionHeader> TransactionHeaders => Set<TransactionHeader>();
    public DbSet<TransactionDetail> TransactionDetails => Set<TransactionDetail>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ComponentVersion>().HasIndex(x => new { x.ComponentId, x.Version }).IsUnique();
        b.Entity<ModelVersion>().HasIndex(x => new { x.ModelId, x.Version }).IsUnique();
        b.Entity<ModelVersion>().HasMany(x => x.Components).WithOne().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ModelComponent>().HasIndex(x => new { x.ModelVersionId, x.ComponentId }).IsUnique();
        b.Entity<ComponentVersion>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ComponentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ComponentVersion>().HasMany(x => x.Rows).WithOne().HasForeignKey(x => x.ComponentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DecisionRow>().HasMany(x => x.Conditions).WithOne().HasForeignKey(x => x.DecisionRowId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PriceVersion>().HasIndex(x => new { x.PriceKey, x.Version }).IsUnique();
        b.Entity<Consent>().HasIndex(x => new { x.Institution, x.ModelId, x.EffectiveFrom }).IsUnique();
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
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        // Extra DB guard: even direct SQL cannot silently rewrite published history.
        string[] tables = ["ComponentVersions", "ComponentItem", "DecisionRow", "DecisionCondition",
            "ModelVersions", "ModelComponent", "Consents", "PriceVersions", "TaxRateVersions", "AgreementVersions",
            "CalculationRuns", "TransactionHeaders", "TransactionDetails"];
        foreach (var table in tables)
            foreach (var op in new[] { "UPDATE", "DELETE" })
            {
                // Identifiers come exclusively from the closed, hard-coded table/op lists above.
                var sql = $"CREATE TRIGGER IF NOT EXISTS immutable_{table}_{op} BEFORE {op} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Published history is immutable'); END;";
                await db.Database.ExecuteSqlRawAsync(sql);
            }
    }
}
