using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Momos.Host.Data;

/// <summary>
/// Stores a list of immutable value records as one <c>jsonb</c> column. The model's elements are
/// always read and written as a whole with their model version, so they don't earn tables of
/// their own; claims do (each one is addressed and updated individually).
/// </summary>
internal static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static PropertyBuilder<List<T>> HasJsonColumn<T>(this PropertyBuilder<List<T>> property) =>
        property
            .HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<List<T>>(v, Options) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                    v => JsonSerializer.Serialize(v, Options).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, Options), Options)!))
            .HasColumnType("jsonb");
}
