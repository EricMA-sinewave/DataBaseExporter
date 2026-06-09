using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DataBaseExporter.Core.Configuration;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Gui;

public sealed class MainWindow : Window
{
    private const string DefaultSchemaLabel = "(default)";

    private readonly DatabaseExportService _service = DatabaseExportService.CreateDefault();
    private readonly TabControl _tabs = new();
    private readonly ComboBox _quickConnections = new() { MinWidth = 220 };
    private readonly ComboBox _scope = new() { MinWidth = 120 };
    private readonly ComboBox _format = new() { MinWidth = 100 };
    private readonly ComboBox _schema = new() { MinWidth = 140 };
    private readonly ComboBox _table = new() { MinWidth = 180 };
    private readonly TextBox _sql = new() { PlaceholderText = "SELECT ...", AcceptsReturn = true, MinHeight = 76 };
    private readonly TextBox _output = new() { PlaceholderText = "exports\\data.json" };
    private readonly NumericUpDown _maxRows = new() { Minimum = 1, Maximum = decimal.MaxValue, Increment = 100, PlaceholderText = "max rows" };
    private readonly CheckBox _overwrite = new() { Content = "Overwrite" };
    private readonly Button _preview = new() { Content = "Preview" };
    private readonly Button _export = new() { Content = "Export" };
    private readonly ListBox _tables = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _connectionStatus = new() { TextWrapping = TextWrapping.Wrap };

    private readonly TextBox _configPath = new() { PlaceholderText = "examples\\config.sample.json" };
    private readonly TextBox _connectionName = new() { PlaceholderText = "local-postgres" };
    private readonly ComboBox _engine = new() { MinWidth = 160 };
    private readonly TextBox _host = new() { PlaceholderText = "localhost or database.example.com" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Increment = 1, PlaceholderText = "port" };
    private readonly TextBox _database = new() { PlaceholderText = "database" };
    private readonly TextBox _username = new() { PlaceholderText = "username" };
    private readonly TextBox _password = new() { PasswordChar = '*' };
    private readonly TextBox _providerInvariant = new() { PlaceholderText = "Provider invariant name" };
    private readonly TextBox _factoryAssembly = new() { PlaceholderText = "Factory assembly" };
    private readonly TextBox _factoryTypeName = new() { PlaceholderText = "Factory type name" };
    private readonly CheckBox _savePassword = new() { Content = "Save password to configuration" };
    private readonly CheckBox _integratedSecurity = new() { Content = "Integrated Security" };
    private readonly CheckBox _readOnly = new() { Content = "Read-only mode", IsChecked = true };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 3600, Increment = 10, Value = 60 };
    private readonly TextBox _rawConnectionString = new() { PlaceholderText = "Optional raw connection string", AcceptsReturn = true, MinHeight = 64 };
    private readonly CheckBox _useSsh = new() { Content = "Use SSH tunnel" };
    private readonly ComboBox _sshAuthMode = new() { MinWidth = 140 };
    private readonly TextBox _sshHost = new() { PlaceholderText = "ssh.example.com" };
    private readonly NumericUpDown _sshPort = new() { Minimum = 1, Maximum = 65535, Increment = 1, Value = 22 };
    private readonly TextBox _sshUsername = new() { PlaceholderText = "ssh user" };
    private readonly TextBox _sshPassword = new() { PasswordChar = '*' };
    private readonly TextBox _sshKeyPath = new() { PlaceholderText = "private key path" };
    private readonly TextBox _sshPassphrase = new() { PasswordChar = '*' };
    private readonly NumericUpDown _sshLocalPort = new() { Minimum = 0, Maximum = 65535, Increment = 1, Value = 0 };
    private readonly Button _testSsh = new() { Content = "Test SSH Tunnel" };

    private DatabaseExportConfiguration _configuration = new();
    private IReadOnlyList<DatabaseTablePreview> _lastPreview = Array.Empty<DatabaseTablePreview>();
    private IReadOnlyList<DatabaseTablePreview> _visiblePreview = Array.Empty<DatabaseTablePreview>();
    private readonly Dictionary<string, DatabaseTablePreview> _visiblePreviewByDisplayText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<DatabaseTablePreview>> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _connectionsAllowedToPersistSecrets = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        Title = "DataBaseExporter";
        Width = 1180;
        Height = 760;
        MinWidth = 980;
        MinHeight = 620;

        _scope.ItemsSource = new[] { "database", "table", "tables", "query" };
        _scope.SelectedItem = "table";
        _format.ItemsSource = new[] { "json", "xml", "xls" };
        _format.SelectedIndex = 0;
        _engine.ItemsSource = new[] { "SqlServer", "PostgreSql", "MySql", "SQLite", "Custom" };
        _engine.SelectedItem = "PostgreSql";
        _sshAuthMode.ItemsSource = new[] { "Password", "PrivateKey" };
        _sshAuthMode.SelectedItem = "Password";
        _configPath.Text = "connections.json";

        _preview.Click += async (_, _) => await PreviewAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _scope.SelectionChanged += (_, _) => ApplyScopeUiState();
        _quickConnections.SelectionChanged += (_, _) =>
        {
            RestorePreviewForSelectedConnection();
            FillEditorFromSelectedConnection();
        };
        _schema.SelectionChanged += (_, _) => RefreshTablesForSelectedSchema();
        _table.SelectionChanged += (_, _) => ShowSelectedDropdownTable();
        _tables.SelectionChanged += (_, _) => ShowSelectedTable();
        _engine.SelectionChanged += (_, _) => ApplyEngineDefaults();
        _useSsh.Click += (_, _) => ApplySshUiState();
        _sshAuthMode.SelectionChanged += (_, _) => ApplySshUiState();
        _testSsh.Click += async (_, _) => await TestSshTunnelAsync();

        Content = BuildLayout();
        _tables.SelectionMode = SelectionMode.Multiple;
        RefreshConnectionLists();
        ApplyEngineDefaults();
        ApplyScopeUiState();
    }

    private Control BuildLayout()
    {
        var root = new DockPanel();
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                BuildMenuItem("Main", 0),
                BuildMenuItem("Connections", 1)
            }
        };
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        _tabs.Items.Add(new TabItem { Header = "Quick Export", Content = BuildMainPage() });
        _tabs.Items.Add(new TabItem { Header = "Connections", Content = BuildConnectionPage() });
        _tabs.SelectedIndex = 0;
        root.Children.Add(_tabs);

        return root;
    }

    private MenuItem BuildMenuItem(string header, int selectedIndex)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _tabs.SelectedIndex = selectedIndex;
        return item;
    }

    private Control BuildMainPage()
    {
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        controls.Children.Add(Field("Cached Connection", _quickConnections, 220));
        controls.Children.Add(Field("Scope", _scope, 120));
        controls.Children.Add(Field("Format", _format, 100));
        controls.Children.Add(Field("Schema", _schema, 140));
        controls.Children.Add(Field("Table", _table, 180));
        controls.Children.Add(Field("Max Rows", _maxRows, 130));
        controls.Children.Add(_overwrite);
        controls.Children.Add(_preview);
        controls.Children.Add(_export);
        top.Children.Add(controls);
        top.Children.Add(Field("Output", _output));
        top.Children.Add(Field("SQL", _sql));

        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var body = new Grid
        {
            Margin = new Thickness(12, 0, 12, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(340)),
                new ColumnDefinition(GridLength.Star)
            }
        };

        body.Children.Add(_tables);
        var detailScroll = new ScrollViewer { Content = _details, Padding = new Thickness(12) };
        Grid.SetColumn(detailScroll, 1);
        body.Children.Add(detailScroll);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _status.Margin = new Thickness(12);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        return root;
    }

    private Control BuildConnectionPage()
    {
        var root = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 12,
                Children =
                {
                    BuildConfigFilePanel(),
                    BuildConnectionEditor(),
                    BuildSshPanel(),
                    BuildConnectionActions(),
                    _connectionStatus
                }
            }
        };

        return root;
    }

    private Control BuildConfigFilePanel()
    {
        var load = new Button { Content = "Load Config" };
        var save = new Button { Content = "Save Config" };
        load.Click += async (_, _) => await LoadConfigurationAsync();
        save.Click += async (_, _) => await SaveConfigurationAsync();

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Configuration File", FontWeight = FontWeight.SemiBold },
                new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        DockRight(save),
                        DockRight(load),
                        _configPath
                    }
                }
            }
        };
    }

    private Control BuildConnectionEditor()
    {
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Connection", FontWeight = FontWeight.SemiBold },
                Row(
                    Field("Name", _connectionName, 220),
                    Field("Engine", _engine, 160),
                    Field("Timeout", _timeout, 110)),
                Row(
                    Field("Host / SQLite File", _host, 260),
                    Field("Port", _port, 110),
                    Field("Database", _database, 180)),
                Row(
                    Field("Username", _username, 180),
                    Field("Password", _password, 180),
                    _savePassword,
                    _integratedSecurity,
                    _readOnly),
                Row(
                    Field("Provider", _providerInvariant, 220),
                    Field("Factory Assembly", _factoryAssembly, 220),
                    Field("Factory Type", _factoryTypeName, 300)),
                Field("Raw Connection String", _rawConnectionString)
            }
        };
    }

    private Control BuildSshPanel()
    {
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "SSH Tunnel", FontWeight = FontWeight.SemiBold },
                Row(_useSsh, Field("Auth", _sshAuthMode, 140), Field("SSH Host", _sshHost, 240), Field("SSH Port", _sshPort, 110), Field("SSH User", _sshUsername, 160), _testSsh),
                Row(Field("SSH Password", _sshPassword, 180), Field("Private Key", _sshKeyPath, 300), Field("Key Passphrase", _sshPassphrase, 180), Field("Local Port", _sshLocalPort, 110))
            }
        };
    }

    private Control BuildConnectionActions()
    {
        var cache = new Button { Content = "Cache / Update Connection" };
        var test = new Button { Content = "Test Preview" };
        cache.Click += (_, _) => CacheCurrentConnection();
        test.Click += async (_, _) => await TestCurrentConnectionAsync();

        return new WrapPanel
        {
            Children =
            {
                cache,
                test
            }
        };
    }

    private async Task TestSshTunnelAsync()
    {
        try
        {
            var options = BuildConnectionFromEditor();
            options.Validate(RequireText(_connectionName, "Connection name"));
            SetStatus("Testing SSH tunnel...");
            var result = await new SshTunnelTester().TestAsync(options);
            SetStatus($"SSH tunnel OK. Local {result.LocalHost}:{result.LocalPort} forwards to {result.RemoteHost}:{result.RemotePort}.");
        }
        catch (Exception ex)
        {
            SetStatus("SSH tunnel test failed. " + BuildDetailedMessage(ex), isError: true);
        }
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            var path = RequireText(_configPath, "Configuration path");
            _configuration = await DatabaseExportConfiguration.LoadAsync(path);
            RefreshPersistedSecretFlags();
            RefreshConnectionLists();
            FillEditorFromSelectedConnection();
            SetStatus($"Loaded configuration: {path}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async Task SaveConfigurationAsync()
    {
        try
        {
            CacheCurrentConnection(showStatus: false);
            var path = RequireText(_configPath, "Configuration path");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, CreateConfigurationForPersistence(), DatabaseExportConfiguration.CreateJsonOptions());
            SetStatus($"Saved configuration: {path}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CacheCurrentConnection(bool showStatus = true)
    {
        try
        {
            var name = RequireText(_connectionName, "Connection name");
            var connection = BuildConnectionFromEditor();
            connection.Validate(name);
            _configuration.Connections[name] = connection;
            if (_savePassword.IsChecked == true)
            {
                _connectionsAllowedToPersistSecrets.Add(name);
            }
            else
            {
                _connectionsAllowedToPersistSecrets.Remove(name);
            }

            RefreshConnectionLists(name);
            if (showStatus)
            {
                SetStatus($"Cached connection: {name}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async Task TestCurrentConnectionAsync()
    {
        try
        {
            CacheCurrentConnection(showStatus: false);
            var name = RequireText(_connectionName, "Connection name");
            var options = _configuration.GetConnection(name);
            if (options.SshTunnel?.Enabled == true)
            {
                SetStatus("Step 1/2: testing SSH tunnel...");
                var tunnelResult = await new SshTunnelTester().TestAsync(options);
                SetStatus($"Step 1/2 OK: SSH tunnel forwards local {tunnelResult.LocalHost}:{tunnelResult.LocalPort} to {tunnelResult.RemoteHost}:{tunnelResult.RemotePort}. Step 2/2: testing database preview...");
            }
            else
            {
                SetStatus("Testing database preview without SSH tunnel...");
            }

            var previews = await _service.PreviewAsync(_configuration, name);
            _previewCache[name] = previews;
            if (string.Equals(_quickConnections.SelectedItem as string, name, StringComparison.OrdinalIgnoreCase))
            {
                ApplyPreview(previews);
            }

            SetStatus($"Connection OK. Tables: {previews.Count}");
        }
        catch (Exception ex)
        {
            SetStatus("Connection test failed. " + BuildDetailedMessage(ex), isError: true);
        }
    }

    private async Task PreviewAsync()
    {
        try
        {
            var connection = RequireSelection(_quickConnections, "Connection");
            SetStatus("Previewing schema...");
            _lastPreview = await _service.PreviewAsync(_configuration, connection);
            _previewCache[connection] = _lastPreview;
            ApplyPreview(_lastPreview);
            SetStatus($"Preview completed. Tables: {_lastPreview.Count}");
        }
        catch (Exception ex)
        {
            SetStatus("Preview failed. " + BuildDetailedMessage(ex), isError: true);
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            var request = new ExportRequest
            {
                ConnectionName = RequireSelection(_quickConnections, "Connection"),
                Scope = ParseScope(RequireSelection(_scope, "Scope")),
                Schema = GetSelectedSchema(),
                Table = GetSelectedTable(),
                Tables = GetSelectedTables(),
                Sql = EmptyToNull(_sql.Text),
                Format = RequireSelection(_format, "Format"),
                OutputPath = RequireText(_output, "Output"),
                Overwrite = _overwrite.IsChecked == true,
                MaxRows = _maxRows.Value is null ? null : decimal.ToInt64(_maxRows.Value.Value)
            };

            SetStatus($"Exporting {DescribeExportTarget(request)}...");
            var summary = await _service.ExportAsync(_configuration, request);
            SetStatus($"Exported {summary.TotalRows} rows to {summary.OutputPath}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private DatabaseConnectionOptions BuildConnectionFromEditor()
    {
        var engine = ParseEngine(RequireSelection(_engine, "Engine"));
        var endpoint = string.IsNullOrWhiteSpace(_rawConnectionString.Text)
            ? new DatabaseEndpointOptions
            {
                Host = RequireText(_host, engine == DatabaseEngine.SQLite ? "SQLite file" : "Host"),
                Port = _port.Value is null || engine == DatabaseEngine.SQLite ? null : decimal.ToInt32(_port.Value.Value),
                Database = EmptyToNull(_database.Text),
                Username = EmptyToNull(_username.Text),
                Password = EmptyToNull(_password.Text),
                IntegratedSecurity = _integratedSecurity.IsChecked == true,
                TrustServerCertificate = true,
                Encrypt = false,
                ReadOnlySqlite = _readOnly.IsChecked == true
            }
            : null;

        return new DatabaseConnectionOptions
        {
            DisplayName = EmptyToNull(_connectionName.Text),
            Engine = engine,
            ProviderInvariantName = RequireText(_providerInvariant, "Provider"),
            FactoryAssembly = EmptyToNull(_factoryAssembly.Text),
            FactoryTypeName = EmptyToNull(_factoryTypeName.Text),
            ConnectionString = EmptyToNull(_rawConnectionString.Text),
            Endpoint = endpoint,
            SshTunnel = BuildSshOptions(),
            CommandTimeoutSeconds = _timeout.Value is null ? 60 : decimal.ToInt32(_timeout.Value.Value),
            ReadOnlyMode = _readOnly.IsChecked == true,
            OpeningIdentifierQuote = engine == DatabaseEngine.SqlServer ? "[" : engine == DatabaseEngine.MySql ? "`" : "\"",
            ClosingIdentifierQuote = engine == DatabaseEngine.SqlServer ? "]" : engine == DatabaseEngine.MySql ? "`" : "\""
        };
    }

    private SshTunnelOptions? BuildSshOptions()
    {
        if (_useSsh.IsChecked != true)
        {
            return null;
        }

        return new SshTunnelOptions
        {
            Enabled = true,
            AuthenticationMode = ParseSshAuthenticationMode(RequireSelection(_sshAuthMode, "SSH authentication")),
            Host = RequireText(_sshHost, "SSH host"),
            Port = _sshPort.Value is null ? 22 : decimal.ToInt32(_sshPort.Value.Value),
            Username = RequireText(_sshUsername, "SSH username"),
            Password = ParseSshAuthenticationMode(RequireSelection(_sshAuthMode, "SSH authentication")) == SshAuthenticationMode.Password ? EmptyToNull(_sshPassword.Text) : null,
            PrivateKeyPath = ParseSshAuthenticationMode(RequireSelection(_sshAuthMode, "SSH authentication")) == SshAuthenticationMode.PrivateKey ? EmptyToNull(_sshKeyPath.Text) : null,
            PrivateKeyPassphrase = ParseSshAuthenticationMode(RequireSelection(_sshAuthMode, "SSH authentication")) == SshAuthenticationMode.PrivateKey ? EmptyToNull(_sshPassphrase.Text) : null,
            LocalPort = _sshLocalPort.Value is null ? 0 : (uint)decimal.ToInt32(_sshLocalPort.Value.Value)
        };
    }

    private void FillEditorFromSelectedConnection()
    {
        if (_quickConnections.SelectedItem is not string name || !_configuration.Connections.TryGetValue(name, out var connection))
        {
            return;
        }

        _connectionName.Text = name;
        _engine.SelectedItem = connection.Engine.ToString();
        _rawConnectionString.Text = connection.ConnectionString;
        _providerInvariant.Text = connection.ProviderInvariantName;
        _factoryAssembly.Text = connection.FactoryAssembly;
        _factoryTypeName.Text = connection.FactoryTypeName;
        _timeout.Value = connection.CommandTimeoutSeconds;
        _readOnly.IsChecked = connection.ReadOnlyMode;
        _savePassword.IsChecked = !string.IsNullOrWhiteSpace(connection.Endpoint?.Password) || !string.IsNullOrWhiteSpace(connection.SshTunnel?.Password);

        _host.Text = connection.Endpoint?.Host;
        _port.Value = connection.Endpoint?.Port;
        _database.Text = connection.Endpoint?.Database;
        _username.Text = connection.Endpoint?.Username;
        _password.Text = connection.Endpoint?.Password ?? "";
        _integratedSecurity.IsChecked = connection.Endpoint?.IntegratedSecurity == true;

        _useSsh.IsChecked = connection.SshTunnel?.Enabled == true;
        _sshAuthMode.SelectedItem = (connection.SshTunnel?.AuthenticationMode ?? SshAuthenticationMode.Password).ToString();
        _sshHost.Text = connection.SshTunnel?.Host;
        _sshPort.Value = connection.SshTunnel?.Port ?? 22;
        _sshUsername.Text = connection.SshTunnel?.Username;
        _sshPassword.Text = connection.SshTunnel?.Password ?? "";
        _sshKeyPath.Text = connection.SshTunnel?.PrivateKeyPath;
        _sshPassphrase.Text = connection.SshTunnel?.PrivateKeyPassphrase ?? "";
        _sshLocalPort.Value = connection.SshTunnel?.LocalPort ?? 0;
        ApplySshUiState();
    }

    private void RefreshPersistedSecretFlags()
    {
        _connectionsAllowedToPersistSecrets.Clear();
        foreach (var pair in _configuration.Connections)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value.Endpoint?.Password)
                || !string.IsNullOrWhiteSpace(pair.Value.SshTunnel?.Password)
                || !string.IsNullOrWhiteSpace(pair.Value.SshTunnel?.PrivateKeyPassphrase))
            {
                _connectionsAllowedToPersistSecrets.Add(pair.Key);
            }
        }
    }

    private DatabaseExportConfiguration CreateConfigurationForPersistence()
    {
        var connections = new Dictionary<string, DatabaseConnectionOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _configuration.Connections)
        {
            connections[pair.Key] = _connectionsAllowedToPersistSecrets.Contains(pair.Key)
                ? pair.Value
                : RedactSecrets(pair.Value);
        }

        return new DatabaseExportConfiguration
        {
            Connections = connections,
            Defaults = _configuration.Defaults
        };
    }

    private static DatabaseConnectionOptions RedactSecrets(DatabaseConnectionOptions connection)
    {
        return new DatabaseConnectionOptions
        {
            DisplayName = connection.DisplayName,
            Engine = connection.Engine,
            ProviderInvariantName = connection.ProviderInvariantName,
            ConnectionString = connection.ConnectionString,
            Endpoint = connection.Endpoint is null ? null : new DatabaseEndpointOptions
            {
                Host = connection.Endpoint.Host,
                Port = connection.Endpoint.Port,
                Database = connection.Endpoint.Database,
                Username = connection.Endpoint.Username,
                Password = null,
                IntegratedSecurity = connection.Endpoint.IntegratedSecurity,
                TrustServerCertificate = connection.Endpoint.TrustServerCertificate,
                Encrypt = connection.Endpoint.Encrypt,
                ReadOnlySqlite = connection.Endpoint.ReadOnlySqlite,
                AdditionalOptions = new Dictionary<string, string>(connection.Endpoint.AdditionalOptions, StringComparer.OrdinalIgnoreCase)
            },
            SshTunnel = connection.SshTunnel is null ? null : new SshTunnelOptions
            {
                Enabled = connection.SshTunnel.Enabled,
                AuthenticationMode = connection.SshTunnel.AuthenticationMode,
                Host = connection.SshTunnel.Host,
                Port = connection.SshTunnel.Port,
                Username = connection.SshTunnel.Username,
                Password = null,
                PrivateKeyPath = connection.SshTunnel.PrivateKeyPath,
                PrivateKeyPassphrase = null,
                LocalHost = connection.SshTunnel.LocalHost,
                LocalPort = connection.SshTunnel.LocalPort
            },
            FactoryAssembly = connection.FactoryAssembly,
            FactoryTypeName = connection.FactoryTypeName,
            CommandTimeoutSeconds = connection.CommandTimeoutSeconds,
            ReadOnlyMode = connection.ReadOnlyMode,
            OpeningIdentifierQuote = connection.OpeningIdentifierQuote,
            ClosingIdentifierQuote = connection.ClosingIdentifierQuote
        };
    }

    private void RefreshConnectionLists(string? selectName = null)
    {
        var names = _configuration.Connections.Keys.OrderBy(x => x).ToArray();
        _quickConnections.ItemsSource = names;

        if (names.Length == 0)
        {
            _quickConnections.SelectedIndex = -1;
            ApplyPreview(Array.Empty<DatabaseTablePreview>());
            return;
        }

        var selected = selectName is null ? 0 : Array.IndexOf(names, selectName);
        if (selected < 0)
        {
            selected = 0;
        }

        _quickConnections.SelectedIndex = selected;
        FillEditorFromSelectedConnection();
        RestorePreviewForSelectedConnection();
    }

    private void ApplyScopeUiState()
    {
        var scope = _scope.SelectedItem as string ?? "table";
        var isDatabase = string.Equals(scope, "database", StringComparison.OrdinalIgnoreCase);
        var isTable = string.Equals(scope, "table", StringComparison.OrdinalIgnoreCase);
        var isTables = string.Equals(scope, "tables", StringComparison.OrdinalIgnoreCase);
        var isQuery = string.Equals(scope, "query", StringComparison.OrdinalIgnoreCase);

        _schema.IsEnabled = isTable || isTables;
        _table.IsEnabled = isTable;
        _tables.IsEnabled = isTable || isTables;
        _sql.IsEnabled = isQuery;

        _tables.SelectionMode = isTables ? SelectionMode.Multiple : SelectionMode.Single;
        if (isDatabase || isQuery)
        {
            _tables.SelectedItems?.Clear();
        }
        else if (isTable && _tables.SelectedItems?.Count > 1)
        {
            var selectedIndex = _tables.SelectedIndex;
            _tables.SelectedItems.Clear();
            if (selectedIndex >= 0)
            {
                _tables.SelectedIndex = selectedIndex;
            }
        }
    }

    private void ApplyEngineDefaults()
    {
        var engine = ParseEngine(_engine.SelectedItem as string ?? "PostgreSql");
        _port.Value = ConnectionStringComposer.GetDefaultPort(engine);
        _providerInvariant.Text = ResolveProvider(engine);
        _factoryAssembly.Text = ResolveFactoryAssembly(engine);
        _factoryTypeName.Text = ResolveFactoryType(engine);
        _integratedSecurity.IsVisible = engine == DatabaseEngine.SqlServer;
        _database.IsEnabled = engine != DatabaseEngine.SQLite;
        _username.IsEnabled = engine != DatabaseEngine.SQLite;
        _password.IsEnabled = engine != DatabaseEngine.SQLite;
        _useSsh.IsEnabled = engine != DatabaseEngine.SQLite;
        if (engine == DatabaseEngine.SQLite)
        {
            _useSsh.IsChecked = false;
        }

        ApplySshUiState();
    }

    private void ApplySshUiState()
    {
        var enabled = _useSsh.IsChecked == true && _useSsh.IsEnabled;
        var mode = ParseSshAuthenticationMode(_sshAuthMode.SelectedItem as string ?? "Password");
        _sshAuthMode.IsEnabled = enabled;
        _sshHost.IsEnabled = enabled;
        _sshPort.IsEnabled = enabled;
        _sshUsername.IsEnabled = enabled;
        _sshLocalPort.IsEnabled = enabled;
        _testSsh.IsEnabled = enabled;
        _sshPassword.IsEnabled = enabled && mode == SshAuthenticationMode.Password;
        _sshKeyPath.IsEnabled = enabled && mode == SshAuthenticationMode.PrivateKey;
        _sshPassphrase.IsEnabled = enabled && mode == SshAuthenticationMode.PrivateKey;
    }

    private void ShowSelectedTable()
    {
        var index = _tables.SelectedIndex;
        if (index < 0 || index >= _visiblePreview.Count)
        {
            _details.Text = "";
            return;
        }

        var table = _visiblePreview[index];
        SelectSchemaAndTable(table.Table);
        ShowTableDetails(table);
        if (_tables.IsEnabled && _tables.SelectedItems?.Count > 1)
        {
            _scope.SelectedItem = "tables";
        }
        else if (_tables.IsEnabled
            && (string.Equals(_scope.SelectedItem as string, "database", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_scope.SelectedItem as string, "query", StringComparison.OrdinalIgnoreCase))
        )
        {
            _scope.SelectedItem = "table";
        }
    }

    private void RestorePreviewForSelectedConnection()
    {
        if (_quickConnections.SelectedItem is string connection && _previewCache.TryGetValue(connection, out var cachedPreview))
        {
            ApplyPreview(cachedPreview);
            return;
        }

        ApplyPreview(Array.Empty<DatabaseTablePreview>());
    }

    private void ApplyPreview(IReadOnlyList<DatabaseTablePreview> previews)
    {
        _lastPreview = previews;
        var schemaItems = previews
            .Select(x => ToSchemaLabel(x.Table.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        _schema.ItemsSource = schemaItems;
        _schema.SelectedIndex = schemaItems.Length > 0 ? 0 : -1;
        RefreshTablesForSelectedSchema();
    }

    private void RefreshTablesForSelectedSchema()
    {
        var selectedSchema = GetSelectedSchema();
        _visiblePreview = _lastPreview
            .Where(x => string.Equals(NormalizeSchema(x.Table.Schema), NormalizeSchema(selectedSchema), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Table.Name)
            .ToArray();

        var tableItems = _visiblePreview.Select(x => x.Table.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _table.ItemsSource = tableItems;
        _table.SelectedIndex = tableItems.Length > 0 ? 0 : -1;
        _visiblePreviewByDisplayText.Clear();
        var displayItems = _visiblePreview.Select(x =>
        {
            var text = $"{x.Table} ({FormatRows(x.RowCount)} rows)";
            _visiblePreviewByDisplayText[text] = x;
            return text;
        }).ToArray();
        _tables.ItemsSource = displayItems;
        _tables.SelectedIndex = _visiblePreview.Count > 0 ? 0 : -1;

        if (_visiblePreview.Count == 0)
        {
            _details.Text = "";
        }
        else
        {
            ShowTableDetails(_visiblePreview[0]);
        }
    }

    private void ShowSelectedDropdownTable()
    {
        var selectedTable = GetSelectedTable();
        if (selectedTable is null)
        {
            return;
        }

        var preview = _visiblePreview.FirstOrDefault(x => string.Equals(x.Table.Name, selectedTable, StringComparison.OrdinalIgnoreCase));
        if (preview is null)
        {
            return;
        }

        var index = FindVisiblePreviewIndex(preview.Table);
        if (index >= 0 && _tables.SelectedIndex != index)
        {
            _tables.SelectedItems?.Clear();
            _tables.SelectedIndex = index;
        }

        if (_table.IsEnabled
            && (string.Equals(_scope.SelectedItem as string, "database", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_scope.SelectedItem as string, "query", StringComparison.OrdinalIgnoreCase))
        )
        {
            _scope.SelectedItem = "table";
        }

        ShowTableDetails(preview);
    }

    private int FindVisiblePreviewIndex(DatabaseTable table)
    {
        for (var i = 0; i < _visiblePreview.Count; i++)
        {
            if (string.Equals(NormalizeSchema(_visiblePreview[i].Table.Schema), NormalizeSchema(table.Schema), StringComparison.OrdinalIgnoreCase)
                && string.Equals(_visiblePreview[i].Table.Name, table.Name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void SelectSchemaAndTable(DatabaseTable table)
    {
        var schemaLabel = ToSchemaLabel(table.Schema);
        if (!string.Equals(_schema.SelectedItem as string, schemaLabel, StringComparison.OrdinalIgnoreCase))
        {
            _schema.SelectedItem = schemaLabel;
        }

        if (!string.Equals(_table.SelectedItem as string, table.Name, StringComparison.OrdinalIgnoreCase))
        {
            _table.SelectedItem = table.Name;
        }
    }

    private void ShowTableDetails(DatabaseTablePreview table)
    {
        var lines = new List<string>
        {
            table.Table.ToString(),
            $"Rows: {FormatRows(table.RowCount)}",
            "",
            "Columns:"
        };

        lines.AddRange(table.Columns.Select(column =>
            $"{column.Name} | {column.DataType} | nullable: {column.IsNullable?.ToString() ?? "unknown"}"));
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    private string? GetSelectedSchema()
    {
        return FromSchemaLabel(_schema.SelectedItem as string);
    }

    private string? GetSelectedTable()
    {
        return _table.SelectedItem as string;
    }

    private IReadOnlyList<ExportTableSelection> GetSelectedTables()
    {
        if (_tables.SelectedItems is null || _tables.SelectedItems.Count == 0)
        {
            var selectedTable = GetSelectedTable();
            return selectedTable is null
                ? Array.Empty<ExportTableSelection>()
                : new[] { new ExportTableSelection(GetSelectedSchema(), selectedTable) };
        }

        var selected = new List<ExportTableSelection>();
        foreach (var item in _tables.SelectedItems)
        {
            if (item is not string text)
            {
                continue;
            }

            if (_visiblePreviewByDisplayText.TryGetValue(text, out var preview))
            {
                selected.Add(new ExportTableSelection(preview.Table.Schema, preview.Table.Name));
            }
        }

        return selected;
    }

    private static string ToSchemaLabel(string? schema)
    {
        return string.IsNullOrWhiteSpace(schema) ? DefaultSchemaLabel : schema;
    }

    private static string? FromSchemaLabel(string? schema)
    {
        return string.IsNullOrWhiteSpace(schema) || schema == DefaultSchemaLabel ? null : schema;
    }

    private static string NormalizeSchema(string? schema)
    {
        return string.IsNullOrWhiteSpace(schema) ? "" : schema;
    }

    private static DatabaseEngine ParseEngine(string value)
    {
        return Enum.Parse<DatabaseEngine>(value, ignoreCase: true);
    }

    private static SshAuthenticationMode ParseSshAuthenticationMode(string value)
    {
        return Enum.Parse<SshAuthenticationMode>(value, ignoreCase: true);
    }

    private static string ResolveProvider(DatabaseEngine engine)
    {
        return engine switch
        {
            DatabaseEngine.SqlServer => "Microsoft.Data.SqlClient",
            DatabaseEngine.PostgreSql => "Npgsql",
            DatabaseEngine.MySql => "MySqlConnector",
            DatabaseEngine.SQLite => "Microsoft.Data.Sqlite",
            _ => ""
        };
    }

    private static string? ResolveFactoryAssembly(DatabaseEngine engine)
    {
        return engine == DatabaseEngine.Custom ? null : ResolveProvider(engine);
    }

    private static string? ResolveFactoryType(DatabaseEngine engine)
    {
        return engine switch
        {
            DatabaseEngine.SqlServer => "Microsoft.Data.SqlClient.SqlClientFactory",
            DatabaseEngine.PostgreSql => "Npgsql.NpgsqlFactory",
            DatabaseEngine.MySql => "MySqlConnector.MySqlConnectorFactory",
            DatabaseEngine.SQLite => "Microsoft.Data.Sqlite.SqliteFactory",
            _ => null
        };
    }

    private static ExportScope ParseScope(string value)
    {
        return value switch
        {
            "database" => ExportScope.Database,
            "table" => ExportScope.Table,
            "tables" => ExportScope.Tables,
            "query" => ExportScope.Query,
            _ => throw new InvalidOperationException($"Unsupported scope '{value}'.")
        };
    }

    private static string DescribeExportTarget(ExportRequest request)
    {
        return request.Scope switch
        {
            ExportScope.Database => "database",
            ExportScope.Table => string.IsNullOrWhiteSpace(request.Schema) ? request.Table ?? "table" : $"{request.Schema}.{request.Table}",
            ExportScope.Tables => $"{request.Tables.Count} selected tables",
            ExportScope.Query => "query result",
            _ => "selection"
        };
    }

    private static WrapPanel Row(params Control[] controls)
    {
        var panel = new WrapPanel();
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    private static Border Field(string label, Control control, double width = double.NaN)
    {
        if (!double.IsNaN(width))
        {
            control.Width = width;
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 8, 8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 12 },
                    control
                }
            }
        };
    }

    private static Control DockRight(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        control.Margin = new Thickness(8, 0, 0, 0);
        return control;
    }

    private static string RequireSelection(ComboBox comboBox, string name)
    {
        return comboBox.SelectedItem as string
            ?? throw new InvalidOperationException($"{name} is required.");
    }

    private static string RequireText(TextBox textBox, string name)
    {
        return string.IsNullOrWhiteSpace(textBox.Text)
            ? throw new InvalidOperationException($"{name} is required.")
            : textBox.Text;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FormatRows(long? rowCount)
    {
        return rowCount?.ToString() ?? "unknown";
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status.Text = message;
        _status.Foreground = isError ? Brushes.Firebrick : Brushes.DarkSlateGray;
        _connectionStatus.Text = message;
        _connectionStatus.Foreground = isError ? Brushes.Firebrick : Brushes.DarkSlateGray;
    }

    private static string BuildDetailedMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" ", messages);
    }
}
