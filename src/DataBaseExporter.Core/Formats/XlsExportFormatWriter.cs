using System.Globalization;
using System.Xml;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Core.Formats;

public sealed class XlsExportFormatWriter : IExportFormatWriter
{
    public string Format => "xls";

    public string FileExtension => ".xls";

    public IExportSession CreateSession(Stream output, ExportWriterOptions options)
    {
        return new XlsExportSession(output, options);
    }

    private sealed class XlsExportSession : IExportSession
    {
        private const string SpreadsheetNamespace = "urn:schemas-microsoft-com:office:spreadsheet";
        private readonly XmlWriter _writer;
        private ResultSetInfo? _activeResultSet;
        private long _sheetIndex;

        public XlsExportSession(Stream output, ExportWriterOptions options)
        {
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
            await _writer.WriteProcessingInstructionAsync("mso-application", "progid=\"Excel.Sheet\"");
            await _writer.WriteStartElementAsync(null, "Workbook", SpreadsheetNamespace);
            await _writer.WriteAttributeStringAsync("xmlns", "ss", null, SpreadsheetNamespace);
            await _writer.WriteStartElementAsync(null, "DocumentProperties", "urn:schemas-microsoft-com:office:office");
            await _writer.WriteElementStringAsync(null, "Created", null, metadata.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await _writer.WriteEndElementAsync();
        }

        public async Task BeginResultSetAsync(ResultSetInfo resultSet, CancellationToken cancellationToken = default)
        {
            if (_activeResultSet is not null)
            {
                throw new InvalidOperationException("A worksheet is already active.");
            }

            _activeResultSet = resultSet;
            _sheetIndex++;

            await _writer.WriteStartElementAsync(null, "Worksheet", SpreadsheetNamespace);
            await _writer.WriteAttributeStringAsync("ss", "Name", SpreadsheetNamespace, BuildWorksheetName(resultSet.Name, _sheetIndex));
            await _writer.WriteStartElementAsync(null, "Table", SpreadsheetNamespace);
            await WriteHeaderAsync(resultSet.Columns);
        }

        public async Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            if (_activeResultSet is null)
            {
                throw new InvalidOperationException("No worksheet is active.");
            }

            await _writer.WriteStartElementAsync(null, "Row", SpreadsheetNamespace);
            foreach (var column in _activeResultSet.Columns)
            {
                row.TryGetValue(column.Name, out var value);
                await WriteCellAsync(value);
            }

            await _writer.WriteEndElementAsync();
        }

        public async Task EndResultSetAsync(long rowCount, CancellationToken cancellationToken = default)
        {
            await _writer.WriteEndElementAsync();
            await _writer.WriteEndElementAsync();
            _activeResultSet = null;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return _writer.FlushAsync();
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            await _writer.WriteEndElementAsync();
            await _writer.WriteEndDocumentAsync();
            await _writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
        }

        private async Task WriteHeaderAsync(IReadOnlyList<Database.DatabaseColumn> columns)
        {
            await _writer.WriteStartElementAsync(null, "Row", SpreadsheetNamespace);
            foreach (var column in columns)
            {
                await WriteCellAsync(column.Name);
            }

            await _writer.WriteEndElementAsync();
        }

        private async Task WriteCellAsync(object? value)
        {
            await _writer.WriteStartElementAsync(null, "Cell", SpreadsheetNamespace);
            await _writer.WriteStartElementAsync(null, "Data", SpreadsheetNamespace);
            await _writer.WriteAttributeStringAsync("ss", "Type", SpreadsheetNamespace, GetSpreadsheetType(value));
            if (value is not null and not DBNull)
            {
                await _writer.WriteStringAsync(XmlExportFormatWriter.FormatScalar(value));
            }

            await _writer.WriteEndElementAsync();
            await _writer.WriteEndElementAsync();
        }

        private static string GetSpreadsheetType(object? value)
        {
            return value switch
            {
                null or DBNull => "String",
                byte or short or int or long or float or double or decimal => "Number",
                bool => "Boolean",
                DateTime or DateTimeOffset => "DateTime",
                _ => "String"
            };
        }

        private static string BuildWorksheetName(string value, long index)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '\\', '/', '?', '*', '[', ']' };
            var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = $"Sheet{index}";
            }

            return sanitized.Length <= 31 ? sanitized : sanitized[..31];
        }
    }
}
