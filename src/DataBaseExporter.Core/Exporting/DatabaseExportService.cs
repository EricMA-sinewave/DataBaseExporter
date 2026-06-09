using System.Data;
using System.Data.Common;
using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Formats;

namespace DataBaseExporter.Core.Exporting;

public sealed class DatabaseExportService
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ExportFormatRegistry _formatRegistry;
    private readonly SchemaReader _schemaReader;
    private readonly SqlSafetyAnalyzer _sqlSafetyAnalyzer;

    public DatabaseExportService(
        IDatabaseConnectionFactory connectionFactory,
        ExportFormatRegistry formatRegistry,
        SchemaReader schemaReader,
        SqlSafetyAnalyzer sqlSafetyAnalyzer)
    {
        _connectionFactory = connectionFactory;
        _formatRegistry = formatRegistry;
        _schemaReader = schemaReader;
        _sqlSafetyAnalyzer = sqlSafetyAnalyzer;
    }

    public static DatabaseExportService CreateDefault()
    {
        return new DatabaseExportService(
            new DatabaseConnectionFactory(),
            ExportFormatRegistry.CreateDefault(),
            new SchemaReader(),
            new SqlSafetyAnalyzer());
    }

    public async Task<ExportSummary> ExportAsync(
        DatabaseExportConfiguration configuration,
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var connectionOptions = configuration.GetConnection(request.ConnectionName);
        var format = request.Format ?? configuration.Defaults.Format;
        var writer = _formatRegistry.GetRequired(format);
        var outputPath = EnsureExtension(request.OutputPath, writer.FileExtension);
        var maxRows = request.MaxRows ?? configuration.Defaults.Safety.DefaultMaxRows;
        var batchSize = request.BatchSize ?? configuration.Defaults.BatchSize;

        if (batchSize <= 0)
        {
            throw new InvalidOperationException("Batch size must be greater than zero.");
        }

        if (File.Exists(outputPath) && !request.Overwrite)
        {
            throw new IOException($"Output file '{outputPath}' already exists. Use --overwrite to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

        await using var connection = await _connectionFactory.OpenAsync(connectionOptions, cancellationToken);
        var quoter = new IdentifierQuoter(connectionOptions);
        await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await using var session = writer.CreateSession(file, new ExportWriterOptions(request.IncludeSchema));

        var metadata = new ExportMetadata(
            request.ConnectionName,
            request.Scope,
            request.Scope switch
            {
                ExportScope.Database => "database",
                ExportScope.Table => new DatabaseTable(request.Schema, request.Table!).ToString(),
                ExportScope.Tables => $"{request.Tables.Count} tables",
                ExportScope.Query => "query",
                _ => null
            },
            DateTimeOffset.UtcNow,
            request.IncludeSchema);

        await session.BeginAsync(metadata, cancellationToken);

        var resultSets = request.Scope switch
        {
            ExportScope.Database => await ExportDatabaseAsync(connection, quoter, connectionOptions, session, maxRows, batchSize, cancellationToken),
            ExportScope.Table => await ExportTableAsync(connection, quoter, connectionOptions, request, session, maxRows, batchSize, cancellationToken),
            ExportScope.Tables => await ExportTablesAsync(connection, quoter, connectionOptions, request, session, maxRows, batchSize, cancellationToken),
            ExportScope.Query => await ExportQueryAsync(connection, connectionOptions, configuration.Defaults.Safety, request, session, maxRows, batchSize, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown export scope '{request.Scope}'.")
        };

        await session.CompleteAsync(cancellationToken);
        return new ExportSummary(outputPath, format, resultSets.Sum(x => x.RowCount), resultSets);
    }

    public async Task<IReadOnlyList<DatabaseTablePreview>> PreviewAsync(
        DatabaseExportConfiguration configuration,
        string connectionName,
        CancellationToken cancellationToken = default)
    {
        var connectionOptions = configuration.GetConnection(connectionName);
        await using var connection = await _connectionFactory.OpenAsync(connectionOptions, cancellationToken);
        return await _schemaReader.PreviewAsync(
            connection,
            connectionOptions,
            new IdentifierQuoter(connectionOptions),
            connectionOptions.CommandTimeoutSeconds,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ResultSetSummary>> ExportDatabaseAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseConnectionOptions options,
        IExportSession session,
        long? maxRows,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var tables = await _schemaReader.GetTablesAsync(connection, options, cancellationToken);
        var summaries = new List<ResultSetSummary>(tables.Count);

        foreach (var table in tables)
        {
            var sql = $"SELECT * FROM {quoter.QuoteTable(table)}";
            summaries.Add(await StreamQueryAsync(connection, table.ToString(), sql, options.CommandTimeoutSeconds, session, maxRows, batchSize, cancellationToken));
        }

        return summaries;
    }

    private static async Task<IReadOnlyList<ResultSetSummary>> ExportTableAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseConnectionOptions options,
        ExportRequest request,
        IExportSession session,
        long? maxRows,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var table = new DatabaseTable(request.Schema, request.Table!);
        var sql = $"SELECT * FROM {quoter.QuoteTable(table)}";
        var summary = await StreamQueryAsync(connection, table.ToString(), sql, options.CommandTimeoutSeconds, session, maxRows, batchSize, cancellationToken);
        return new[] { summary };
    }

    private static async Task<IReadOnlyList<ResultSetSummary>> ExportTablesAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseConnectionOptions options,
        ExportRequest request,
        IExportSession session,
        long? maxRows,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var summaries = new List<ResultSetSummary>(request.Tables.Count);
        foreach (var selection in request.Tables)
        {
            var table = new DatabaseTable(selection.Schema, selection.Table);
            var sql = $"SELECT * FROM {quoter.QuoteTable(table)}";
            summaries.Add(await StreamQueryAsync(connection, table.ToString(), sql, options.CommandTimeoutSeconds, session, maxRows, batchSize, cancellationToken));
        }

        return summaries;
    }

    private async Task<IReadOnlyList<ResultSetSummary>> ExportQueryAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        ExportSafetyOptions safety,
        ExportRequest request,
        IExportSession session,
        long? maxRows,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var analysis = _sqlSafetyAnalyzer.Analyze(
            request.Sql!,
            options.ReadOnlyMode && safety.RequireReadOnlySql && !safety.AllowNonQueryOperations,
            safety.RefuseMultipleStatements);

        if (!analysis.IsAccepted)
        {
            throw new InvalidOperationException($"SQL query was refused: {analysis.Reason}");
        }

        var summary = await StreamQueryAsync(connection, "query", request.Sql!, options.CommandTimeoutSeconds, session, maxRows, batchSize, cancellationToken);
        return new[] { summary };
    }

    private static async Task<ResultSetSummary> StreamQueryAsync(
        DbConnection connection,
        string resultSetName,
        string sql,
        int commandTimeoutSeconds,
        IExportSession session,
        long? maxRows,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
        var columns = await ReadColumnsAsync(reader, cancellationToken);
        var info = new ResultSetInfo(resultSetName, columns, sql);

        await session.BeginResultSetAsync(info, cancellationToken);

        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (maxRows is not null && rows >= maxRows.Value)
            {
                break;
            }

            var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i].Name] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            await session.WriteRowAsync(row, cancellationToken);
            rows++;

            if (rows % batchSize == 0)
            {
                await session.FlushAsync(cancellationToken);
            }
        }

        await session.EndResultSetAsync(rows, cancellationToken);
        return new ResultSetSummary(resultSetName, rows);
    }

    private static async Task<IReadOnlyList<DatabaseColumn>> ReadColumnsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            var schema = await reader.GetColumnSchemaAsync(cancellationToken);
            if (schema.Count > 0)
            {
                return schema
                    .Select((column, index) => new DatabaseColumn(
                        string.IsNullOrWhiteSpace(column.ColumnName) ? $"Column{index + 1}" : column.ColumnName,
                        column.DataTypeName ?? column.DataType?.Name ?? "unknown",
                        column.AllowDBNull,
                        column.ColumnSize))
                    .ToArray();
            }
        }
        catch (NotSupportedException)
        {
            // Some providers do not implement GetColumnSchemaAsync; FieldCount metadata is enough for export headers.
        }

        return Enumerable.Range(0, reader.FieldCount)
            .Select(i => new DatabaseColumn(reader.GetName(i), reader.GetFieldType(i).Name, null, null))
            .ToArray();
    }

    private static string EnsureExtension(string outputPath, string extension)
    {
        return Path.HasExtension(outputPath)
            ? outputPath
            : outputPath + extension;
    }

    private static void ValidateRequest(ExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionName))
        {
            throw new InvalidOperationException("Connection name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new InvalidOperationException("Output path is required.");
        }

        if (request.Scope == ExportScope.Table && string.IsNullOrWhiteSpace(request.Table))
        {
            throw new InvalidOperationException("Table export requires a table name.");
        }

        if (request.Scope == ExportScope.Tables && request.Tables.Count == 0)
        {
            throw new InvalidOperationException("Multi-table export requires at least one selected table.");
        }

        if (request.Scope == ExportScope.Tables && request.Tables.Any(x => string.IsNullOrWhiteSpace(x.Table)))
        {
            throw new InvalidOperationException("Multi-table export contains an empty table name.");
        }

        if (request.Scope == ExportScope.Query && string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new InvalidOperationException("Query export requires SQL text.");
        }
    }
}

public sealed record ExportSummary(string OutputPath, string Format, long TotalRows, IReadOnlyList<ResultSetSummary> ResultSets);

public sealed record ResultSetSummary(string Name, long RowCount);
