using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LessonCue.Server;

public sealed class SignageWidgetService(IServiceScopeFactory scopeFactory, IHttpClientFactory clients,
    IHubContext<SyncHub> hub, SignageCredentialStore credentials, ILogger<SignageWidgetService> logger) : BackgroundService
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RefreshAsync(null, false, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Signage widget refresh failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RefreshAsync(Guid? signageId, bool force, CancellationToken cancellationToken)
    {
        if (!await refreshLock.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LessonCueDb>();
            var organization = await db.Organizations.AsNoTracking().OrderBy(item => item.Id).FirstAsync(cancellationToken);
            var allowlist = SignageLayout.ParseAllowlist(organization.SignageSourceAllowlistJson);
            var query = db.SignagePlaylists.Where(x => x.Enabled);
            if (signageId is { } id) query = query.Where(x => x.Id == id);
            var signage = await query.ToListAsync(cancellationToken);
            var layoutIds = signage.Where(x => x.LayoutId is not null).Select(x => x.LayoutId!.Value).Distinct().ToArray();
            var layouts = await db.SignageLayouts.AsNoTracking().Where(x => layoutIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            var changed = 0;
            foreach (var sign in signage)
            {
                var zones = sign.LayoutId is { } layoutId && layouts.TryGetValue(layoutId, out var layout)
                    ? SignageLayout.ParseZones(layout.PublishedZonesJson)
                    : SignageLayout.ParseZones(sign.ZonesJson);
                var cache = SignageLayout.ParseCache(sign.WidgetCacheJson).ToDictionary(x => x.ZoneId, StringComparer.OrdinalIgnoreCase);
                var errors = new List<string>();
                var signChanged = false;
                foreach (var rawZone in zones.Where(x => x.Type is "calendar" or "weather" or "menu" or "rss" or "data" or "social" or "traffic")
                             .Where(x => !string.IsNullOrWhiteSpace(x.SourceUrl) ||
                                 x.Type == "weather" && SignageLayout.Normalize(x).WeatherProvider is "open-meteo" or "nws"))
                {
                    var zone = SignageLayout.Normalize(rawZone);
                    if (zone.WeatherProvider is not ("open-meteo" or "nws") &&
                        !SignageLayout.IsBuiltInPublicSource(zone) &&
                        (!SignageLayout.TryOrigin(zone.SourceUrl!, out var origin) || !allowlist.Contains(origin, StringComparer.OrdinalIgnoreCase)))
                    {
                        errors.Add($"{zone.Title ?? zone.Type}: source is no longer approved");
                        continue;
                    }
                    if (!force && cache.TryGetValue(zone.Id, out var existing) &&
                        existing.RefreshedAt.AddMinutes(Math.Clamp(zone.RefreshMinutes, 5, 1440)) > DateTimeOffset.UtcNow) continue;
                    try
                    {
                        cache[zone.Id] = await FetchAsync(zone, cancellationToken);
                        signChanged = true;
                    }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or XmlException)
                    {
                        errors.Add($"{zone.Title ?? zone.Type}: {Short(error.Message)}");
                    }
                }
                var errorText = errors.Count == 0 ? null : string.Join(" · ", errors);
                if (signChanged || sign.WidgetCacheError != errorText)
                {
                    sign.WidgetCacheJson = SignageLayout.StoreCache(cache.Values);
                    if (signChanged) sign.WidgetCacheUpdatedAt = DateTimeOffset.UtcNow;
                    sign.WidgetCacheError = errorText;
                    changed++;
                }
            }
            if (changed > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                await hub.Clients.Group("admins").SendAsync("ManifestInvalidated", new { version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, cancellationToken);
            }
            return changed;
        }
        finally { refreshLock.Release(); }
    }

    public async Task<SignageWidgetCacheEntry> FetchAsync(SignageZoneInput zone, CancellationToken cancellationToken)
    {
        zone = await ResolveWeatherLocationAsync(SignageLayout.Normalize(zone), cancellationToken);
        var source = WeatherSource(zone);
        if (source is null && zone.Type == "weather" && zone.WeatherProvider is ("open-meteo" or "nws"))
            throw new InvalidDataException("Preset weather elements require a postal code or valid coordinates.");
        source ??= zone.SourceUrl ?? throw new InvalidDataException("Widget source is missing.");
        var text = await FetchTextAsync(source, zone.WeatherProvider == "custom" ? zone.CredentialKey : null, cancellationToken);
        if (zone.Type == "weather" && zone.WeatherProvider == "nws")
        {
            using var points = JsonDocument.Parse(text);
            var forecast = points.RootElement.GetProperty("properties").GetProperty("forecast").GetString();
            if (!Uri.TryCreate(forecast, UriKind.Absolute, out var forecastUri) ||
                forecastUri.Scheme != "https" || !forecastUri.Host.Equals("api.weather.gov", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("National Weather Service returned an invalid forecast address.");
            source = forecastUri.AbsoluteUri;
            text = await FetchTextAsync(source, null, cancellationToken);
        }
        return Parse(zone with { SourceUrl = source }, text, DateTimeOffset.UtcNow);
    }

    private async Task<string> FetchTextAsync(string source, string? credentialKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (request.RequestUri?.Host.Equals("api.weather.gov", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.UserAgent.ParseAdd("LessonCue/0.36 (+https://github.com/ProbablyModded/lessoncue)");
            request.Headers.Accept.ParseAdd("application/geo+json");
        }
        credentials.Apply(request, credentialKey);
        using var response = await clients.CreateClient("signage-widgets").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new InvalidDataException("Source response exceeds 2 MB.");
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length > 2_000_000) throw new InvalidDataException("Source response exceeds 2 MB.");
        return text;
    }

    private async Task<SignageZoneInput> ResolveWeatherLocationAsync(SignageZoneInput zone,
        CancellationToken cancellationToken)
    {
        if (zone.Type != "weather" || zone.WeatherProvider is not ("open-meteo" or "nws") ||
            zone.WeatherLatitude is not null && zone.WeatherLongitude is not null ||
            string.IsNullOrWhiteSpace(zone.WeatherPostalCode)) return zone;
        var source = "https://geocoding-api.open-meteo.com/v1/search" +
            $"?name={Uri.EscapeDataString(zone.WeatherPostalCode.Trim())}&count=1&language=en&format=json";
        var payload = await FetchTextAsync(source, null, cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException($"No weather location matched postal code {zone.WeatherPostalCode}.");
        var match = results[0];
        var latitude = NumberProperty(match, "latitude");
        var longitude = NumberProperty(match, "longitude");
        if (latitude is null || longitude is null)
            throw new InvalidDataException("The weather geocoding service did not return valid coordinates.");
        if (zone.WeatherProvider == "nws" &&
            !string.Equals(StringProperty(match, "country_code"), "US", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("National Weather Service forecasts require a United States postal code.");
        var location = zone.WeatherLocation;
        if (string.IsNullOrWhiteSpace(location))
        {
            var name = StringProperty(match, "name");
            var region = StringProperty(match, "admin1");
            location = string.Join(", ", new[] { name, region }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        return zone with { WeatherLatitude = latitude, WeatherLongitude = longitude, WeatherLocation = location };
    }

    public static string? WeatherSource(SignageZoneInput raw)
    {
        var zone = SignageLayout.Normalize(raw);
        if (zone.Type != "weather" || zone.WeatherLatitude is null || zone.WeatherLongitude is null) return null;
        var latitude = zone.WeatherLatitude.Value.ToString("0.####", CultureInfo.InvariantCulture);
        var longitude = zone.WeatherLongitude.Value.ToString("0.####", CultureInfo.InvariantCulture);
        if (zone.WeatherProvider == "nws") return $"https://api.weather.gov/points/{latitude},{longitude}";
        if (zone.WeatherProvider != "open-meteo") return null;
        var temperatureUnit = zone.WeatherUnits == "celsius" ? "celsius" : "fahrenheit";
        var windUnit = zone.WeatherUnits == "celsius" ? "kmh" : "mph";
        return "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={latitude}&longitude={longitude}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
            $"&temperature_unit={temperatureUnit}&wind_speed_unit={windUnit}&timezone=auto&forecast_days=2";
    }

    public static SignageWidgetCacheEntry Parse(SignageZoneInput zone, string payload, DateTimeOffset refreshedAt)
    {
        var title = zone.Title ?? zone.Type.ToUpperInvariant();
        var text = zone.Content ?? "";
        var items = Array.Empty<string>();
        if (zone.Type == "rss")
        {
            var document = XDocument.Parse(payload);
            var channel = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "channel");
            title = zone.Title ?? Clean(channel?.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value) ?? title;
            items = document.Descendants().Where(x => x.Name.LocalName is "item" or "entry")
                .Select(x => Clean(x.Elements().FirstOrDefault(y => y.Name.LocalName == "title")?.Value))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).Cast<string>().ToArray();
        }
        else if (zone.Type == "calendar")
        {
            var events = ParseCalendarEvents(payload, refreshedAt).Take(24).ToArray();
            items = events.Select(value => value.Title).ToArray();
            if (items.Length == 0) items = ParseJsonItems(payload);
            return new(zone.Id, title, Clean(text) ?? "", items, refreshedAt, zone.SourceUrl,
                Events: events.Length == 0 ? null : events);
        }
        else if (zone.Type == "weather" && zone.WeatherProvider is "open-meteo" or "nws")
        {
            using var document = JsonDocument.Parse(payload);
            return ParsePresetWeather(zone, document.RootElement, refreshedAt);
        }
        else if (zone.Type is "weather" or "data" or "social" or "traffic")
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            title = zone.Title ?? StringProperty(root, "title") ?? StringProperty(root, "name") ?? title;
            text = StringProperty(root, "summary") ?? StringProperty(root, "message") ?? StringProperty(root, "text") ?? WeatherText(root) ?? text;
            items = JsonItems(root);
        }
        else
        {
            items = payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).Cast<string>().ToArray();
        }
        return new(zone.Id, title, Clean(text) ?? "", items, refreshedAt, zone.SourceUrl);
    }

    private static SignageWidgetCacheEntry ParsePresetWeather(SignageZoneInput zone, JsonElement root, DateTimeOffset refreshedAt)
    {
        var fields = (zone.WeatherFields ?? "icon,temperature,conditions,precipitation,high,low,wind").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(SignageLayout.WeatherDisplayFields.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var title = zone.WeatherLocation ?? zone.Title ?? "Local weather";
        double? temperature = null, feelsLike = null, high = null, low = null, precipitation = null, humidity = null, wind = null;
        string? temperatureUnit = null, windUnit = null, conditions = null, forecast = null, windText = null, sunrise = null, sunset = null;
        int? code = null, forecastCode = null;
        if (zone.WeatherProvider == "open-meteo")
        {
            if (root.TryGetProperty("current", out var current))
            {
                temperature = NumberProperty(current, "temperature_2m");
                feelsLike = NumberProperty(current, "apparent_temperature");
                humidity = NumberProperty(current, "relative_humidity_2m");
                wind = NumberProperty(current, "wind_speed_10m");
                code = IntProperty(current, "weather_code");
            }
            if (root.TryGetProperty("current_units", out var units))
            {
                temperatureUnit = StringProperty(units, "temperature_2m");
                windUnit = StringProperty(units, "wind_speed_10m");
            }
            if (root.TryGetProperty("daily", out var daily))
            {
                high = FirstNumber(daily, "temperature_2m_max");
                low = FirstNumber(daily, "temperature_2m_min");
                precipitation = FirstNumber(daily, "precipitation_probability_max");
                code ??= FirstInt(daily, "weather_code");
                forecastCode = ArrayInt(daily, "weather_code", 1);
                sunrise = FirstString(daily, "sunrise");
                sunset = FirstString(daily, "sunset");
            }
            conditions = WeatherCondition(code);
        }
        else if (root.TryGetProperty("properties", out var properties) &&
                 properties.TryGetProperty("periods", out var periods) && periods.ValueKind == JsonValueKind.Array)
        {
            var all = periods.EnumerateArray().ToArray();
            if (all.Length > 0)
            {
                var current = all[0];
                temperature = NumberProperty(current, "temperature");
                temperatureUnit = StringProperty(current, "temperatureUnit") is { } unit ? $"°{unit}" : "°";
                conditions = StringProperty(current, "shortForecast");
                windText = $"{StringProperty(current, "windSpeed")} {StringProperty(current, "windDirection")}".Trim();
                if (current.TryGetProperty("probabilityOfPrecipitation", out var probability))
                    precipitation = NumberProperty(probability, "value");
                if (current.TryGetProperty("relativeHumidity", out var relativeHumidity))
                    humidity = NumberProperty(relativeHumidity, "value");
            }
            high = all.Where(period => BoolProperty(period, "isDaytime") == true).Select(period => NumberProperty(period, "temperature")).FirstOrDefault(value => value is not null);
            low = all.Where(period => BoolProperty(period, "isDaytime") == false).Select(period => NumberProperty(period, "temperature")).FirstOrDefault(value => value is not null);
            forecast = all.Skip(1).Select(period => StringProperty(period, "shortForecast")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            code = ForecastCode(conditions);
        }
        var icon = WeatherIcon(code, conditions);
        var unitText = temperatureUnit ?? (zone.WeatherUnits == "celsius" ? "°C" : "°F");
        var headline = new List<string>();
        if (fields.Contains("icon")) headline.Add(icon);
        if (fields.Contains("temperature") && temperature is not null) headline.Add($"{temperature:0.#}{unitText}");
        if (fields.Contains("conditions") && !string.IsNullOrWhiteSpace(conditions)) headline.Add(conditions);
        var items = new List<string>();
        if (fields.Contains("feelsLike") && feelsLike is not null) items.Add($"Feels like {feelsLike:0.#}{unitText}");
        if (fields.Contains("high") && high is not null) items.Add($"High {high:0.#}{unitText}");
        if (fields.Contains("low") && low is not null) items.Add($"Low {low:0.#}{unitText}");
        if (fields.Contains("precipitation") && precipitation is not null) items.Add($"Precipitation {precipitation:0.#}%");
        if (fields.Contains("humidity") && humidity is not null) items.Add($"Humidity {humidity:0.#}%");
        if (fields.Contains("wind") && !string.IsNullOrWhiteSpace(windText)) items.Add($"Wind {windText}");
        else if (fields.Contains("wind") && wind is not null) items.Add($"Wind {wind:0.#} {windUnit}".Trim());
        if (fields.Contains("forecast") && forecastCode is not null) items.Add($"Tomorrow {WeatherCondition(forecastCode)}");
        if (fields.Contains("sunrise") && FormatWeatherTime(sunrise) is { } sunriseText) items.Add($"Sunrise {sunriseText}");
        if (fields.Contains("sunset") && FormatWeatherTime(sunset) is { } sunsetText) items.Add($"Sunset {sunsetText}");
        var weather = new SignageWeatherSnapshot(temperature, feelsLike, high, low, precipitation, humidity, wind,
            unitText, windUnit, conditions,
            forecast ?? (forecastCode is null ? null : WeatherCondition(forecastCode)),
            FormatWeatherTime(sunrise), FormatWeatherTime(sunset), windText);
        return new(zone.Id, title, string.Join(" ", headline), items.ToArray(), refreshedAt, zone.SourceUrl, icon,
            Weather: weather);
    }

    private static IReadOnlyCollection<SignageCalendarEvent> ParseCalendarEvents(string payload, DateTimeOffset now)
    {
        var unfolded = Regex.Replace(payload, "\\r?\\n[ \\t]", "");
        var events = new List<SignageCalendarEvent>();
        foreach (var block in Regex.Matches(unfolded, "BEGIN:VEVENT(?<body>[\\s\\S]*?)END:VEVENT",
                     RegexOptions.IgnoreCase).Cast<Match>())
        {
            var values = block.Groups["body"].Value.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? Value(string key)
            {
                var line = values.FirstOrDefault(value => value.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
                    value.IndexOf(':') >= 0);
                return line is null ? null : UnescapeCalendar(line[(line.IndexOf(':') + 1)..]);
            }
            var title = Clean(Value("SUMMARY"));
            if (string.IsNullOrWhiteSpace(title)) continue;
            var startRaw = Value("DTSTART");
            var endRaw = Value("DTEND");
            var allDay = startRaw?.Length == 8 && startRaw.All(char.IsDigit);
            events.Add(new SignageCalendarEvent(title, Clean(Value("DESCRIPTION")), Clean(Value("LOCATION")),
                ParseCalendarDate(startRaw), ParseCalendarDate(endRaw), allDay));
        }
        return events.Where(value => (value.EndsAt ?? value.StartsAt ?? DateTimeOffset.MaxValue) >= now)
            .OrderBy(value => value.StartsAt ?? DateTimeOffset.MaxValue).ToArray();
    }

    private static DateTimeOffset? ParseCalendarDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParseExact(value, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var absolute)) return absolute;
        if (DateTime.TryParseExact(value, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return new DateTimeOffset(local);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
            out absolute) ? absolute : null;
    }

    private static string UnescapeCalendar(string value) => value.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",", StringComparison.Ordinal).Replace("\\;", ";", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string? FormatWeatherTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.ToString("h:mm tt", CultureInfo.CurrentCulture) : null;

    private static string[] ParseJsonItems(string payload)
    {
        try { using var document = JsonDocument.Parse(payload); return JsonItems(document.RootElement); }
        catch (JsonException) { return []; }
    }

    private static string[] JsonItems(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) && !root.TryGetProperty("events", out items)) return [];
        if (items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() :
            StringProperty(item, "title") ?? StringProperty(item, "name") ?? StringProperty(item, "summary"))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).Cast<string>().ToArray();
    }

    private static string? WeatherText(JsonElement root)
    {
        if (root.TryGetProperty("current_weather", out var current) || root.TryGetProperty("current", out current))
        {
            var temperature = NumberProperty(current, "temperature") ?? NumberProperty(current, "temperature_2m");
            var unit = StringProperty(current, "temperature_unit") ?? "°";
            if (temperature is not null) return $"{temperature:0.#}{unit}";
        }
        return null;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? NumberProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
    private static int? IntProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static bool? BoolProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static double? FirstNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var values) &&
        values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0 && values[0].TryGetDouble(out var number) ? number : null;
    private static int? FirstInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var values) &&
        values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0 && values[0].TryGetInt32(out var number) ? number : null;
    private static int? ArrayInt(JsonElement element, string name, int index) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var values) &&
        values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > index &&
        values[index].TryGetInt32(out var number) ? number : null;
    private static string? FirstString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var values) &&
        values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0 &&
        values[0].ValueKind == JsonValueKind.String ? values[0].GetString() : null;
    private static string WeatherCondition(int? code) => code switch
    {
        0 => "Clear sky",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "Rain",
        71 or 73 or 75 or 77 or 85 or 86 => "Snow",
        95 or 96 or 99 => "Thunderstorms",
        _ => "Weather"
    };
    private static int? ForecastCode(string? value)
    {
        var text = value?.ToLowerInvariant() ?? "";
        if (text.Contains("thunder")) return 95;
        if (text.Contains("snow") || text.Contains("sleet") || text.Contains("ice")) return 71;
        if (text.Contains("rain") || text.Contains("shower") || text.Contains("drizzle")) return 61;
        if (text.Contains("fog") || text.Contains("haze") || text.Contains("smoke")) return 45;
        if (text.Contains("partly") || text.Contains("mostly sunny") || text.Contains("mostly clear")) return 2;
        if (text.Contains("cloud") || text.Contains("overcast")) return 3;
        if (text.Contains("sun") || text.Contains("clear")) return 0;
        return null;
    }
    private static string WeatherIcon(int? code, string? conditions) => (code ?? ForecastCode(conditions)) switch
    {
        0 => "☀️",
        1 or 2 => "🌤️",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧️",
        71 or 73 or 75 or 77 or 85 or 86 => "❄️",
        95 or 96 or 99 => "⛈️",
        _ => "🌡️"
    };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")).Replace("\\n", " ").Trim()[..Math.Min(500, WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")).Replace("\\n", " ").Trim().Length)];
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];
}
