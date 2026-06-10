using System.Data;
using System.Data.Common;
using DataBaseExporter.Core.Configuration;

namespace DataBaseExporter.Core.Database;

public sealed class SchemaReader
{
    public async Task<IReadOnlyList<DatabaseTablePreview>> PreviewAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        IdentifierQuoter quoter,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (options.Engine == DatabaseEngine.MySql)
        {
            return await PreviewMySqlAsync(connection, options, commandTimeoutSeconds, cancellationToken);
        }

        var tables = await GetTablesAsync(connection, options, cancellationToken);
        var previews = new List<DatabaseTablePreview>(tables.Count);

        foreach (var table in tables)
        {
            var columns = await GetColumnsAsync(connection, quoter, table, commandTimeoutSeconds, cancellationToken);
            var rowCount = await TryGetRowCountAsync(connection, quoter, table, commandTimeoutSeconds, cancellationToken);
            previews.Add(new DatabaseTablePreview(table, columns, rowCount));
        }

        return previews;
    }

    public async Task<IReadOnlyList<DatabaseTable>> GetTablesAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.Engine == DatabaseEngine.MySql)
        {
            return await GetMySqlTablesAsync(connection, options, cancellationToken);
        }

        var schemaTable = await connection.GetSchemaAsync("Tables", cancellationToken);
        var result = new List<DatabaseTable>();

        foreach (DataRow row in schemaTable.Rows)
        {
            var tableType = GetString(row, "TABLE_TYPE");
            if (!string.IsNullOrWhiteSpace(tableType)
                && !tableType.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
                && !tableType.Contains("BASE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = GetString(row, "TABLE_NAME");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(new DatabaseTable(GetString(row, "TABLE_SCHEMA"), name));
        }

        return result
            .Distinct()
            .OrderBy(x => x.Schema)
            .ThenBy(x => x.Name)
            .ToArray();
    }

    private static async Task<IReadOnlyList<DatabaseTablePreview>> PreviewMySqlAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var tables = await GetMySqlTableRowsAsync(connection, options, commandTimeoutSeconds, cancellationToken);
        var previews = new List<DatabaseTablePreview>(tables.Count);

        foreach (var pair in tables)
        {
            var columns = await GetMySqlColumnsAsync(connection, pair.Table, commandTimeoutSeconds, cancellationToken);
            previews.Add(new DatabaseTablePreview(pair.Table, columns, pair.RowCount));
        }

        return previews;
    }

    private static async Task<IReadOnlyList<DatabaseTable>> GetMySqlTablesAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var rows = await GetMySqlTableRowsAsync(connection, options, commandTimeoutSeconds: 60, cancellationToken);
        return rows.Select(x => x.Table).ToArray();
    }

    private static async Task<IReadOnlyList<MySqlTableRow>> GetMySqlTableRowsAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_ROWS
FROM information_schema.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
  AND TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
  AND (@database IS NULL OR TABLE_SCHEMA = @database)
ORDER BY TABLE_SCHEMA, TABLE_NAME
""";

        var databaseName = string.IsNullOrWhiteSpace(options.Endpoint?.Database) ? connection.Database : options.Endpoint.Database;
        AddParameter(command, "@database", string.IsNullOrWhiteSpace(databaseName) ? DBNull.Value : databaseName);

        var result = new List<MySqlTableRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            long? rowCount = await reader.IsDBNullAsync(2, cancellationToken) ? null : Convert.ToInt64(reader.GetValue(2));
            result.Add(new MySqlTableRow(new DatabaseTable(schema, name), rowCount));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DatabaseColumn>> GetMySqlColumnsAsync(
        DbConnection connection,
        DatabaseTable table,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = @schema
  AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION
""";

        AddParameter(command, "@schema", table.Schema ?? "");
        AddParameter(command, "@table", table.Name);

        var columns = new List<DatabaseColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
            long? size = null;
            if (!await reader.IsDBNullAsync(3, cancellationToken))
            {
                size = Convert.ToInt64(reader.GetValue(3));
            }
            else if (!await reader.IsDBNullAsync(4, cancellationToken))
            {
                size = Convert.ToInt64(reader.GetValue(4));
            }

            columns.Add(new DatabaseColumn(name, dataType, isNullable, size));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseTable table,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {quoter.QuoteTable(table)} WHERE 1 = 0";
        command.CommandTimeout = commandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
        var schema = await reader.GetColumnSchemaAsync(cancellationToken);

        return schema
            .Select(column => new DatabaseColumn(
                column.ColumnName ?? "",
                column.DataTypeName ?? column.DataType?.Name ?? "unknown",
                column.AllowDBNull,
                column.ColumnSize))
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .ToArray();
    }

    private static async Task<long?> TryGetRowCountAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseTable table,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {quoter.QuoteTable(table)}";
            command.CommandTimeout = commandTimeoutSeconds;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(DataRow row, string columnName)
    {
        return row.Table.Columns.Contains(columnName) && row[columnName] is not DBNull
            ? Convert.ToString(row[columnName])
            : null;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record MySqlTableRow(DatabaseTable Table, long? RowCount);
}

public readonly record struct DatabaseTable(string? Schema, string Name)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Schema) ? Name : $"{Schema}.{Name}";
}

public sealed record DatabaseColumn(string Name, string DataType, bool? IsNullable, long? Size);

public sealed record DatabaseTablePreview(DatabaseTable Table, IReadOnlyList<DatabaseColumn> Columns, long? RowCount);
