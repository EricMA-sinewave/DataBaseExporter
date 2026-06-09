using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Core.Formats;

public interface IExportFormatWriter
{
    string Format { get; }

    string FileExtension { get; }

    IExportSession CreateSession(Stream output, ExportWriterOptions options);
}

public interface IExportSession : IAsyncDisposable
{
    Task BeginAsync(ExportMetadata metadata, CancellationToken cancellationToken = default);

    Task BeginResultSetAsync(ResultSetInfo resultSet, CancellationToken cancellationToken = default);

    Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default);

    Task EndResultSetAsync(long rowCount, CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record ExportWriterOptions(bool IncludeSchema);
