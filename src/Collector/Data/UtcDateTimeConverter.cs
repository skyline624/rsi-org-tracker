using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Collector.Data;

/// <summary>
/// Every date is written in UTC, and SQLite stores it as text without a zone: dates read
/// back are marked UTC again. Unmarked, the API serialized them without "Z" and browsers
/// took them for local time.
/// </summary>
public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
