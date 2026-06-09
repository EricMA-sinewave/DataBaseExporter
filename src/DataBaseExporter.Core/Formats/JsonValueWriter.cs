using System.Globalization;
using System.Text.Json;

namespace DataBaseExporter.Core.Formats;

internal static class JsonValueWriter
{
    public static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                break;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                break;
            case int intValue:
                writer.WriteNumberValue(intValue);
                break;
            case long longValue:
                writer.WriteNumberValue(longValue);
                break;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                break;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                break;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
