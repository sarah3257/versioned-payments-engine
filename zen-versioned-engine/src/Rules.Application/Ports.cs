using System.Text.Json.Nodes;
using Rules.Domain;

namespace Rules.Application;

public interface IRuleRuntime
{
    string Version { get; }
    void ValidateExpression(string expression);
    JsonNode? Evaluate(string expression, JsonObject context);
    Task<(JsonArray Rows, JsonNode? Trace)> EvaluateTable(DecisionTable table, JsonObject facts);
}
public sealed record StateSnapshot(long Revision, EngineState State);
public interface IEngineStore
{
    StateSnapshot Read();
    bool TryCommit(long expectedRevision, EngineState state);
}
