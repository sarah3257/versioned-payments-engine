using Rules.Domain;

namespace Rules.Application.Retro;

public static class ResultComparer
{
    public static LineDifference[] Compare(EngineState state, CalculationResult next)
    {
        var differences = new List<LineDifference>();
        foreach (var month in next.Months)
        {
            var position = state.Positions.SingleOrDefault(x => x.PlacementId == next.Input.PlacementId && x.Month == month.Month);
            var before = position is null ? [] : state.Runs.Single(x => x.Result.RunId == position.RunId).Result.Months
                .Single(x => x.Month == month.Month).Segments.SelectMany(x => x.Lines).ToArray();
            var after = month.Segments.SelectMany(x => x.Lines).ToArray();
            var oldAmounts = before.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(v => v.Amount));
            var newAmounts = after.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(v => v.Amount));
            foreach (var key in oldAmounts.Keys.Union(newAmounts.Keys).Order())
            {
                var oldValue = oldAmounts.GetValueOrDefault(key); var newValue = newAmounts.GetValueOrDefault(key);
                differences.Add(new(next.Input.PlacementId, month.Month, key, oldValue, newValue, newValue - oldValue));
            }
        }
        return differences.ToArray();
    }
    public static void Apply(EngineState state, CalculationResult next)
    {
        var differences = Compare(state, next);
        foreach (var month in next.Months)
        {
            var position = state.Positions.SingleOrDefault(x => x.PlacementId == next.Input.PlacementId && x.Month == month.Month);
            var previous = position is null ? null : state.Runs.Single(x => x.Result.RunId == position.RunId).Result;
            var newLines = month.Segments.SelectMany(x => x.Lines).ToDictionary(x => x.Key);
            if (position?.Paid == true)
            {
                var oldLines = previous!.Months.Single(x => x.Month == month.Month).Segments.SelectMany(x => x.Lines).ToDictionary(x => x.Key);
                foreach (var delta in differences.Where(x => x.Month == month.Month && x.Delta != 0))
                {
                    var line = newLines.GetValueOrDefault(delta.Key) ?? oldLines[delta.Key];
                    var origin = state.Ledger.LastOrDefault(x => x.PlacementId == next.Input.PlacementId && x.Month == month.Month && x.Key == delta.Key);
                    state.Ledger.Add(new(Guid.NewGuid().ToString("N"), next.RunId, next.Input.PlacementId, month.Month, delta.Key, delta.Delta, "Adjustment", origin?.Id, line.TraceId));
                }
            }
            else
            {
                if (position is not null)
                {
                    // Pending originals remain immutable; append cancellation entries, never delete.
                    foreach (var old in state.Ledger.Where(x => x.RunId == position.RunId && x.Month == month.Month && x.Kind == "Pending").ToArray())
                        state.Ledger.Add(new(Guid.NewGuid().ToString("N"), next.RunId, next.Input.PlacementId, month.Month, old.Key, -old.Amount, "Canceled", old.Id, old.TraceId));
                }
                foreach (var line in newLines.Values)
                    state.Ledger.Add(new(Guid.NewGuid().ToString("N"), next.RunId, next.Input.PlacementId, month.Month, line.Key, line.Amount, "Pending", null, line.TraceId));
            }
            if (position is not null) state.Positions.Remove(position);
            state.Positions.Add(new(next.Input.PlacementId, month.Month, next.RunId, position?.Paid ?? false));
        }
    }
}
