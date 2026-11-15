using System.Text.RegularExpressions;
using Rules.Domain;

namespace Rules.Application.Rules;

// Deliberately exposes only pure expressions, never JavaScript nodes or user callbacks.
public static partial class ExpressionPolicy
{
    private static readonly HashSet<string> Functions = ["filter", "map", "flatMap", "sum", "min", "max", "avg", "len", "count", "some", "all", "one", "none", "contains", "abs", "round", "floor", "ceil", "trunc", "keys", "values", "merge", "string", "upper", "lower", "trim", "startsWith", "endsWith"];
    public static void Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 8000)
            throw new BusinessException("EXPRESSION_SIZE", "ביטוי ריק או ארוך מדי");
        var stripped = Strings().Replace(expression, "''");
        if (stripped.IndexOfAny([';', '`', '\\']) >= 0 || Regex.IsMatch(stripped, @"(?<![=!<>])=(?!=)|\b(function|import|export|while|for|eval|rand|random|fetch|http)\b"))
            throw new BusinessException("EXPRESSION_FORBIDDEN", "הביטוי מכיל פעולה שאינה מורשית");
        foreach (Match match in Calls().Matches(stripped))
            if (!Functions.Contains(match.Groups[1].Value) && match.Groups[1].Value is not ("and" or "or" or "not" or "in"))
                throw new BusinessException("EXPRESSION_FUNCTION", $"פונקציה אינה מורשית: {match.Groups[1].Value}");
        var depth = 0;
        foreach (var ch in stripped)
        {
            if (ch is '(' or '[' or '{') depth++;
            if (ch is ')' or ']' or '}') depth--;
            if (depth > 24 || depth < 0) throw new BusinessException("EXPRESSION_DEPTH", "עומק ביטוי לא תקין");
        }
        if (depth != 0) throw new BusinessException("EXPRESSION_SYNTAX", "סוגריים לא מאוזנים");
    }
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'")]
    private static partial Regex Strings();
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex Calls();
}
