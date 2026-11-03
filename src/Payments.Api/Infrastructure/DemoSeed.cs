using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Services;

namespace Payments.Api.Infrastructure;

public static class DemoSeed
{
    // Deliberately small, labelled subset of the Word model, not a silent import of inconsistent source data.
    public static async Task InitializeAsync(EngineDb db)
    {
        if (await db.ModelVersions.AnyAsync()) return;
        await using var seedTransaction = await db.Database.BeginTransactionAsync();
        var p = new PublicationService(db);
        var from = new DateOnly(2026, 1, 1); var to = new DateOnly(2027, 12, 31);
        await p.PublishPriceAsync(new("clothes", 1, "ביגוד", ComponentKind.Maintenance, from, to, 94));
        await p.PublishPriceAsync(new("municipal", 1, "מיסים — פריט אחזקה", ComponentKind.Maintenance, from, to, 187));
        await p.PublishPriceAsync(new("doctor", 1, "רופא — מחיר בסיס לדוגמה", ComponentKind.Staff, from, to, 18000));
        await p.PublishPriceAsync(new("doctor", 2, "רופא", ComponentKind.Staff, new(2026, 9, 1), to, 18500));
        foreach (var owner in Enum.GetValues<Ownership>())
        {
            await p.PublishTaxAsync(new(owner, ComponentKind.Maintenance, 1, "מע״מ", from, to, 18));
            await p.PublishTaxAsync(new(owner, ComponentKind.Basket, 1, "מע״מ", from, to, 18));
            await p.PublishTaxAsync(new(owner, ComponentKind.Profit, 1, "מע״מ", from, to, 18));
            await p.PublishTaxAsync(new(owner, ComponentKind.Staff, 1, owner == Ownership.Public ? "מס מעסיקים" : "מע״מ", from, to, owner == Ownership.Public ? 7.5m : 18));
        }
        await p.PublishAgreementAsync(new(12380, 1, from, to, 3.5m));
        List<PublishItem> Maintenance(decimal clothes, decimal municipal) =>
            [new("clothes", clothes, true, TaxLiability.Both), new("municipal", municipal, true, TaxLiability.Both)];
        await p.PublishComponentAsync(new(1, 1, "אחזקה", ComponentKind.Maintenance, from, to, Maintenance(1, 1), []));
        await p.PublishComponentAsync(new(1, 2, "אחזקה — מיסים 90%", ComponentKind.Maintenance, new(2026, 9, 1), to, Maintenance(1, .9m), []));
        await p.PublishComponentAsync(new(1, 3, "אחזקה — תיקון רטרואקטיבי ביגוד 110%", ComponentKind.Maintenance, from, new(2026, 6, 30), Maintenance(1.1m, 1), []));
        await p.PublishComponentAsync(new(3, 1, "כוח אדם — תמיכה 4", ComponentKind.Staff, from, to, [new("doctor", .1m, true, TaxLiability.Both)], [], SupportLevel: 4));
        await p.PublishComponentAsync(new(4, 1, "כוח אדם — תמיכה 5", ComponentKind.Staff, from, to, [new("doctor", .2m, true, TaxLiability.Both)], [], SupportLevel: 5));
        await p.PublishComponentAsync(new(12, 1, "כוח אדם מתוקנן — תמיכה 4", ComponentKind.Staff, new(2026, 7, 1), to, [new("doctor", .15m, true, TaxLiability.Both)], [], SupportLevel: 4));
        await p.PublishComponentAsync(new(7, 1, "סל לפי כללים", ComponentKind.Basket, from, to, [],
            [new("medical", "צרכים רפואיים מיוחדים", 1345, null, TaxLiability.None, [new("institutionType", "=", "הוסטל")])]));
        await p.PublishComponentAsync(new(10, 1, "רווח קבוע 4% — הדגמה", ComponentKind.Profit, from, to, [], [], ProfitPercent: 4));
        await p.PublishComponentAsync(new(11, 1, "מיסוי", ComponentKind.Tax, from, to, [], []));
        List<PublishLink> Links(int staff) => [new(1, 1, 1), new(staff, 1, 2), new(4, 1, 3), new(7, 2, 1), new(10, 6, 1), new(11, 7, 1)];
        await p.PublishModelAsync(new(1, 1, "מודל דיור — תת־קבוצה לימודית", from, to, Links(3)));
        await p.PublishModelAsync(new(1, 2, "מודל דיור — תקן רופא חדש", new(2026, 7, 1), to, Links(12)));
        await p.PublishConsentAsync(new(12380, 1, 2, new(2026, 7, 1)));
        await p.PublishConsentAsync(new(3404, 1, 2, new(2026, 10, 1)));
        await seedTransaction.CommitAsync();
    }
}
