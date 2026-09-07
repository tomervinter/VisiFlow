using System.Text.Json;
using VisiFlow.Data;
using VisiFlow.Data.Entities;

// No namespace - matches VisitPlanGenerator.cs/VisitPlanCityOptimizer.cs/Program.cs's global namespace.

/// <summary>
/// Wraps Google's Geocoding API (address/city text -> lat/lng) to fill in Customer.Latitude/Longitude,
/// which RouteOptimizationService later needs to order a day's visits into an efficient walking route.
///
/// Reads its key from the GOOGLE_MAPS_API_KEY environment variable - if it's unset (no key supplied
/// yet), every method here is a silent no-op (IsConfigured false, GeocodeMissingAsync returns 0
/// immediately) rather than throwing, so the customer-import flow that calls this keeps working exactly
/// as it did before this feature existed. Once a real key is set (locally or as a Render environment
/// variable), geocoding activates automatically on the next import - no code change needed.
/// </summary>
public class GeocodingService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public GeocodingService(HttpClient http)
    {
        _http = http;
        _apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>Geocodes every customer in the given set that doesn't already have coordinates and has
    /// an address to try - called once at the end of a customer import, so a re-upload of the same
    /// address never re-spends an API call on it. Returns how many customers were actually updated.</summary>
    public async Task<int> GeocodeMissingAsync(VisiFlowDbContext db, IEnumerable<Customer> customers)
    {
        if (!IsConfigured) return 0;
        var updated = 0;
        foreach (var customer in customers)
        {
            if (customer.Latitude.HasValue || string.IsNullOrWhiteSpace(customer.Address)) continue;
            var fullAddress = string.Join(", ", new[] { customer.Address, customer.City, "ישראל" }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            var coords = await GeocodeAsync(fullAddress);
            if (coords == null) continue;
            customer.Latitude = coords.Value.Latitude;
            customer.Longitude = coords.Value.Longitude;
            updated++;
        }
        if (updated > 0) await db.SaveChangesAsync();
        return updated;
    }

    /// <summary>Single-address lookup. Never throws - a bad address, a network hiccup, or a
    /// zero-results response all just come back null so the caller can move on to the next customer
    /// instead of aborting the whole import over one unresolvable address.</summary>
    public async Task<(decimal Latitude, decimal Longitude)?> GeocodeAsync(string address)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(address)}&key={_apiKey}";
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var statusEl) || statusEl.GetString() != "OK") return null;
            var results = root.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;
            var location = results[0].GetProperty("geometry").GetProperty("location");
            return ((decimal)location.GetProperty("lat").GetDouble(), (decimal)location.GetProperty("lng").GetDouble());
        }
        catch
        {
            return null;
        }
    }
}
