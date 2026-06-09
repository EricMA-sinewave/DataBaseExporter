using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataBaseExporter.Core.Configuration;

public sealed class DatabaseExportConfiguration
{
    public Dictionary<string, DatabaseConnectionOptions> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public ExportDefaults Defaults { get; init; } = new();

    public static async Task<DatabaseExportConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var options = CreateJsonOptions();

        var configuration = await JsonSerializer.DeserializeAsync<DatabaseExportConfiguration>(stream, options, cancellationToken)
            ?? throw new InvalidOperationException($"Configuration file '{path}' is empty.");

        configuration.Validate();
        return configuration;
    }

    public DatabaseConnectionOptions GetConnection(string name)
    {
        if (!Connections.TryGetValue(name, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{name}' was not found in configuration.");
        }

        connection.Validate(name);
        return connection;
    }

    public void Validate()
    {
        if (Connections.Count == 0)
        {
            throw new InvalidOperationException("At least one database connection must be configured.");
        }

        foreach (var pair in Connections)
        {
            pair.Value.Validate(pair.Key);
        }
    }

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class DatabaseConnectionOptions
{
    public string? DisplayName { get; init; }

    public DatabaseEngine Engine { get; init; } = DatabaseEngine.Custom;

    public string ProviderInvariantName { get; init; } = "";

    public string? ConnectionString { get; init; }

    public DatabaseEndpointOptions? Endpoint { get; init; }

    public SshTunnelOptions? SshTunnel { get; init; }

    public string? FactoryAssembly { get; init; }

    public string? FactoryTypeName { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 60;

    public bool ReadOnlyMode { get; init; } = true;

    public string OpeningIdentifierQuote { get; init; } = "\"";

    public string ClosingIdentifierQuote { get; init; } = "\"";

    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(ProviderInvariantName))
        {
            throw new InvalidOperationException($"Connection '{name}' must define providerInvariantName.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionString) && Endpoint is null)
        {
            throw new InvalidOperationException($"Connection '{name}' must define connectionString or endpoint.");
        }

        Endpoint?.Validate(name);
        SshTunnel?.Validate(name);

        if (Endpoint is not null && Engine == DatabaseEngine.Custom)
        {
            throw new InvalidOperationException($"Connection '{name}' must define engine when endpoint is used.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException($"Connection '{name}' commandTimeoutSeconds must be greater than zero.");
        }
    }
}

public enum DatabaseEngine
{
    Custom,
    SqlServer,
    PostgreSql,
    MySql,
    SQLite
}

public sealed class DatabaseEndpointOptions
{
    public string Host { get; init; } = "";

    public int? Port { get; init; }

    public string? Database { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool IntegratedSecurity { get; init; }

    public bool TrustServerCertificate { get; init; } = true;

    public bool Encrypt { get; init; }

    public bool ReadOnlySqlite { get; init; } = true;

    public Dictionary<string, string> AdditionalOptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException($"Connection '{name}' endpoint.host is required.");
        }

        if (Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Connection '{name}' endpoint.port must be between 1 and 65535.");
        }
    }
}

public sealed class SshTunnelOptions
{
    public bool Enabled { get; init; }

    public SshAuthenticationMode AuthenticationMode { get; init; } = SshAuthenticationMode.Password;

    public string Host { get; init; } = "";

    public int Port { get; init; } = 22;

    public string Username { get; init; } = "";

    public string? Password { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public string LocalHost { get; init; } = "127.0.0.1";

    public uint LocalPort { get; init; }

    public void Validate(string name)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException($"Connection '{name}' sshTunnel.host is required when SSH tunnel is enabled.");
        }

        if (Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Connection '{name}' sshTunnel.port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException($"Connection '{name}' sshTunnel.username is required when SSH tunnel is enabled.");
        }

        // Authentication material may be intentionally omitted from saved configuration.
        // It is validated when the user tests or opens the connection.
    }
}

public enum SshAuthenticationMode
{
    Password,
    PrivateKey
}

public sealed class ExportDefaults
{
    public string Format { get; init; } = "json";

    public int BatchSize { get; init; } = 1000;

    public bool IncludeSchema { get; init; } = true;

    public ExportSafetyOptions Safety { get; init; } = new();
}

public sealed class ExportSafetyOptions
{
    public bool AllowNonQueryOperations { get; init; }

    public bool RequireReadOnlySql { get; init; } = true;

    public bool RefuseMultipleStatements { get; init; } = true;

    public long? DefaultMaxRows { get; init; }
}
