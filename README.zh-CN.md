# DataBaseExporter

泛用 SQL 数据库导出工具。当前实现包含核心库、CLI 和 Avalonia 桌面 GUI。

## 当前能力

- 通过 JSON 配置管理多个本地或远程数据库连接。
- 基于 ADO.NET `DbProviderFactory`，可扩展 SQL Server、PostgreSQL、MySQL、SQLite 等 provider。
- 支持导出范围：全数据库、单表、多表、按主键整合的 Items、自定义只读 SQL 查询。
- 支持导出格式：JSON、XML、XLS。XLS 使用 Excel 可打开的 SpreadsheetML 2003。
- 支持导出前预览：表名、列结构、记录数。
- 默认只允许读取型 SQL，并拒绝多语句和常见破坏性关键字。

## 使用

```powershell
dotnet run --project src\DataBaseExporter.Cli -- preview --config examples\config.sample.json --connection sqlserver
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope database --output exports\db.json --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope table --schema dbo --table Users --output exports\users.xml --format xml --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection mysql --scope items --item-profile examples\item-profile.sample.json --output exports\avatars --format json --max-rows 10 --overwrite
dotnet run --project src\DataBaseExporter.Cli -- export --config examples\config.sample.json --connection sqlserver --scope query --sql "SELECT TOP 100 * FROM dbo.Users" --output exports\query.xls --format xls --overwrite
```

GUI 启动后不会自动载入配置文件。先进入 `Connections` 页面手填连接，或手动载入已有配置让字段自动回填；点击 `Cache / Update Connection` 后，`Quick Export` 页面会使用内存中的连接进行快速预览和导出。首次 `Preview` 成功后会缓存数据库结构，并把 schema/table 填充为下拉框选项，连接切换时会复用已缓存的结构信息。

GUI 默认导出当前选中的单表。需要导出多个表时，在左侧表列表中多选，范围会切换为 `tables`；需要导出全库时，手动将范围切换为 `database`。

`Scope` 是 GUI 的当前模式开关。`query` 模式只使用 SQL 编辑器导出，表格选择会被禁用；`table` 或 `tables` 模式会禁用 SQL，使用表格选择控件；`items` 模式选择一个基础表和 item key 列，输出路径会被当作目录，每个 key 值写一个文件；`database` 模式会禁用表格和 SQL 输入，并导出全数据库。

Items 导出使用关系图。从 root 表读取 key 值后，按配置的下游关系递归查找相关数据，并按每个 root key 生成一个自包含文件。`Max Rows` / `--max-rows` 用于限制测试用的 root key 数量；`0` 或空值表示无限制。该类型不写入 schema 元数据。看起来像 Base64 的字符串值会先解码再写入。

GUI 的关系配置每行一条：

```text
avatar.id -> inventory.avatar_id
inventory.item_id -> item_detail.id
```

GUI 的表主键配置每行一条：

```text
avatar=id
inventory=id
item_detail=id
```

```powershell
dotnet run --project src\DataBaseExporter.Gui
```

## Provider 配置

核心库不硬编码具体数据库驱动。CLI 和 GUI 宿主当前内置引用了常见 ADO.NET provider：

- SQL Server: `Microsoft.Data.SqlClient`
- PostgreSQL: `Npgsql`
- MySQL: `MySqlConnector`
- SQLite: `Microsoft.Data.Sqlite`

连接配置可指定 `factoryAssembly` 和 `factoryTypeName`，宿主会尝试加载对应 factory。新增数据库类型时，在宿主项目添加 provider 包并在配置中增加连接即可。

GUI 支持两种连接输入方式：

- 常规字段：engine、host、port、database、username、password。
- Raw connection string：保留给特殊 provider 或高级参数。

SSH 隧道可在连接页面启用，认证方式可在 `Password` 和 `PrivateKey` 之间切换。启用后工具会建立本地端口转发，再通过本地端口连接远端数据库。

连接页面提供两个测试入口：

- `Test SSH Tunnel`：只测试 SSH 登录和本地端口转发，不读取数据库 schema。
- `Test Preview`：如果启用了 SSH，会先测试 SSH 隧道，再测试数据库连接和 schema 读取。

连接失败时，GUI 会区分 SSH 认证失败、SSH 主机/端口连接失败、私钥加载失败、本地端口转发失败、数据库连接失败和 schema 读取失败。

密码默认不会写入配置文件。勾选 `Save password to configuration` 后才会保存数据库密码、SSH 密码或私钥口令；该文件是明文 JSON，生产环境应使用只读账号并妥善保护配置文件。

## 安全建议

- 生产环境使用只读账号，不复用管理员或写入账号。
- 大表导出时先使用 `preview` 查看记录数，并用 `--max-rows` 验证输出。
- 避免在业务高峰对大表执行无过滤全量导出。
- 优先从只读副本、报表库或备份库导出。
- 自定义 SQL 默认只允许 `SELECT`、`WITH`、`SHOW`、`DESCRIBE`、`EXPLAIN` 等读取型语句。
- 默认拒绝多语句，降低误执行批量 DDL/DML 的风险。

## GUI

GUI 使用 C# + AvaloniaUI。核心导出逻辑集中在 `DataBaseExporter.Core`，Avalonia GUI 只负责：

- 编辑和选择连接配置。
- 展示 schema 预览：数据库、表、列、记录数。
- 配置导出范围、格式、输出路径和最大行数。
- 调用 `DatabaseExportService` 执行导出并展示进度。
