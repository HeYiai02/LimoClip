using System.Globalization;

namespace LiveMonitor.Data;

internal static class SqliteValues
{
    public static string ToDb(Guid value) => value.ToString("D");

    public static Guid ToGuid(string value) => Guid.Parse(value);

    public static string ToDb(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTime ToDateTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static object NullableText(string? value) => value is null ? DBNull.Value : value;
}
