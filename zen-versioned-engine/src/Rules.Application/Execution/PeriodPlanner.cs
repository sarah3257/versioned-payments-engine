using Rules.Domain;

namespace Rules.Application.Execution;

public sealed record PeriodSegment(DateOnly Month, DateOnly From, DateOnly To, decimal Factor);
public static class PeriodPlanner
{
    public static PeriodSegment[] Plan(EngineState state, CalculationRequest request)
    {
        if (request.From > request.To || request.To.DayNumber - request.From.DayNumber > 366 * 5)
            throw new BusinessException("PERIOD", "תקופת החישוב אינה תקינה או עולה על חמש שנים");
        var points = new SortedSet<DateOnly> { request.From, request.To.AddDays(1) };
        void Add(DateOnly from, DateOnly to) { if (from > request.From && from <= request.To) points.Add(from); if (to < request.To && to >= request.From) points.Add(to.AddDays(1)); }
        foreach (var p in request.Periods) Add(p.From, p.To);
        foreach (var p in state.Models.Where(x => x.Id == request.ModelId)) Add(p.From, p.To);
        var ids = state.Models.Where(x => x.Id == request.ModelId).SelectMany(x => x.Components).Select(x => x.ComponentId).ToHashSet();
        foreach (var p in state.Components.Where(x => ids.Contains(x.Id))) Add(p.From, p.To);
        foreach (var p in state.References) Add(p.From, p.To);
        foreach (var p in state.Agreements.Where(x => x.ModelId == request.ModelId && x.InstitutionId == request.InstitutionId)) Add(p.From, p.To);
        for (var month = new DateOnly(request.From.Year, request.From.Month, 1).AddMonths(1); month <= request.To; month = month.AddMonths(1)) points.Add(month);
        var dates = points.ToArray();
        return dates.Zip(dates.Skip(1), (from, until) => new PeriodSegment(new(from.Year, from.Month, 1), from, until.AddDays(-1),
            (decimal)(until.DayNumber - from.DayNumber) / DateTime.DaysInMonth(from.Year, from.Month))).ToArray();
    }
}
