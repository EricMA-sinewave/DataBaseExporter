using System.Data;
using System.Data.Common;
using System.Text;
using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Formats;

namespace DataBaseExporter.Core.Exporting;

public sealed class DatabaseExportService
{
    private const string ExplicitBase64Prefix = "BASE64__";
    private const int MaxItemBatchSize = 1000;

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
        var outputPath = request.Scope == ExportScope.Items
            ? request.OutputPath
            : EnsureExtension(request.OutputPath, writer.FileExtension);
        var maxRows = NormalizeMaxRows(request.MaxRows ?? configuration.Defaults.Safety.DefaultMaxRows);
        var batchSize = request.BatchSize ?? configuration.Defaults.BatchSize;

        if (batchSize <= 0)
        {
            throw new InvalidOperationException("Batch size must be greater than zero.");
        }

        if (request.Scope == ExportScope.Items)
        {
            Directory.CreateDirectory(outputPath);
            await using var itemConnection = await _connectionFactory.OpenAsync(connectionOptions, cancellationToken);
            return await ExportItemsAsync(
                itemConnection,
                connectionOptions,
                request,
                writer,
                outputPath,
                maxRows,
                batchSize,
                request.Overwrite,
                cancellationToken);
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
                ExportScope.Items => $"{request.Table}.{request.ItemKeyColumn}",
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

    private async Task<ExportSummary> ExportItemsAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        ExportRequest request,
        IExportFormatWriter writer,
        string outputDirectory,
        long? maxItems,
        int configuredBatchSize,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var quoter = new IdentifierQuoter(options);
        var profile = request.ItemProfile ?? new ItemExportProfile
        {
            RootSchema = request.Schema,
            RootTable = request.Table!,
            RootKeyColumn = request.ItemKeyColumn!,
            TableKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BuildTableKey(new DatabaseTable(request.Schema, request.Table!))] = request.ItemKeyColumn!
            },
            Relationships = Array.Empty<ItemRelationship>()
        };

        ValidateItemProfile(profile);

        var baseTable = new DatabaseTable(profile.RootSchema, profile.RootTable);
        var itemKeys = await ReadItemKeysAsync(connection, options, quoter, baseTable, profile.RootKeyColumn, maxItems, cancellationToken);
        var rootBatchSize = NormalizeItemBatchSize(profile.BatchSize, configuredBatchSize);
        var requiredItemTableKeys = request.RequireAllItemTables || profile.RequireAllTables
            ? BuildRequiredItemTableKeys(profile)
            : null;
        var summaries = new List<ResultSetSummary>(itemKeys.Count);

        foreach (var keyBatch in itemKeys.Chunk(rootBatchSize))
        {
            var graphs = await ReadItemGraphsBatchAsync(
                connection,
                quoter,
                profile,
                keyBatch,
                options.CommandTimeoutSeconds,
                rootBatchSize,
                cancellationToken);

            foreach (var key in keyBatch)
            {
                if (!graphs.TryGetValue(BuildValueKey(key), out var graph))
                {
                    graph = new ItemExportGraph(key);
                }

                if (requiredItemTableKeys is not null && !HasRequiredItemTables(graph.Tables, requiredItemTableKeys))
                {
                    continue;
                }

                summaries.Add(await WriteItemGraphAsync(
                    writer,
                    outputDirectory,
                    overwrite,
                    request.ConnectionName,
                    graph,
                    cancellationToken));
            }
        }

        return new ExportSummary(outputDirectory, writer.Format, summaries.Sum(x => x.RowCount), summaries);
    }

    private static HashSet<string> BuildRequiredItemTableKeys(ItemExportProfile profile)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildTableKey(new DatabaseTable(profile.RootSchema, profile.RootTable))
        };

        foreach (var relationship in profile.Relationships)
        {
            required.Add(BuildTableKey(new DatabaseTable(relationship.FromSchema, relationship.FromTable)));
            required.Add(BuildTableKey(new DatabaseTable(relationship.ToSchema, relationship.ToTable)));
        }

        return required;
    }

    private static bool HasRequiredItemTables(
        IReadOnlyDictionary<string, List<IReadOnlyDictionary<string, object?>>> tables,
        IReadOnlyCollection<string> requiredTableKeys)
    {
        foreach (var tableKey in requiredTableKeys)
        {
            if (!tables.TryGetValue(tableKey, out var rows) || rows.Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<ResultSetSummary> WriteItemGraphAsync(
        IExportFormatWriter writer,
        string outputDirectory,
        bool overwrite,
        string connectionName,
        ItemExportGraph graph,
        CancellationToken cancellationToken)
    {
        var itemName = Convert.ToString(graph.RootKey) ?? "null";
        var filePath = Path.Combine(outputDirectory, SanitizeFileName(itemName) + writer.FileExtension);
        if (File.Exists(filePath) && !overwrite)
        {
            throw new IOException($"Output file '{filePath}' already exists. Use overwrite to replace it.");
        }

        await using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await using var session = writer.CreateSession(file, new ExportWriterOptions(IncludeSchema: false));

        await session.BeginAsync(new ExportMetadata(
            connectionName,
            ExportScope.Items,
            itemName,
            DateTimeOffset.UtcNow,
            IncludeSchema: false), cancellationToken);

        long itemRows = 0;
        foreach (var pair in graph.Tables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var columns = pair.Value
                .SelectMany(row => row.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new DatabaseColumn(name, "unknown", null, null))
                .ToArray();

            await session.BeginResultSetAsync(new ResultSetInfo(pair.Key, columns, null), cancellationToken);
            foreach (var row in pair.Value)
            {
                await session.WriteRowAsync(DecodeRowValues(row), cancellationToken);
                itemRows++;
            }

            await session.EndResultSetAsync(pair.Value.Count, cancellationToken);
        }

        await session.CompleteAsync(cancellationToken);
        return new ResultSetSummary(itemName, itemRows);
    }

    private static async Task<Dictionary<string, ItemExportGraph>> ReadItemGraphsBatchAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        ItemExportProfile profile,
        IReadOnlyList<object> rootKeys,
        int commandTimeoutSeconds,
        int queryBatchSize,
        CancellationToken cancellationToken)
    {
        var rootTable = new DatabaseTable(profile.RootSchema, profile.RootTable);
        var graphs = rootKeys
            .GroupBy(BuildValueKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => new ItemExportGraph(x.First()), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<BatchedItemTraversalNode>();
        var delay = TimeSpan.FromMilliseconds(profile.QueryDelayMilliseconds);

        var rootRows = await ReadRowsByColumnInAsync(
            connection,
            quoter,
            rootTable,
            profile.RootKeyColumn,
            rootKeys,
            commandTimeoutSeconds,
            queryBatchSize,
            delay,
            cancellationToken);

        foreach (var row in rootRows)
        {
            if (!row.TryGetValue(profile.RootKeyColumn, out var rootValue) || rootValue is null)
            {
                continue;
            }

            if (graphs.TryGetValue(BuildValueKey(rootValue), out var graph))
            {
                AddItemRow(graph, queue, profile, rootTable, row, depth: 0);
            }
        }

        while (queue.Count > 0)
        {
            var levelNodes = DrainTraversalLevel(queue);
            foreach (var group in BuildRelationshipGroups(levelNodes, profile))
            {
                var targetTable = new DatabaseTable(group.Relationship.ToSchema, group.Relationship.ToTable);
                var valueBuckets = BuildRelationshipValueBuckets(group.Nodes, group.Relationship.FromColumn);
                if (valueBuckets.Count == 0)
                {
                    continue;
                }

                var targetRows = await ReadRowsByColumnInAsync(
                    connection,
                    quoter,
                    targetTable,
                    group.Relationship.ToColumn,
                    valueBuckets.Values.Select(x => x.Value).ToArray(),
                    commandTimeoutSeconds,
                    queryBatchSize,
                    delay,
                    cancellationToken);

                foreach (var targetRow in targetRows)
                {
                    if (!targetRow.TryGetValue(group.Relationship.ToColumn, out var targetValue) || targetValue is null)
                    {
                        continue;
                    }

                    if (!valueBuckets.TryGetValue(BuildValueKey(targetValue), out var bucket))
                    {
                        continue;
                    }

                    foreach (var rootKey in bucket.RootKeys)
                    {
                        if (graphs.TryGetValue(rootKey, out var graph))
                        {
                            AddItemRow(graph, queue, profile, targetTable, targetRow, group.Depth + 1);
                        }
                    }
                }
            }
        }

        return graphs;
    }

    private static IReadOnlyList<BatchedItemTraversalNode> DrainTraversalLevel(Queue<BatchedItemTraversalNode> queue)
    {
        var count = queue.Count;
        var nodes = new List<BatchedItemTraversalNode>(count);
        for (var i = 0; i < count; i++)
        {
            nodes.Add(queue.Dequeue());
        }

        return nodes;
    }

    private static IEnumerable<ItemRelationshipGroup> BuildRelationshipGroups(
        IReadOnlyList<BatchedItemTraversalNode> nodes,
        ItemExportProfile profile)
    {
        foreach (var tableGroup in nodes.GroupBy(x => BuildTableKey(x.Table), StringComparer.OrdinalIgnoreCase))
        {
            var tableNodes = tableGroup.ToArray();
            if (tableNodes.Length == 0 || tableNodes[0].Depth >= profile.MaxDepth)
            {
                continue;
            }

            foreach (var relationship in profile.Relationships.Where(x => SameTable(new DatabaseTable(x.FromSchema, x.FromTable), tableNodes[0].Table)))
            {
                yield return new ItemRelationshipGroup(relationship, tableNodes, tableNodes[0].Depth);
            }
        }
    }

    private static Dictionary<string, RelationshipValueBucket> BuildRelationshipValueBuckets(
        IReadOnlyList<BatchedItemTraversalNode> nodes,
        string column)
    {
        var buckets = new Dictionary<string, RelationshipValueBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!node.Row.TryGetValue(column, out var value) || value is null)
            {
                continue;
            }

            var valueKey = BuildValueKey(value);
            if (!buckets.TryGetValue(valueKey, out var bucket))
            {
                bucket = new RelationshipValueBucket(value);
                buckets[valueKey] = bucket;
            }

            bucket.RootKeys.Add(node.RootKey);
        }

        return buckets;
    }

    private static void AddItemRow(
        ItemExportGraph graph,
        Queue<BatchedItemTraversalNode> queue,
        ItemExportProfile profile,
        DatabaseTable table,
        IReadOnlyDictionary<string, object?> row,
        int depth)
    {
        var identity = BuildRowIdentity(profile, table, row);
        if (!graph.Visited.Add(identity))
        {
            return;
        }

        var tableKey = BuildTableKey(table);
        if (!graph.Tables.TryGetValue(tableKey, out var rows))
        {
            rows = new List<IReadOnlyDictionary<string, object?>>();
            graph.Tables[tableKey] = rows;
        }

        rows.Add(row);
        queue.Enqueue(new BatchedItemTraversalNode(graph.RootKeyIdentity, table, row, depth));
    }

    private static async Task<IReadOnlyList<object>> ReadItemKeysAsync(
        DbConnection connection,
        DatabaseConnectionOptions options,
        IdentifierQuoter quoter,
        DatabaseTable table,
        string itemKeyColumn,
        long? maxItems,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildItemKeyQuery(options.Engine, quoter, table, itemKeyColumn, maxItems);
        command.CommandTimeout = options.CommandTimeoutSeconds;

        var keys = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken))
            {
                continue;
            }

            keys.Add(reader.GetValue(0));
            if (maxItems is not null && keys.Count >= maxItems.Value)
            {
                break;
            }
        }

        return keys;
    }

    private static async Task<Dictionary<string, List<IReadOnlyDictionary<string, object?>>>> ReadItemGraphAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        ItemExportProfile profile,
        object rootKey,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var rootTable = new DatabaseTable(profile.RootSchema, profile.RootTable);
        var result = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ItemTraversalNode>();

        var rootRows = await ReadRowsByColumnAsync(connection, quoter, rootTable, profile.RootKeyColumn, rootKey, commandTimeoutSeconds, cancellationToken);
        foreach (var row in rootRows)
        {
            AddItemRow(result, visited, queue, profile, rootTable, row, depth: 0);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Depth >= profile.MaxDepth)
            {
                continue;
            }

            foreach (var relationship in profile.Relationships.Where(x => SameTable(new DatabaseTable(x.FromSchema, x.FromTable), node.Table)))
            {
                if (!node.Row.TryGetValue(relationship.FromColumn, out var value) || value is null)
                {
                    continue;
                }

                var targetTable = new DatabaseTable(relationship.ToSchema, relationship.ToTable);
                var targetRows = await ReadRowsByColumnAsync(
                    connection,
                    quoter,
                    targetTable,
                    relationship.ToColumn,
                    value,
                    commandTimeoutSeconds,
                    cancellationToken);

                foreach (var targetRow in targetRows)
                {
                    AddItemRow(result, visited, queue, profile, targetTable, targetRow, node.Depth + 1);
                }
            }
        }

        return result;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByColumnAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseTable table,
        string column,
        object value,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {quoter.QuoteTable(table)} WHERE {quoter.QuoteIdentifier(column)} = @value";
        command.CommandTimeout = commandTimeoutSeconds;
        AddParameter(command, "@value", value);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
        var columns = await ReadColumnsAsync(reader, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i].Name] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByColumnInAsync(
        DbConnection connection,
        IdentifierQuoter quoter,
        DatabaseTable table,
        string column,
        IReadOnlyList<object> values,
        int commandTimeoutSeconds,
        int queryBatchSize,
        TimeSpan queryDelay,
        CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var chunk in values.Where(x => x is not null).Chunk(queryBatchSize))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            {
                await using var command = connection.CreateCommand();
                command.CommandText = BuildInQuery(quoter, table, column, chunk.Length);
                command.CommandTimeout = commandTimeoutSeconds;
                for (var i = 0; i < chunk.Length; i++)
                {
                    AddParameter(command, $"@p{i}", chunk[i]);
                }

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
                var columns = await ReadColumnsAsync(reader, cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < columns.Count; i++)
                    {
                        row[columns[i].Name] = await reader.IsDBNullAsync(i, cancellationToken)
                            ? null
                            : reader.GetValue(i);
                    }

                    rows.Add(row);
                }
            }

            if (queryDelay > TimeSpan.Zero)
            {
                await Task.Delay(queryDelay, cancellationToken);
            }
        }

        return rows;
    }

    private static string BuildInQuery(IdentifierQuoter quoter, DatabaseTable table, string column, int parameterCount)
    {
        var parameters = string.Join(", ", Enumerable.Range(0, parameterCount).Select(i => $"@p{i}"));
        return $"SELECT * FROM {quoter.QuoteTable(table)} WHERE {quoter.QuoteIdentifier(column)} IN ({parameters})";
    }

    private static void AddItemRow(
        Dictionary<string, List<IReadOnlyDictionary<string, object?>>> result,
        HashSet<string> visited,
        Queue<ItemTraversalNode> queue,
        ItemExportProfile profile,
        DatabaseTable table,
        IReadOnlyDictionary<string, object?> row,
        int depth)
    {
        var identity = BuildRowIdentity(profile, table, row);
        if (!visited.Add(identity))
        {
            return;
        }

        var tableKey = BuildTableKey(table);
        if (!result.TryGetValue(tableKey, out var rows))
        {
            rows = new List<IReadOnlyDictionary<string, object?>>();
            result[tableKey] = rows;
        }

        rows.Add(row);
        queue.Enqueue(new ItemTraversalNode(table, row, depth));
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

    private static string BuildItemKeyQuery(
        DatabaseEngine engine,
        IdentifierQuoter quoter,
        DatabaseTable table,
        string itemKeyColumn,
        long? maxItems)
    {
        var column = quoter.QuoteIdentifier(itemKeyColumn);
        var tableName = quoter.QuoteTable(table);
        return engine == DatabaseEngine.SqlServer && maxItems is not null
            ? $"SELECT DISTINCT TOP {maxItems.Value} {column} FROM {tableName} WHERE {column} IS NOT NULL"
            : $"SELECT DISTINCT {column} FROM {tableName} WHERE {column} IS NOT NULL{BuildLimitClause(engine, maxItems)}";
    }

    private static string BuildLimitClause(DatabaseEngine engine, long? maxItems)
    {
        if (maxItems is null || engine == DatabaseEngine.SqlServer)
        {
            return "";
        }

        return engine is DatabaseEngine.MySql or DatabaseEngine.PostgreSql or DatabaseEngine.SQLite
            ? $" LIMIT {maxItems.Value}"
            : "";
    }

    private static long? NormalizeMaxRows(long? value)
    {
        return value is null or <= 0 ? null : value;
    }

    private static int NormalizeItemBatchSize(int profileBatchSize, int configuredBatchSize)
    {
        if (profileBatchSize > 0)
        {
            return Math.Min(profileBatchSize, MaxItemBatchSize);
        }

        return configuredBatchSize > 0 ? Math.Min(configuredBatchSize, MaxItemBatchSize) : 100;
    }

    private static object? DecodePossibleBase64(object? value)
    {
        if (value is not string text)
        {
            return value;
        }

        var payload = text;
        var hasExplicitPrefix = text.StartsWith(ExplicitBase64Prefix, StringComparison.OrdinalIgnoreCase);
        if (hasExplicitPrefix)
        {
            payload = text[ExplicitBase64Prefix.Length..];
        }

        if (payload.Length == 0 || payload.Length % 4 != 0 || (!hasExplicitPrefix && payload.Length < 8))
        {
            return value;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            var decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Contains('\uFFFD', StringComparison.Ordinal) ? bytes : decoded;
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static IReadOnlyDictionary<string, object?> DecodeRowValues(IReadOnlyDictionary<string, object?> row)
    {
        var decoded = new Dictionary<string, object?>(row.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in row)
        {
            decoded[pair.Key] = DecodePossibleBase64(pair.Value);
        }

        return decoded;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool SameTable(DatabaseTable left, DatabaseTable right)
    {
        return string.Equals(left.Schema ?? "", right.Schema ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTableKey(DatabaseTable table)
    {
        return string.IsNullOrWhiteSpace(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
    }

    private static string BuildValueKey(object value)
    {
        return $"{value.GetType().FullName}:{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string BuildRowIdentity(ItemExportProfile profile, DatabaseTable table, IReadOnlyDictionary<string, object?> row)
    {
        var tableKey = BuildTableKey(table);
        if (profile.TableKeys.TryGetValue(tableKey, out var primaryKey)
            || profile.TableKeys.TryGetValue(table.Name, out primaryKey))
        {
            if (row.TryGetValue(primaryKey, out var value) && value is not null)
            {
                return $"{tableKey}|pk|{value}";
            }
        }

        var hash = string.Join(
            "\u001f",
            row.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key}={Convert.ToString(x.Value)}"));
        return $"{tableKey}|row|{hash}";
    }

    private static void ValidateItemProfile(ItemExportProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.RootTable))
        {
            throw new InvalidOperationException("Items export requires profile.rootTable.");
        }

        if (string.IsNullOrWhiteSpace(profile.RootKeyColumn))
        {
            throw new InvalidOperationException("Items export requires profile.rootKeyColumn.");
        }

        if (profile.MaxDepth <= 0)
        {
            throw new InvalidOperationException("Items export profile maxDepth must be greater than zero.");
        }

        if (profile.BatchSize <= 0)
        {
            throw new InvalidOperationException("Items export profile batchSize must be greater than zero.");
        }

        if (profile.BatchSize > MaxItemBatchSize)
        {
            throw new InvalidOperationException($"Items export profile batchSize must be less than or equal to {MaxItemBatchSize}.");
        }

        if (profile.QueryDelayMilliseconds < 0)
        {
            throw new InvalidOperationException("Items export profile queryDelayMilliseconds cannot be negative.");
        }

        foreach (var relationship in profile.Relationships)
        {
            if (string.IsNullOrWhiteSpace(relationship.FromTable)
                || string.IsNullOrWhiteSpace(relationship.FromColumn)
                || string.IsNullOrWhiteSpace(relationship.ToTable)
                || string.IsNullOrWhiteSpace(relationship.ToColumn))
            {
                throw new InvalidOperationException("Items export relationships require fromTable, fromColumn, toTable, and toColumn.");
            }
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "item" : sanitized;
    }

    private sealed record ItemTraversalNode(
        DatabaseTable Table,
        IReadOnlyDictionary<string, object?> Row,
        int Depth);

    private sealed class ItemExportGraph
    {
        public ItemExportGraph(object rootKey)
        {
            RootKey = rootKey;
            RootKeyIdentity = BuildValueKey(rootKey);
        }

        public object RootKey { get; }

        public string RootKeyIdentity { get; }

        public Dictionary<string, List<IReadOnlyDictionary<string, object?>>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RelationshipValueBucket
    {
        public RelationshipValueBucket(object value)
        {
            Value = value;
        }

        public object Value { get; }

        public HashSet<string> RootKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record BatchedItemTraversalNode(
        string RootKey,
        DatabaseTable Table,
        IReadOnlyDictionary<string, object?> Row,
        int Depth);

    private sealed record ItemRelationshipGroup(
        ItemRelationship Relationship,
        IReadOnlyList<BatchedItemTraversalNode> Nodes,
        int Depth);

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

        if (request.Scope == ExportScope.Items)
        {
            if (request.ItemProfile is not null)
            {
                ValidateItemProfile(request.ItemProfile);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Table))
            {
                throw new InvalidOperationException("Items export requires a base table.");
            }

            if (string.IsNullOrWhiteSpace(request.ItemKeyColumn))
            {
                throw new InvalidOperationException("Items export requires an item key column.");
            }
        }

        if (request.Scope == ExportScope.Query && string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new InvalidOperationException("Query export requires SQL text.");
        }
    }
}

public sealed record ExportSummary(string OutputPath, string Format, long TotalRows, IReadOnlyList<ResultSetSummary> ResultSets);

public sealed record ResultSetSummary(string Name, long RowCount);
