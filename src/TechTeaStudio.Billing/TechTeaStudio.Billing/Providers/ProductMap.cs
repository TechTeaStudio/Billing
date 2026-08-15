namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Lookup helper for the platform-product-to-product-id dictionaries.
///
/// Platform product names contain characters a deployment cannot always express: a Ko-fi
/// tier is literally "Gold Tier", and there is no way to write
/// <c>Billing__Kofi__TierProducts__Gold Tier</c> as an environment variable or a
/// docker-compose key. So the lookup tries the exact key first (case-insensitive, as the
/// dictionaries are configured) and then a normalized comparison that ignores everything
/// except letters and digits - which lets the same mapping be written as
/// <c>Billing__Kofi__TierProducts__GoldTier</c> or <c>..._Gold_Tier</c> in env-only setups.
/// </summary>
internal static class ProductMap
{
    public static bool TryResolve(
        IReadOnlyDictionary<string, string> map, string? platformProduct, out string productId)
    {
        productId = "";
        if (map.Count == 0 || string.IsNullOrWhiteSpace(platformProduct)) return false;

        if (map.TryGetValue(platformProduct, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            productId = exact;
            return true;
        }

        var wanted = Normalize(platformProduct);
        if (wanted.Length == 0) return false;

        foreach (var pair in map)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            if (!string.Equals(Normalize(pair.Key), wanted, StringComparison.Ordinal)) continue;
            productId = pair.Value;
            return true;
        }
        return false;
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) buffer[length++] = char.ToLowerInvariant(c);
        return new string(buffer[..length]);
    }
}
