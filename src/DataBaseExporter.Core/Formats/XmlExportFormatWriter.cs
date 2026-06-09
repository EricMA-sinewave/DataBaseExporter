using System.Globalization;
using System.Xml;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Core.Formats;

public sealed class XmlExportFormatWriter : IExportFormatWriter
{
    public string Format => "xml";

    public string FileExtension => ".xml";

    public IExportSession CreateSession(Stream output, ExportWriterOptions options)
    {
        return new XmlExportSession(output, options);
    }

    private sealed class XmlExportSession : IExportSession
    {
        private readonly XmlWriter _writer;
        private readonly ExportWriterOptions _options;
        private bool _hasActiveResultSet;

        public XmlExportSession(Stream output, ExportWriterOptions options)
        {
            _options = options;
            _writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                CloseOutput = false
            });
        }

        public async Task BeginAsync(ExportMetadata metadata, CancellationToken cancellationToken = default)
        {
            await _writer.WriteStartDocumentAsync();
            await _writer.WriteStartElementAsync(null, "databaseExport", null);
            await _writer.WriteAttributeStringAsync(null, "generatedAtUtc", null, metadata.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await _writer.WriteAttributeStringAsync(null, "connection", null, metadata.ConnectionName);
            await _writer.WriteAttributeStringAsync(null, "scope", null, metadata.Scope.ToString());
            if (metadata.Source is not null)
            {
                await _writer.WriteAttributeStringAsync(null, "source", null, metadata.Source);
            }

            await _writer.WriteStartElementAsync(null, "resultSets", null);
        }

        public async Task BeginResultSetAsync(ResultSetInfo resultSet, CancellationToken cancellationToken = default)
        {
            if (_hasActiveResultSet)
            {
                throw new InvalidOperationException("A result set is already active.");
            }

            await _writer.WriteStartElementAsync(null, "resultSet", null);
            await _writer.WriteAttributeStringAsync(null, "name", null, resultSet.Name);

            if (_options.IncludeSchema)
            {
                await _writer.WriteStartElementAsync(null, "columns", null);
                foreach (var column in resultSet.Columns)
                {
                    await _writer.WriteStartElementAsync(null, "column", null);
                    await _writer.WriteAttributeStringAsync(null, "name", null, column.Name);
                    await _writer.WriteAttributeStringAsync(null, "dataType", null, column.DataType);
                    if (column.IsNullable is not null)
                    {
                        await _writer.WriteAttributeStringAsync(null, "nullable", null, column.IsNullable.Value.ToString(CultureInfo.InvariantCulture));
                    }

                    if (column.Size is not null)
                    {
                        await _writer.WriteAttributeStringAsync(null, "size", null, column.Size.Value.ToString(CultureInfo.InvariantCulture));
                    }

                    await _writer.WriteEndElementAsync();
                }

                await _writer.WriteEndElementAsync();
            }

            if (resultSet.SourceSql is not null)
            {
                await _writer.WriteElementStringAsync(null, "sourceSql", null, resultSet.SourceSql);
            }

            await _writer.WriteStartElementAsync(null, "rows", null);
            _hasActiveResultSet = true;
        }

        public async Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            await _writer.WriteStartElementAsync(null, "row", null);
            foreach (var pair in row)
            {
                await _writer.WriteStartElementAsync(null, XmlName.Sanitize(pair.Key), null);
                await _writer.WriteAttributeStringAsync(null, "name", null, pair.Key);
                if (pair.Value is null or DBNull)
                {
                    await _writer.WriteAttributeStringAsync(null, "isNull", null, "true");
                }
                else
                {
                    await _writer.WriteStringAsync(FormatScalar(pair.Value));
                }

                await _writer.WriteEndElementAsync();
            }

            await _writer.WriteEndElementAsync();
        }

        public async Task EndResultSetAsync(long rowCount, CancellationToken cancellationToken = default)
        {
            await _writer.WriteEndElementAsync();
            await _writer.WriteElementStringAsync(null, "rowCount", null, rowCount.ToString(CultureInfo.InvariantCulture));
            await _writer.WriteEndElementAsync();
            _hasActiveResultSet = false;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return _writer.FlushAsync();
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            await _writer.WriteEndElementAsync();
            await _writer.WriteEndElementAsync();
            await _writer.WriteEndDocumentAsync();
            await _writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
        }
    }

    internal static string FormatScalar(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }
}

internal static class XmlName
{
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "column";
        }

        var sanitized = new string(name.Select(ch => XmlConvert.IsNCNameChar(ch) ? ch : '_').ToArray());
        if (!XmlConvert.IsStartNCNameChar(sanitized[0]))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }
}
