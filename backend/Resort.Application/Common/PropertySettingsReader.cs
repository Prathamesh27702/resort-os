using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Resort.Application.Common;

/// <summary>Reads property_settings values that code gives a default for.</summary>
public static class PropertySettingsReader
{
    /// <summary>A boolean property setting (true/false, or {"enabled": …}), or <paramref name="fallback"/> when it isn't set.</summary>
    public static async Task<bool> BoolAsync(IAppDbContext db, Guid propertyId, string key, bool fallback, CancellationToken ct)
    {
        var json = await db.PropertySettings.AsNoTracking().Where(s => s.PropertyId == propertyId && s.Key == key).Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        if (json is null) return fallback;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object when doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False => e.GetBoolean(),
            _ => fallback,
        };
    }

    /// <summary>A number property setting (10000, or {"amount": 10000}), or <paramref name="fallback"/> when it isn't set
    /// or isn't a number.</summary>
    public static async Task<decimal> DecimalAsync(IAppDbContext db, Guid propertyId, string key, decimal fallback, CancellationToken ct)
    {
        var json = await db.PropertySettings.AsNoTracking().Where(s => s.PropertyId == propertyId && s.Key == key).Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        if (json is null) return fallback;
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("amount", out var a) ? a : doc.RootElement;
        return e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var v) && v >= 0 ? v : fallback;
    }
}
