using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DataBaseExporter.Core.Database;
using DataBaseExporter.Core.Exporting;
using DataBaseExporter.Core.Formats;

namespace DataBaseExporter.Tests;

[TestClass]
public sealed class ExportFormatWriterTests
{
    [TestMethod]
    public async Task JsonWriter_WritesValidExportDocument()
    {
        await using var stream = new MemoryStream();
        var writer = new JsonExportFormatWriter();

        await WriteSampleAsync(writer, stream);

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.AreEqual("test", document.RootElement.GetProperty("connection").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("resultSets")[0].GetProperty("rowCount").GetInt32());
        Assert.AreEqual("Alice", document.RootElement.GetProperty("resultSets")[0].GetProperty("rows")[0].GetProperty("Name").GetString());
    }

    [TestMethod]
    public async Task XmlWriter_WritesValidExportDocument()
    {
        await using var stream = new MemoryStream();
        var writer = new XmlExportFormatWriter();

        await WriteSampleAsync(writer, stream);

        stream.Position = 0;
        var xml = XDocument.Load(stream);
        Assert.AreEqual("databaseExport", xml.Root!.Name.LocalName);
        Assert.AreEqual("Alice", xml.Descendants("Name").Single().Value);
    }

    [TestMethod]
    public async Task XlsWriter_WritesSpreadsheetWorkbook()
    {
        await using var stream = new MemoryStream();
        var writer = new XlsExportFormatWriter();

        await WriteSampleAsync(writer, stream);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        StringAssert.Contains(text, "Workbook");
        StringAssert.Contains(text, "Alice");
        StringAssert.Contains(text, "Worksheet");
    }

    private static async Task WriteSampleAsync(IExportFormatWriter writer, Stream stream)
    {
        await using var session = writer.CreateSession(stream, new ExportWriterOptions(IncludeSchema: true));
        await session.BeginAsync(new ExportMetadata("test", ExportScope.Table, "Users", DateTimeOffset.UnixEpoch, IncludeSchema: true));
        await session.BeginResultSetAsync(
            new ResultSetInfo(
                "Users",
                new[]
                {
                    new DatabaseColumn("Id", "int", false, null),
                    new DatabaseColumn("Name", "nvarchar", true, 100)
                },
                "SELECT * FROM Users"));

        await session.WriteRowAsync(new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice"
        });
        await session.EndResultSetAsync(1);
        await session.CompleteAsync();
    }
}
