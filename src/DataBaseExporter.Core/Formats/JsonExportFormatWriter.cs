using System.Text.Json;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Core.Formats;

public sealed class JsonExportFormatWriter : IExportFormatWriter
{
    public string Format => "json";

    public string FileExtension => ".json";

    public IExportSession CreateSession(Stream output, ExportWriterOptions options)
    {
        return new JsonExportSession(output, options);
    }

    private sealed class JsonExportSession : IExportSession
    {
        private readonly Utf8JsonWriter _writer;
        private readonly ExportWriterOptions _options;
        private bool _hasActiveResultSet;
        private bool _hasWrittenRow;

        public JsonExportSession(Stream output, ExportWriterOptions options)
        {
            _options = options;
            _writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        }

        public Task BeginAsync(ExportMetadata metadata, CancellationToken cancellationToken = default)
        {
            _writer.WriteStartObject();
            _writer.WriteString("generatedAtUtc", metadata.GeneratedAtUtc);
            _writer.WriteString("connection", metadata.ConnectionName);
            _writer.WriteString("scope", metadata.Scope.ToString());
            if (metadata.Source is not null)
            {
                _writer.WriteString("source", metadata.Source);
            }

            _writer.WriteStartArray("resultSets");
            return FlushAsync(cancellationToken);
        }

        public Task BeginResultSetAsync(ResultSetInfo resultSet, CancellationToken cancellationToken = default)
        {
            if (_hasActiveResultSet)
            {
                throw new InvalidOperationException("A result set is already active.");
            }

            _writer.WriteStartObject();
            _writer.WriteString("name", resultSet.Name);
            if (_options.IncludeSchema)
            {
                _writer.WritePropertyName("columns");
                WriteColumns(resultSet.Columns);
            }

            if (resultSet.SourceSql is not null)
            {
                _writer.WriteString("sourceSql", resultSet.SourceSql);
            }

            _writer.WriteStartArray("rows");
            _hasActiveResultSet = true;
            _hasWrittenRow = false;
            return FlushAsync(cancellationToken);
        }

        public Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            if (!_hasActiveResultSet)
            {
                throw new InvalidOperationException("No result set is active.");
            }

            _writer.WriteStartObject();
            foreach (var pair in row)
            {
                _writer.WritePropertyName(pair.Key);
                JsonValueWriter.WriteValue(_writer, pair.Value);
            }

            _writer.WriteEndObject();
            _hasWrittenRow = true;
            return _hasWrittenRow ? Task.CompletedTask : FlushAsync(cancellationToken);
        }

        public Task EndResultSetAsync(long rowCount, CancellationToken cancellationToken = default)
        {
            _writer.WriteEndArray();
            _writer.WriteNumber("rowCount", rowCount);
            _writer.WriteEndObject();
            _hasActiveResultSet = false;
            return FlushAsync(cancellationToken);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_hasActiveResultSet)
            {
                throw new InvalidOperationException("Cannot complete while a result set is active.");
            }

            _writer.WriteEndArray();
            _writer.WriteEndObject();
            await FlushAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
        }

        private void WriteColumns(IReadOnlyList<DatabaseColumn> columns)
        {
            // Column metadata stays compact because row data is often the dominant output size.
            _writer.WriteStartArray();
            foreach (var column in columns)
            {
                _writer.WriteStartObject();
                _writer.WriteString("name", column.Name);
                _writer.WriteString("dataType", column.DataType);
                if (column.IsNullable is not null)
                {
                    _writer.WriteBoolean("nullable", column.IsNullable.Value);
                }

                if (column.Size is not null)
                {
                    _writer.WriteNumber("size", column.Size.Value);
                }

                _writer.WriteEndObject();
            }

            _writer.WriteEndArray();
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return _writer.FlushAsync(cancellationToken);
        }
    }
}
