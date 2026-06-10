using System.Data.Common;
using DataBaseExporter.Core.Configuration;

namespace DataBaseExporter.Core.Database;

public static class ConnectionStringComposer
{
    public static string Compose(DatabaseConnectionOptions options, string? hostOverride = null, int? portOverride = null)
    {
        if (options.Endpoint is null)
        {
            var connectionString = options.ConnectionString
                ?? throw new InvalidOperationException("Connection string is required when endpoint is not configured.");
            return options.Engine == DatabaseEngine.MySql
                ? ApplyMySqlZeroDateTimeDefault(connectionString)
                : connectionString;
        }

        var endpoint = options.Endpoint;
        var host = hostOverride ?? endpoint.Host;
        var port = portOverride ?? endpoint.Port ?? GetDefaultPort(options.Engine);
        var builder = new DbConnectionStringBuilder();

        switch (options.Engine)
        {
            case DatabaseEngine.SqlServer:
                builder["Server"] = port is null ? host : $"{host},{port}";
                if (!string.IsNullOrWhiteSpace(endpoint.Database))
                {
                    builder["Database"] = endpoint.Database;
                }

                if (endpoint.IntegratedSecurity)
                {
                    builder["Integrated Security"] = true;
                }
                else
                {
                    builder["User Id"] = endpoint.Username ?? "";
                    builder["Password"] = endpoint.Password ?? "";
                }

                builder["Encrypt"] = endpoint.Encrypt;
                builder["Trust Server Certificate"] = endpoint.TrustServerCertificate;
                break;
            case DatabaseEngine.PostgreSql:
                builder["Host"] = host;
                if (port is not null)
                {
                    builder["Port"] = port.Value;
                }

                AddIfPresent(builder, "Database", endpoint.Database);
                AddIfPresent(builder, "Username", endpoint.Username);
                AddIfPresent(builder, "Password", endpoint.Password);
                break;
            case DatabaseEngine.MySql:
                builder["Server"] = host;
                if (port is not null)
                {
                    builder["Port"] = port.Value;
                }

                AddIfPresent(builder, "Database", endpoint.Database);
                AddIfPresent(builder, "User ID", endpoint.Username);
                AddIfPresent(builder, "Password", endpoint.Password);
                if (!HasMySqlZeroDateTimeOption(endpoint.AdditionalOptions))
                {
                    builder["ConvertZeroDateTime"] = true;
                }

                break;
            case DatabaseEngine.SQLite:
                builder["Data Source"] = endpoint.Host;
                if (endpoint.ReadOnlySqlite)
                {
                    builder["Mode"] = "ReadOnly";
                }

                break;
            default:
                throw new InvalidOperationException($"Engine '{options.Engine}' cannot compose a connection string.");
        }

        foreach (var pair in endpoint.AdditionalOptions)
        {
            builder[pair.Key] = pair.Value;
        }

        return builder.ConnectionString;
    }

    private static string ApplyMySqlZeroDateTimeDefault(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!HasMySqlZeroDateTimeOption(builder))
        {
            builder["ConvertZeroDateTime"] = true;
        }

        return builder.ConnectionString;
    }

    private static bool HasMySqlZeroDateTimeOption(IDictionary<string, string> options)
    {
        return options.Keys.Any(IsMySqlZeroDateTimeOption);
    }

    private static bool HasMySqlZeroDateTimeOption(DbConnectionStringBuilder builder)
    {
        return builder.Keys.Cast<string>().Any(IsMySqlZeroDateTimeOption);
    }

    private static bool IsMySqlZeroDateTimeOption(string key)
    {
        var normalized = key.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return string.Equals(normalized, "AllowZeroDateTime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "ConvertZeroDateTime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "AllowZeroDatetime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "ConvertZeroDatetime", StringComparison.OrdinalIgnoreCase);
    }

    public static int? GetDefaultPort(DatabaseEngine engine)
    {
        return engine switch
        {
            DatabaseEngine.SqlServer => 1433,
            DatabaseEngine.PostgreSql => 5432,
            DatabaseEngine.MySql => 3306,
            _ => null
        };
    }

    private static void AddIfPresent(DbConnectionStringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder[key] = value;
        }
    }
}
