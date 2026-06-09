using DataBaseExporter.Core.Formats;

namespace DataBaseExporter.Core.Exporting;

public sealed class ExportFormatRegistry
{
    private readonly Dictionary<string, IExportFormatWriter> _writers;

    public ExportFormatRegistry(IEnumerable<IExportFormatWriter> writers)
    {
        _writers = writers.ToDictionary(x => x.Format, StringComparer.OrdinalIgnoreCase);
    }

    public static ExportFormatRegistry CreateDefault()
    {
        return new ExportFormatRegistry(new IExportFormatWriter[]
        {
            new JsonExportFormatWriter(),
            new XmlExportFormatWriter(),
            new XlsExportFormatWriter()
        });
    }

    public IExportFormatWriter GetRequired(string format)
    {
        if (_writers.TryGetValue(format, out var writer))
        {
            return writer;
        }

        var available = string.Join(", ", _writers.Keys.OrderBy(x => x));
        throw new InvalidOperationException($"Export format '{format}' is not registered. Available formats: {available}.");
    }
}
