using System.Globalization;
namespace SurfTimer.Storage;

internal sealed record ArchivedValue(string Type, string? Value)
{
    public static ArchivedValue From(object value) => value switch
    {
        DBNull => new("null", null), byte[] bytes => new("binary", Convert.ToBase64String(bytes)),
        DateTime date => new("date", date.ToString("O", CultureInfo.InvariantCulture)),
        _ => new(value.GetType().Name, Convert.ToString(value, CultureInfo.InvariantCulture))
    };
    public object ToValue() => Type switch
    {
        "null" => DBNull.Value, "binary" => Convert.FromBase64String(Value!),
        "date" => DateTime.Parse(Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "UInt64" => ulong.Parse(Value!, CultureInfo.InvariantCulture), "Int64" => long.Parse(Value!, CultureInfo.InvariantCulture),
        "UInt32" => uint.Parse(Value!, CultureInfo.InvariantCulture), "Int32" => int.Parse(Value!, CultureInfo.InvariantCulture),
        "UInt16" => ushort.Parse(Value!, CultureInfo.InvariantCulture), "Int16" => short.Parse(Value!, CultureInfo.InvariantCulture),
        "Byte" => byte.Parse(Value!, CultureInfo.InvariantCulture), "SByte" => sbyte.Parse(Value!, CultureInfo.InvariantCulture),
        "Double" => double.Parse(Value!, CultureInfo.InvariantCulture), "Single" => float.Parse(Value!, CultureInfo.InvariantCulture),
        "Decimal" => decimal.Parse(Value!, CultureInfo.InvariantCulture), "Boolean" => bool.Parse(Value!), "String" => Value!,
        _ => throw new InvalidDataException($"Unsupported archived value type {Type}.")
    };
}
internal sealed record ArchivedTable(string Table, List<Dictionary<string, ArchivedValue>> Rows);


