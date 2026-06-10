namespace DataBaseExporter.Core.Exporting;

public sealed class ExportRequest
{
    public string ConnectionName { get; init; } = "";

    public ExportScope Scope { get; init; } = ExportScope.Database;

    public string? Table { get; init; }

    public string? Schema { get; init; }

    public IReadOnlyList<ExportTableSelection> Tables { get; init; } = Array.Empty<ExportTableSelection>();

    public string? ItemKeyColumn { get; init; }

    public ItemExportProfile? ItemProfile { get; init; }

    public string? Sql { get; init; }

    public string OutputPath { get; init; } = "";

    public string? Format { get; init; }

    public bool IncludeSchema { get; init; } = true;

    public bool Overwrite { get; init; }

    public int? BatchSize { get; init; }

    public long? MaxRows { get; init; }
}

public enum ExportScope
{
    Database,
    Table,
    Tables,
    Items,
    Query
}

public sealed record ExportTableSelection(string? Schema, string Table);

public sealed class ItemExportProfile
{
    public string? RootSchema { get; init; }

    public string RootTable { get; init; } = "";

    public string RootKeyColumn { get; init; } = "";

    public Dictionary<string, string> TableKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ItemRelationship> Relationships { get; init; } = Array.Empty<ItemRelationship>();

    public int MaxDepth { get; init; } = 20;

    public int BatchSize { get; init; } = 100;

    public int QueryDelayMilliseconds { get; init; }
}

public sealed record ItemRelationship(
    string? FromSchema,
    string FromTable,
    string FromColumn,
    string? ToSchema,
    string ToTable,
    string ToColumn);
