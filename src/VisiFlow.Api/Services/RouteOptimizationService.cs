using System.Text;
using System.Text.Json;
using VisiFlow.Data;
using VisiFlow.Data.Entities;

// No namespace - matches VisitPlanGenerator.cs/VisitPlanCityOptimizer.cs/Program.cs's global namespace.

/// <summary>
/// Orders each agent+day group of an already-generated (and already city-optimized) visit plan into an
/// efficient driving route, via Google's Routes API (computeRouteMatrix) plus a nearest-neighbor
/// heuristic - not a full TSP solver, which would cost more API quota than a daily route of a handful
/// to ~15 stops justifies. Writes VisitPlanEntry.VisitOrder (1 = first stop of the day).
///
/// Reads its key from GOOGLE_MAPS_API_KEY (same variable GeocodingService uses - one Google Cloud
/// project with both Geocoding API and Routes API enabled covers both). Unset key or unresolvable
/// distances -> silent no-op on the affected group, same graceful-degradation pattern as
/// GeocodingService: a plan without route ordering is exactly what the app has always produced, so
/// nothing regresses while the key is missing.
///
/// NOTE: built against Google's documented computeRouteMatrix request/response shape but not yet
/// exercised against a real API key/quota - when a real GOOGLE_MAPS_API_KEY is supplied, re-verify the
/// parsed response shape against one real call before relying on this in production.
/// </summary>
public class RouteOptimizationService
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public RouteOptimizationService(HttpClient http)
    {
        _http = http;
        _apiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>Sets VisitOrder on every entry in `entries` that has PlannedDate + a geocoded customer,
    /// grouped by (AgentName, PlannedDate). Returns how many entries got an order assigned.</summary>
    public async Task<int> OrderVisitsAsync(VisiFlowDbContext db, List<VisitPlanEntry> entries, Dictionary<string, Customer> customersByNumber)
    {
        if (!IsConfigured) return 0;
        var ordered = 0;

        var groups = entries
            .Where(e => e.PlannedDate.HasValue && !string.IsNullOrWhiteSpace(e.AgentName))
            .GroupBy(e => (e.AgentName, Date: e.PlannedDate!.Value.Date));

        foreach (var group in groups)
        {
            var stops = group
                .Select(e => (Entry: e, Customer: customersByNumber.GetValueOrDefault(e.CustomerNumber)))
                .Where(x => x.Customer?.Latitude != null && x.Customer?.Longitude != null)
                .ToList();
            if (stops.Count == 0) continue;
            if (stops.Count == 1) { stops[0].Entry.VisitOrder = 1; ordered++; continue; }

            var points = stops.Select(s => (s.Customer!.Latitude!.Value, s.Customer.Longitude!.Value)).ToList();
            var order = await NearestNeighborOrderAsync(points);
            if (order == null) continue; // matrix lookup failed - leave this group's VisitOrder untouched

            for (var i = 0; i < order.Count; i++)
            {
                stops[order[i]].Entry.VisitOrder = i + 1;
                ordered++;
            }
        }

        if (ordered > 0) await db.SaveChangesAsync();
        return ordered;
    }

    /// <summary>Greedy nearest-neighbor tour starting from stop 0 (arbitrary - the plan has no fixed
    /// "home base" per agent) over a real driving-distance matrix. Good enough for a single day's route
    /// (a handful to ~15 stops); not a shortest-tour guarantee.</summary>
    private async Task<List<int>?> NearestNeighborOrderAsync(List<(decimal Lat, decimal Lng)> points)
    {
        var matrix = await FetchDistanceMatrixAsync(points);
        if (matrix == null) return null;

        var n = points.Count;
        var visited = new bool[n];
        var order = new List<int> { 0 };
        visited[0] = true;

        for (var step = 1; step < n; step++)
        {
            var last = order[^1];
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var candidate = 0; candidate < n; candidate++)
            {
                if (visited[candidate]) continue;
                var distance = matrix[last, candidate];
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            if (best == -1) return null; // every remaining distance came back unresolved
            order.Add(best);
            visited[best] = true;
        }
        return order;
    }

    private async Task<double[,]?> FetchDistanceMatrixAsync(List<(decimal Lat, decimal Lng)> points)
    {
        try
        {
            var waypoints = points.Select(p => new
            {
                waypoint = new { location = new { latLng = new { latitude = (double)p.Lat, longitude = (double)p.Lng } } }
            }).ToList();

            var body = JsonSerializer.Serialize(new
            {
                origins = waypoints,
                destinations = waypoints,
                travelMode = "DRIVE"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/distanceMatrix/v2:computeRouteMatrix")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Goog-Api-Key", _apiKey);
            request.Headers.Add("X-Goog-FieldMask", "originIndex,destinationIndex,distanceMeters,condition");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var n = points.Count;
            var matrix = new double[n, n];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    matrix[i, j] = i == j ? 0 : double.MaxValue;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("originIndex", out var oEl) || !element.TryGetProperty("destinationIndex", out var dEl)) continue;
                if (element.TryGetProperty("condition", out var condEl) && condEl.GetString() != "ROUTE_EXISTS") continue;
                if (!element.TryGetProperty("distanceMeters", out var distEl)) continue;
                matrix[oEl.GetInt32(), dEl.GetInt32()] = distEl.GetDouble();
            }
            return matrix;
        }
        catch
        {
            return null;
        }
    }
}
