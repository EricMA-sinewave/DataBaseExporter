using System.Text;

namespace DataBaseExporter.Core.Database;

public sealed class SqlSafetyAnalyzer
{
    private static readonly HashSet<string> ReadOnlyLeadingKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select",
        "with",
        "show",
        "describe",
        "desc",
        "explain"
    };

    private static readonly HashSet<string> DangerousKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert",
        "update",
        "delete",
        "merge",
        "drop",
        "alter",
        "truncate",
        "create",
        "replace",
        "grant",
        "revoke",
        "vacuum",
        "analyze",
        "call",
        "exec",
        "execute"
    };

    public SqlSafetyResult Analyze(string sql, bool requireReadOnly, bool refuseMultipleStatements)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlSafetyResult.Rejected("SQL query cannot be empty.");
        }

        var normalized = StripCommentsAndStrings(sql);
        var statements = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (refuseMultipleStatements && statements.Length > 1)
        {
            return SqlSafetyResult.Rejected("Multiple SQL statements are refused by safety policy.");
        }

        var firstKeyword = ReadFirstKeyword(normalized);
        if (requireReadOnly && (firstKeyword is null || !ReadOnlyLeadingKeywords.Contains(firstKeyword)))
        {
            return SqlSafetyResult.Rejected($"SQL must start with a read-only keyword. Found '{firstKeyword ?? "<none>"}'.");
        }

        foreach (var token in ReadTokens(normalized))
        {
            if (DangerousKeywords.Contains(token))
            {
                return SqlSafetyResult.Rejected($"SQL contains refused keyword '{token}'.");
            }
        }

        return SqlSafetyResult.Accepted();
    }

    private static string? ReadFirstKeyword(string sql) => ReadTokens(sql).FirstOrDefault();

    private static IEnumerable<string> ReadTokens(string sql)
    {
        var builder = new StringBuilder();

        foreach (var ch in sql)
        {
            if (char.IsLetter(ch) || ch == '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string StripCommentsAndStrings(string sql)
    {
        var output = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var current = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (current == '-' && next == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] is not '\r' and not '\n')
                {
                    i++;
                }

                output.Append(' ');
                continue;
            }

            if (current == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                output.Append(' ');
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                var quote = current;
                output.Append(' ');
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote)
                        {
                            i++;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                output.Append(' ');
                continue;
            }

            output.Append(current);
        }

        return output.ToString();
    }
}

public readonly record struct SqlSafetyResult(bool IsAccepted, string? Reason)
{
    public static SqlSafetyResult Accepted() => new(true, null);

    public static SqlSafetyResult Rejected(string reason) => new(false, reason);
}
