using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.Services;

public class PublicationService(EngineDb db)
{
    public static readonly string[] Attributes = ["institutionType", "supportLevel", "institution", "ownership", "administration", "population"];
    public static readonly string[] Operators = ["=", "!=", "<", ">", "<=", ">=", "BETWEEN"];
    static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool test, string message) { if (!test) throw new BusinessException(message); }
    static void Dates(DateOnly from, DateOnly to) => Require(from != default && from <= to, "טווח תאריכים אינו תקין");

    public async Task<ComponentVersion> PublishComponentAsync(PublishComponent p)
    {
        Dates(p.From, p.To);
        Require(p.ComponentId > 0 && p.Version > 0 && !string.IsNullOrWhiteSpace(p.Name), "נדרשים מזהה, גרסה ושם רכיב");
        Require(Enum.IsDefined(p.Kind) && p.TemplateVersion == "v1", "סוג רכיב או תבנית חישוב לא נתמכים");
        Require(p.Items != null && p.Rows != null, "נדרשות רשימות items ו־rows (גם אם ריקות)");
        if (await db.ComponentVersions.AnyAsync(x => x.ComponentId == p.ComponentId && x.Version == p.Version))
            throw new BusinessException("גרסת רכיב כבר פורסמה; יש לפרסם מספר גרסה חדש", 409);
        var older = await db.ComponentVersions.Where(x => x.ComponentId == p.ComponentId).ToListAsync();
        Require(older.All(x => x.Kind == p.Kind), "שינוי סוג רכיב מחייב ComponentId חדש");
        Require(older.All(x => x.Version < p.Version), "מספר גרסה חדש חייב להיות גבוה מכל הקודמות");
        Require(p.ProfitPercent >= 0 && p.ProfitPercent <= 100, "אחוז רווח מחוץ לטווח 0–100");
        Require(p.Items.All(i => !string.IsNullOrWhiteSpace(i.PriceKey) && i.Factor >= 0 && Enum.IsDefined(i.TaxLiability)), "פריט רכיב אינו תקין");
        Require(p.Items.Select(i => i.PriceKey).Distinct().Count() == p.Items.Count, "אין לשכפל פריט בתוך רכיב");
        Require(p.Kind is not (ComponentKind.Maintenance or ComponentKind.Staff) || p.Items.Count > 0, "רכיב אחזקה או כוח אדם דורש מפרט פריטים");
        Require(p.Kind == ComponentKind.Basket || p.Rows.Count == 0, "שורות החלטה מותרות רק ברכיב סל");
        Require(p.Kind is ComponentKind.Maintenance or ComponentKind.Staff || p.Items.Count == 0, "פריטים מותרים רק באחזקה או בכוח אדם");
        Require(p.Rows.Select(r => r.RowKey).Distinct().Count() == p.Rows.Count, "מפתח שורת החלטה כפול");
        foreach (var row in p.Rows)
        {
            Require(!string.IsNullOrWhiteSpace(row.RowKey) && row.ResultAmount >= 0, "שורת החלטה אינה תקינה");
            Require(row.ResultAmount != 0 || !string.IsNullOrWhiteSpace(row.PriceKey), "סכום סל אפס מחייב פריט מחירון");
            Require(row.Conditions != null, "חסרה רשימת תנאים");
            foreach (var c in row.Conditions)
            {
                Require(Attributes.Contains(c.Attribute) && Operators.Contains(c.Operator), "מאפיין או אופרטור לא מוכר בטבלת החלטה");
                if (c.Value != null && c.Operator != "=" && c.Operator != "!=")
                {
                    Require(decimal.TryParse(c.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _), "השוואת סדר דורשת ערך מספרי");
                    Require(c.Attribute is "supportLevel" or "institution", "השוואת סדר נתמכת במאפיינים מספריים בלבד");
                    if (c.Operator == "BETWEEN")
                        Require(decimal.TryParse(c.ValueTo, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var upper) && decimal.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture) <= upper, "טווח תנאי לא תקין");
                }
            }
        }
        // Verify structural references now; effective price coverage is checked at execution.
        foreach (var item in p.Items)
            Require(await db.PriceVersions.AnyAsync(x => x.PriceKey == item.PriceKey && x.Kind == p.Kind), $"פריט מחירון לא מוכר לסוג הרכיב: {item.PriceKey}");
        foreach (var row in p.Rows.Where(r => r.ResultAmount == 0))
            Require(await db.PriceVersions.AnyAsync(x => x.PriceKey == row.PriceKey), $"פריט מחירון סל לא מוכר: {row.PriceKey}");
        var entity = new ComponentVersion
        {
            ComponentId = p.ComponentId, Version = p.Version, Name = p.Name, Kind = p.Kind,
            From = p.From, To = p.To, TemplateVersion = p.TemplateVersion,
            SupportLevel = p.SupportLevel, InstitutionType = p.InstitutionType,
            Administration = p.Administration, Population = p.Population,
            ProfitPercent = p.ProfitPercent, UseAgreement = p.UseAgreement,
            ProfitTaxLiability = p.ProfitTaxLiability, PublishedAtUtc = DateTime.UtcNow,
            Items = p.Items.Select(i => new ComponentItem { PriceKey = i.PriceKey, Factor = i.Factor, ProfitRequired = i.ProfitRequired, TaxLiability = i.TaxLiability }).ToList(),
            Rows = p.Rows.Select(r => new DecisionRow { RowKey = r.RowKey, Description = r.Description,
                ResultAmount = r.ResultAmount, PriceKey = r.PriceKey, TaxLiability = r.TaxLiability,
                Conditions = r.Conditions.Select(c => new DecisionCondition { Attribute = c.Attribute, Operator = c.Operator, Value = c.Value, ValueTo = c.ValueTo }).ToList() }).ToList()
        };
        db.ComponentVersions.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task<ModelVersion> PublishModelAsync(PublishModel p)
    {
        Dates(p.From, p.To);
        Require(p.ModelId > 0 && p.Version > 0 && !string.IsNullOrWhiteSpace(p.Name) && p.Components is { Count: > 0 }, "הגדרת מודל חסרה");
        var previous = await db.ModelVersions.Where(x => x.ModelId == p.ModelId).ToListAsync();
        Require(previous.All(x => x.Version < p.Version), "מספר גרסת מודל חייב להיות חדש וגבוה");
        Require(p.Components.Select(x => x.ComponentId).Distinct().Count() == p.Components.Count, "רכיב מופיע פעמיים במודל");
        Require(p.Components.Select(x => (x.Layer, x.Order)).Distinct().Count() == p.Components.Count, "סדר כפול באותה שכבה");
        foreach (var link in p.Components)
        {
            Require(link.Layer is >= 1 and <= 7 && link.Order >= 1, "שכבה או סדר לא תקינים");
            var versions = await db.ComponentVersions.Where(x => x.ComponentId == link.ComponentId).ToListAsync();
            Require(versions.Count > 0, $"רכיב לא קיים: {link.ComponentId}");
            var kind = versions[0].Kind;
            Require(kind != ComponentKind.Profit || link.Layer == 6, "רווח חייב להיות בשכבה 6");
            Require(kind != ComponentKind.Tax || link.Layer == 7, "מס חייב להיות בשכבה 7");
            Require(kind is ComponentKind.Profit or ComponentKind.Tax || link.Layer <= 5, "רכיב בסיס חייב להקדים רווח ומס");
            Require(Covers(versions.Select(v => (v.From, v.To)), p.From, p.To), $"אין כיסוי תוקף מלא לרכיב {link.ComponentId}");
        }
        var entity = new ModelVersion { ModelId = p.ModelId, Version = p.Version, Name = p.Name, From = p.From, To = p.To,
            PublishedAtUtc = DateTime.UtcNow, Components = p.Components.Select(c => new ModelComponent { ComponentId = c.ComponentId, Layer = c.Layer, Order = c.Order }).ToList() };
        db.ModelVersions.Add(entity); await db.SaveChangesAsync(); return entity;
    }

    public async Task<Consent> PublishConsentAsync(PublishConsent p)
    {
        var model = await db.ModelVersions.SingleOrDefaultAsync(x => x.ModelId == p.ModelId && x.Version == p.ModelVersion);
        Require(model != null && p.Institution > 0 && p.EffectiveFrom >= model.From && p.EffectiveFrom <= model.To, "הסכמה חייבת להפנות לגרסת מודל קיימת ובתוקף");
        var entity = new Consent { Institution = p.Institution, ModelId = p.ModelId, ModelVersion = p.ModelVersion, EffectiveFrom = p.EffectiveFrom, RecordedAtUtc = DateTime.UtcNow };
        db.Consents.Add(entity); await db.SaveChangesAsync(); return entity;
    }
    public async Task<PriceVersion> PublishPriceAsync(PublishPrice p)
    {
        Dates(p.From, p.To);
        Require(!string.IsNullOrWhiteSpace(p.PriceKey) && p.Version > 0 && p.Amount >= 0 && p.Kind is ComponentKind.Maintenance or ComponentKind.Staff, "מחירון לא תקין");
        var previous = await db.PriceVersions.Where(x => x.PriceKey == p.PriceKey).ToListAsync();
        Require(previous.All(x => x.Version < p.Version && x.Kind == p.Kind), "גרסת מחירון חייבת להיות גבוהה ולשמור על סוג הפריט");
        var e = new PriceVersion { PriceKey = p.PriceKey, Version = p.Version, Description = p.Description, Kind = p.Kind, From = p.From, To = p.To, Amount = p.Amount };
        db.PriceVersions.Add(e); await db.SaveChangesAsync(); return e;
    }
    public async Task<AgreementVersion> PublishAgreementAsync(PublishAgreement p)
    {
        Dates(p.From, p.To); Require(p.Institution > 0 && p.Version > 0 && p.ProfitPercent is >= 0 and <= 100, "הסכם לא תקין");
        var all = await db.AgreementVersions.Where(x => x.Institution == p.Institution).ToListAsync();
        Require(all.All(x => x.Version < p.Version), "גרסת הסכם חייבת להיות גבוהה");
        var e = new AgreementVersion { Institution = p.Institution, Version = p.Version, From = p.From, To = p.To, ProfitPercent = p.ProfitPercent };
        db.AgreementVersions.Add(e); await db.SaveChangesAsync(); return e;
    }
    public async Task<TaxRateVersion> PublishTaxAsync(PublishTax p)
    {
        Dates(p.From, p.To); Require(p.Version > 0 && p.Percent is >= 0 and <= 100 && Enum.IsDefined(p.Ownership) && Enum.IsDefined(p.SourceKind) && !string.IsNullOrWhiteSpace(p.TaxName), "שיעור מס לא תקין");
        var all = await db.TaxRateVersions.Where(x => x.Ownership == p.Ownership && x.SourceKind == p.SourceKind).ToListAsync();
        Require(all.All(x => x.Version < p.Version), "גרסת מס חייבת להיות גבוהה");
        var e = new TaxRateVersion { Ownership = p.Ownership, SourceKind = p.SourceKind, Version = p.Version, TaxName = p.TaxName, From = p.From, To = p.To, Percent = p.Percent };
        db.TaxRateVersions.Add(e); await db.SaveChangesAsync(); return e;
    }
    public static bool Covers(IEnumerable<(DateOnly From, DateOnly To)> periods, DateOnly from, DateOnly to)
    {
        int next = from.DayNumber;
        foreach (var p in periods.OrderBy(x => x.From))
        {
            if (p.To.DayNumber < next) continue;
            if (p.From.DayNumber > next) return false;
            next = Math.Max(next, p.To.DayNumber + 1);
            if (next > to.DayNumber) return true;
        }
        return next > to.DayNumber;
    }
}
