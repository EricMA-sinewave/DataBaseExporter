using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;

namespace DataBaseExporter.Tests;

[TestClass]
public sealed class ConfigurationAndConnectionStringTests
{
    [TestMethod]
    public void Compose_PostgreSqlEndpoint_BuildsExpectedConnectionString()
    {
        var options = new DatabaseConnectionOptions
        {
            Engine = DatabaseEngine.PostgreSql,
            ProviderInvariantName = "Npgsql",
            Endpoint = new DatabaseEndpointOptions
            {
                Host = "db.example.com",
                Port = 5432,
                Database = "app",
                Username = "readonly",
                Password = "secret"
            }
        };

        var connectionString = ConnectionStringComposer.Compose(options);

        StringAssert.Contains(connectionString, "Host=db.example.com");
        StringAssert.Contains(connectionString, "Port=5432");
        StringAssert.Contains(connectionString, "Database=app");
        StringAssert.Contains(connectionString, "Username=readonly");
    }

    [TestMethod]
    public async Task LoadAsync_ReadsStringEnumEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
{
  "connections": {
    "sqlite": {
      "engine": "SQLite",
      "providerInvariantName": "Microsoft.Data.Sqlite",
      "endpoint": {
        "host": "local.db"
      }
    }
  }
}
""");

        try
        {
            var configuration = await DatabaseExportConfiguration.LoadAsync(path);

            Assert.AreEqual(DatabaseEngine.SQLite, configuration.GetConnection("sqlite").Engine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_AllowsSshPasswordModeWithoutSavedPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
{
  "connections": {
    "postgres": {
      "engine": "PostgreSql",
      "providerInvariantName": "Npgsql",
      "endpoint": {
        "host": "db.internal",
        "port": 5432
      },
      "sshTunnel": {
        "enabled": true,
        "authenticationMode": "Password",
        "host": "ssh.example.com",
        "port": 22,
        "username": "deploy"
      }
    }
  }
}
""");

        try
        {
            var configuration = await DatabaseExportConfiguration.LoadAsync(path);

            Assert.AreEqual(SshAuthenticationMode.Password, configuration.GetConnection("postgres").SshTunnel!.AuthenticationMode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_ReadsSshPrivateKeyMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
{
  "connections": {
    "postgres": {
      "engine": "PostgreSql",
      "providerInvariantName": "Npgsql",
      "endpoint": {
        "host": "db.internal",
        "port": 5432
      },
      "sshTunnel": {
        "enabled": true,
        "authenticationMode": "PrivateKey",
        "host": "ssh.example.com",
        "port": 22,
        "username": "deploy",
        "privateKeyPath": "C:\\Users\\me\\.ssh\\id_rsa"
      }
    }
  }
}
""");

        try
        {
            var configuration = await DatabaseExportConfiguration.LoadAsync(path);

            Assert.AreEqual(SshAuthenticationMode.PrivateKey, configuration.GetConnection("postgres").SshTunnel!.AuthenticationMode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
