using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using DataBaseExporter.Core.Configuration;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DataBaseExporter.Core.Database;

public interface IDatabaseConnectionFactory
{
    Task<DbConnection> OpenAsync(DatabaseConnectionOptions options, CancellationToken cancellationToken = default);
}

public sealed class DatabaseConnectionFactory : IDatabaseConnectionFactory
{
    public async Task<DbConnection> OpenAsync(DatabaseConnectionOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(options.ProviderInvariantName);
        var factory = ResolveFactory(options);
        SshTunnelSession? tunnel = null;
        try
        {
            tunnel = options.SshTunnel?.Enabled == true && options.Endpoint is not null
                ? SshTunnelSession.Open(options)
                : null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SSH tunnel failed before database connection: {ex.Message}", ex);
        }

        var connectionString = tunnel is null
            ? ConnectionStringComposer.Compose(options)
            : ConnectionStringComposer.Compose(options, tunnel.LocalHost, (int)tunnel.LocalPort);

        var connection = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Provider '{options.ProviderInvariantName}' did not create a connection.");

        connection.ConnectionString = connectionString;

        try
        {
            await connection.OpenAsync(cancellationToken);
            return tunnel is null ? connection : new TunnelingDbConnection(connection, tunnel);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            tunnel?.Dispose();
            var phase = tunnel is null ? "Database connection failed" : "Database connection failed after SSH tunnel setup";
            throw new InvalidOperationException($"{phase}: {ex.Message}", ex);
        }
    }

    private static DbProviderFactory ResolveFactory(DatabaseConnectionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FactoryTypeName))
        {
            var assembly = ResolveAssembly(options);
            var factoryType = assembly.GetType(options.FactoryTypeName, throwOnError: true)
                ?? throw new InvalidOperationException($"Factory type '{options.FactoryTypeName}' was not found.");

            if (factoryType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is DbProviderFactory instance)
            {
                return instance;
            }

            if (Activator.CreateInstance(factoryType) is DbProviderFactory created)
            {
                return created;
            }

            throw new InvalidOperationException($"Factory type '{options.FactoryTypeName}' is not a DbProviderFactory.");
        }

        try
        {
            return DbProviderFactories.GetFactory(options.ProviderInvariantName);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Provider '{options.ProviderInvariantName}' is not registered. Configure factoryAssembly and factoryTypeName, or register the provider package in the host application.",
                ex);
        }
    }

    private static Assembly ResolveAssembly(DatabaseConnectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FactoryAssembly))
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetType(options.FactoryTypeName!, throwOnError: false) is not null)
                {
                    return assembly;
                }
            }

            throw new InvalidOperationException("factoryAssembly is required when factoryTypeName is not already loaded.");
        }

        return File.Exists(options.FactoryAssembly)
            ? Assembly.LoadFrom(options.FactoryAssembly)
            : Assembly.Load(new AssemblyName(options.FactoryAssembly));
    }
}

public sealed class SshTunnelTester
{
    public Task<SshTunnelTestResult> TestAsync(DatabaseConnectionOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(options.ProviderInvariantName);
        if (options.SshTunnel?.Enabled != true)
        {
            throw new InvalidOperationException("SSH tunnel is not enabled for this connection.");
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = SshTunnelSession.Open(options);
            return new SshTunnelTestResult(
                session.LocalHost,
                session.LocalPort,
                options.Endpoint?.Host ?? "",
                options.Endpoint?.Port ?? ConnectionStringComposer.GetDefaultPort(options.Engine));
        }, cancellationToken);
    }
}

public sealed record SshTunnelTestResult(string LocalHost, uint LocalPort, string RemoteHost, int? RemotePort);

internal sealed class SshTunnelSession : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;
    private bool _isDisposed;

    private SshTunnelSession(SshClient client, ForwardedPortLocal port, string localHost, uint localPort)
    {
        _client = client;
        _port = port;
        LocalHost = localHost;
        LocalPort = localPort;
    }

    public string LocalHost { get; }

    public uint LocalPort { get; }

    public static SshTunnelSession Open(DatabaseConnectionOptions options)
    {
        var ssh = options.SshTunnel ?? throw new InvalidOperationException("SSH tunnel options are required.");
        var endpoint = options.Endpoint ?? throw new InvalidOperationException("Endpoint options are required for SSH tunneling.");

        var connectionInfo = new ConnectionInfo(ssh.Host, ssh.Port, ssh.Username, BuildAuthenticationMethods(ssh).ToArray());
        var client = new SshClient(connectionInfo);
        try
        {
            client.Connect();
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new InvalidOperationException("SSH authentication failed. Check username, password, private key, and passphrase.", ex);
        }
        catch (SshConnectionException ex)
        {
            client.Dispose();
            throw new InvalidOperationException("SSH connection failed. Check SSH host, port, firewall, and server availability.", ex);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"SSH connection failed: {ex.Message}", ex);
        }

        var remoteHost = endpoint.Host;
        var remotePort = (uint)(endpoint.Port ?? ConnectionStringComposer.GetDefaultPort(options.Engine)
            ?? throw new InvalidOperationException($"Engine '{options.Engine}' does not have a default port for SSH tunneling."));
        var localPort = ssh.LocalPort;
        var port = new ForwardedPortLocal(ssh.LocalHost, localPort, remoteHost, remotePort);
        try
        {
            client.AddForwardedPort(port);
            port.Start();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"SSH local port forwarding failed. Check local port and remote database host/port. {ex.Message}", ex);
        }

        return new SshTunnelSession(client, port, ssh.LocalHost, port.BoundPort);
    }

    private static IEnumerable<AuthenticationMethod> BuildAuthenticationMethods(SshTunnelOptions ssh)
    {
        if (ssh.AuthenticationMode == SshAuthenticationMode.Password)
        {
            if (string.IsNullOrWhiteSpace(ssh.Password))
            {
                throw new InvalidOperationException("SSH password is required for password authentication.");
            }

            yield return new PasswordAuthenticationMethod(ssh.Username, ssh.Password ?? "");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ssh.PrivateKeyPath))
        {
            throw new InvalidOperationException("SSH private key path is required for private key authentication.");
        }

        PrivateKeyFile keyFile;
        try
        {
            keyFile = string.IsNullOrWhiteSpace(ssh.PrivateKeyPassphrase)
                ? new PrivateKeyFile(ssh.PrivateKeyPath)
                : new PrivateKeyFile(ssh.PrivateKeyPath, ssh.PrivateKeyPassphrase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SSH private key could not be loaded. Check private key path and passphrase. {ex.Message}", ex);
        }

        yield return new PrivateKeyAuthenticationMethod(ssh.Username, keyFile);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            if (_port.IsStarted)
            {
                _port.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
            // SSH.NET can report the owning client as disposed during repeated teardown.
        }
        finally
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}

internal sealed class TunnelingDbConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly SshTunnelSession _tunnel;
    private bool _isDisposed;

    public TunnelingDbConnection(DbConnection inner, SshTunnelSession tunnel)
    {
        _inner = inner;
        _tunnel = tunnel;
    }

#pragma warning disable CS8765
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        [param: AllowNull]
        set => _inner.ConnectionString = value;
    }
#pragma warning restore CS8765

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    public override void Close() => _inner.Close();

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
    {
        return _inner.BeginTransaction(isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return _inner.CreateCommand();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            _inner.Dispose();
            _tunnel.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _inner.DisposeAsync();
        _tunnel.Dispose();
        GC.SuppressFinalize(this);
    }
}
