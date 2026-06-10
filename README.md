# DataBaseExporter

A general-purpose SQL database export tool. The current implementation includes a core library, a CLI, and an Avalonia desktop GUI.

Chinese documentation is available in [README.zh-CN.md](README.zh-CN.md).

## Features

- Manage multiple local or remote database connections through JSON configuration.
- Uses ADO.NET `DbProviderFactory`, with support for SQL Server, PostgreSQL, MySQL, SQLite, and other providers.
- Export scopes: entire database, single table, multiple selected tables, item-by-key export, or a custom read-only SQL query.
- Export formats: JSON, XML, and XLS. XLS uses SpreadsheetML 2003, which Excel can open.
- Preview database structure before export: schema, table names, columns, and approximate row counts.
- Read-only SQL safety checks by default, including refusal of multiple statements and common destructive keywords.
- SSH tunnel support with password or private-key authentication.

## Usage

```powershell
dotnet run --project src\DataBaseExporter.Cli -- preview --config examples\config.sample.json --connection sqlserver
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope database --output exports\db.json --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope table --schema dbo --table Users --output exports\users.xml --format xml --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection mysql --scope items --item-profile examples\item-profile.sample.json --output exports\avatars --format json --max-rows 10 --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope query --sql "SELECT TOP 100 * FROM dbo.Users" --output exports\query.xls --format xls --overwrite
```

Start the GUI:

```powershell
dotnet run --project src\DataBaseExporter.Gui
```

The GUI does not load a configuration file automatically on startup. Open the `Connections` page to enter connection details manually, or load an existing configuration file to fill the fields. After `Cache / Update Connection`, the `Quick Export` page can use the in-memory connection for preview and export.

After the first successful `Preview`, the GUI caches database structure and fills schema/table dropdowns. Switching back to a previously previewed connection reuses the cached structure.

The GUI defaults to exporting the currently selected single table. To export multiple tables, select multiple entries in the table list; the scope switches to `tables`. To export the entire database, explicitly set the scope to `database`.

`Scope` is the active mode switch in the GUI. In `query` mode, only the SQL editor is used for export and table selection is disabled. In `table` or `tables` mode, SQL is disabled and the selected table controls are used. In `items` mode, select a base table and an item key column; the output path is treated as a directory and one file is written per key value. In `database` mode, table and SQL inputs are disabled and the entire database is exported.

Items export uses a relationship graph. It reads key values from the root table, recursively follows configured downstream relationships, and writes one self-contained file per root key. `Max Rows` / `--max-rows` limits the number of root key values for testing; `0` or an empty value means unlimited. Schema metadata is omitted for this export type. String values that look like Base64 payloads are decoded before writing.

Items export batches relationship queries for a group of root keys to reduce per-item database round trips. `batchSize` controls the root/query batch size, and `queryDelayMilliseconds` can add a short pause between batched queries to reduce production database pressure. For production use, make sure the root key and relationship columns are indexed, and prefer read replicas or off-peak windows.

The GUI Items configuration uses dropdowns and `+/-` dynamic rows for root key, Table Keys, and Relationships, and can save/load JSON profiles.

## Provider Configuration

The core library does not hard-code database drivers. The CLI and GUI hosts currently reference common ADO.NET providers:

- SQL Server: `Microsoft.Data.SqlClient`
- PostgreSQL: `Npgsql`
- MySQL: `MySqlConnector`
- SQLite: `Microsoft.Data.Sqlite`

Connection configuration can specify `factoryAssembly` and `factoryTypeName`; the host will try to load the corresponding factory. To add another database type, add the provider package to the host project and add a connection entry to the configuration.

The GUI supports two connection input styles:

- Structured fields: engine, host, port, database, username, password.
- Raw connection string: for custom providers or advanced options.

SSH tunnels can be enabled on the connection page. Authentication can be switched between `Password` and `PrivateKey`. When enabled, the tool creates local port forwarding and connects to the remote database through the local forwarded port.

The connection page has two test actions:

- `Test SSH Tunnel`: tests SSH login and local port forwarding only. It does not read database schema.
- `Test Preview`: if SSH is enabled, tests the SSH tunnel first, then tests database connection and schema reading.

Connection failures are reported by phase where possible: SSH authentication, SSH host/port connectivity, private-key loading, local port forwarding, database connection, and schema reading.

Passwords are not written to the configuration file by default. They are saved only when `Save password to configuration` is checked. The configuration file is plain JSON; use read-only accounts in production and protect the file appropriately.

## Safety

- Use read-only accounts in production. Do not reuse administrator or write-capable accounts.
- Preview row counts before exporting large tables, and use `--max-rows` to validate output.
- Avoid unfiltered full-table or full-database exports during business peak hours.
- Prefer exporting from read replicas, reporting databases, or backups.
- Custom SQL defaults to read-only statements such as `SELECT`, `WITH`, `SHOW`, `DESCRIBE`, and `EXPLAIN`.
- Multiple SQL statements are refused by default to reduce accidental DDL/DML execution risk.

## GUI Architecture

The GUI is built with C# and AvaloniaUI. Export behavior lives in `DataBaseExporter.Core`; the Avalonia project is responsible for:

- Editing and selecting connection configuration.
- Previewing schema metadata: schemas, tables, columns, and row counts.
- Configuring export scope, format, output path, and max rows.
- Calling `DatabaseExportService` and showing progress/results.
