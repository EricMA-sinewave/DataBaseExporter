using DataBaseExporter.Core.Database;

namespace DataBaseExporter.Core.Exporting;

public sealed record ExportMetadata(
    string ConnectionName,
    ExportScope Scope,
    string? Source,
    DateTimeOffset GeneratedAtUtc,
    bool IncludeSchema);

public sealed record ResultSetInfo(
    string Name,
    IReadOnlyList<DatabaseColumn> Columns,
    string? SourceSql);
