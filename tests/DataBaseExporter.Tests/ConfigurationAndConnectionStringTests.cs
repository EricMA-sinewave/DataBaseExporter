using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Exporting;
using System.Text.Json;

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
    public void Compose_MySqlEndpoint_AddsZeroDateTimeHandling()
    {
        var options = new DatabaseConnectionOptions
        {
            Engine = DatabaseEngine.MySql,
            ProviderInvariantName = "MySqlConnector",
            Endpoint = new DatabaseEndpointOptions
            {
                Host = "db.example.com",
                Port = 3306,
                Database = "app",
                Username = "readonly",
                Password = "secret"
            }
        };

        var connectionString = ConnectionStringComposer.Compose(options);

        StringAssert.Contains(connectionString, "ConvertZeroDateTime=True");
    }

    [TestMethod]
    public void Compose_MySqlRawConnectionString_PreservesExplicitZeroDateTimeHandling()
    {
        var options = new DatabaseConnectionOptions
        {
            Engine = DatabaseEngine.MySql,
            ProviderInvariantName = "MySqlConnector",
            ConnectionString = "Server=db.example.com;Database=app;User ID=readonly;AllowZeroDateTime=True"
        };

        var connectionString = ConnectionStringComposer.Compose(options);

        Assert.IsTrue(connectionString.Contains("AllowZeroDateTime=True", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(connectionString.Contains("ConvertZeroDateTime", StringComparison.OrdinalIgnoreCase));
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

    [TestMethod]
    public void ItemExportProfile_DeserializesRelationships()
    {
        var profile = JsonSerializer.Deserialize<ItemExportProfile>("""
{
  "rootTable": "avatar",
  "rootKeyColumn": "id",
  "tableKeys": {
    "avatar": "id",
    "inventory": "id"
  },
  "relationships": [
    {
      "fromTable": "avatar",
      "fromColumn": "id",
      "toTable": "inventory",
      "toColumn": "avatar_id"
    }
  ],
  "maxDepth": 8,
  "batchSize": 50,
  "queryDelayMilliseconds": 25,
  "requireAllTables": true
}
""", DatabaseExportConfiguration.CreateJsonOptions());

        Assert.IsNotNull(profile);
        Assert.AreEqual("avatar", profile.RootTable);
        Assert.AreEqual("id", profile.TableKeys["avatar"]);
        Assert.AreEqual(1, profile.Relationships.Count);
        Assert.AreEqual("inventory", profile.Relationships[0].ToTable);
        Assert.AreEqual(8, profile.MaxDepth);
        Assert.AreEqual(50, profile.BatchSize);
        Assert.AreEqual(25, profile.QueryDelayMilliseconds);
        Assert.IsTrue(profile.RequireAllTables);
    }
}
