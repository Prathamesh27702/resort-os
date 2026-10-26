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
}
