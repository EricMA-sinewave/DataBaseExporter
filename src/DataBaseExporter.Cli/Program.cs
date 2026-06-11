using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Exporting;
using System.Text.Json;

var exitCode = await CliApplication.RunAsync(args);
return exitCode;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            var configPath = options.GetRequired("--config");
            var connectionName = options.GetRequired("--connection");
            var configuration = await DatabaseExportConfiguration.LoadAsync(configPath, cancellationToken);
            var service = DatabaseExportService.CreateDefault();

            switch (command)
            {
                case "preview":
                    await RunPreviewAsync(service, configuration, connectionName, cancellationToken);
                    return 0;
                case "export":
                    await RunExportAsync(service, configuration, connectionName, options, cancellationToken);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task RunPreviewAsync(
        DatabaseExportService service,
        DatabaseExportConfiguration configuration,
        string connectionName,
        CancellationToken cancellationToken)
    {
        var previews = await service.PreviewAsync(configuration, connectionName, cancellationToken);
        Console.WriteLine($"Tables: {previews.Count}");
        foreach (var preview in previews)
        {
            var rowCount = preview.RowCount?.ToString() ?? "unknown";
            Console.WriteLine($"{preview.Table} | rows: {rowCount} | columns: {preview.Columns.Count}");
            foreach (var column in preview.Columns)
            {
                Console.WriteLine($"  - {column.Name}: {column.DataType}" + (column.IsNullable is null ? "" : $" nullable={column.IsNullable.Value}"));
            }
        }
    }

    private static async Task RunExportAsync(
        DatabaseExportService service,
        DatabaseExportConfiguration configuration,
        string connectionName,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var scope = ParseScope(options.Get("--scope") ?? "database");
        var request = new ExportRequest
        {
            ConnectionName = connectionName,
            Scope = scope,
            Schema = options.Get("--schema"),
            Table = options.Get("--table"),
            ItemKeyColumn = options.Get("--item-key"),
            ItemProfile = await LoadItemProfileAsync(options.Get("--item-profile"), cancellationToken),
            RequireAllItemTables = options.Has("--require-all-item-tables"),
            Sql = options.Get("--sql"),
            Format = options.Get("--format"),
            OutputPath = options.GetRequired("--output"),
            IncludeSchema = !options.Has("--no-schema"),
            Overwrite = options.Has("--overwrite"),
            BatchSize = options.GetInt("--batch-size"),
            MaxRows = options.GetLong("--max-rows")
        };

        var summary = await service.ExportAsync(configuration, request, cancellationToken);
        Console.WriteLine($"Exported {summary.TotalRows} rows to {summary.OutputPath} ({summary.Format}).");
        foreach (var resultSet in summary.ResultSets)
        {
            Console.WriteLine($"  - {resultSet.Name}: {resultSet.RowCount} rows");
        }
    }

    private static ExportScope ParseScope(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "database" or "db" or "all" => ExportScope.Database,
            "table" => ExportScope.Table,
            "items" => ExportScope.Items,
            "query" or "sql" => ExportScope.Query,
            _ => throw new InvalidOperationException($"Unsupported scope '{value}'. Use database, table, items, or query.")
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
DataBaseExporter

Commands:
  preview --config <path> --connection <name>
  export  --config <path> --connection <name> --scope database --output <path> [--format json|xml|xls]
  export  --config <path> --connection <name> --scope table --table <name> [--schema <schema>] --output <path>
  export  --config <path> --connection <name> --scope items --item-profile <path> --output <directory>
  export  --config <path> --connection <name> --scope query --sql <select-sql> --output <path>

Common options:
  --format <json|xml|xls>    Defaults to configuration defaults.format.
  --max-rows <n>             Caps exported rows per result set.
  --overwrite                Replace an existing output file.
  --no-schema                Omit column metadata when the format supports it.
  --require-all-item-tables  Items only: skip root keys missing data in any profile table.
""");
    }

    private static async Task<ItemExportProfile?> LoadItemProfileAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ItemExportProfile>(stream, DatabaseExportConfiguration.CreateJsonOptions(), cancellationToken)
            ?? throw new InvalidOperationException($"Item profile '{path}' is empty.");
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument '{key}'.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key] = null;
                continue;
            }

            options._values[key] = args[++i];
        }

        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string GetRequired(string key)
    {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required.");
        }

        return value;
    }

    public int? GetInt(string key)
    {
        var value = Get(key);
        return value is null ? null : int.Parse(value);
    }

    public long? GetLong(string key)
    {
        var value = Get(key);
        return value is null ? null : long.Parse(value);
    }
}
