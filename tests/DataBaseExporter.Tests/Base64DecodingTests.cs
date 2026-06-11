using System.Reflection;
using DataBaseExporter.Core.Exporting;

namespace DataBaseExporter.Tests;

[TestClass]
public sealed class Base64DecodingTests
{
    [TestMethod]
    public void DecodePossibleBase64_DecodesExplicitPrefix()
    {
        var decoded = Decode("BASE64__SGVsbG8=");

        Assert.AreEqual("Hello", decoded);
    }

    [TestMethod]
    public void DecodePossibleBase64_DecodesShortExplicitPrefixPayload()
    {
        var decoded = Decode("BASE64__MQ==");

        Assert.AreEqual("1", decoded);
    }

    [TestMethod]
    public void DecodePossibleBase64_KeepsInvalidExplicitPrefixValue()
    {
        var value = "BASE64__not base64";

        var decoded = Decode(value);

        Assert.AreEqual(value, decoded);
    }

    private static object? Decode(object? value)
    {
        var method = typeof(DatabaseExportService).GetMethod(
            "DecodePossibleBase64",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        return method.Invoke(null, new[] { value });
    }
}
