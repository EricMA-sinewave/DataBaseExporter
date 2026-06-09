namespace DataBaseExporter.Core.Exporting;

public sealed class ExportRequest
{
    public string ConnectionName { get; init; } = "";

    public ExportScope Scope { get; init; } = ExportScope.Database;

    public string? Table { get; init; }

    public string? Schema { get; init; }

    public IReadOnlyList<ExportTableSelection> Tables { get; init; } = Array.Empty<ExportTableSelection>();

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
    Query
}

public sealed record ExportTableSelection(string? Schema, string Table);
