using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Provider-backed place facts. Model output is never a coordinate source.</summary>
public sealed class DestinationPlace
{
    public string Name;
    public string Address;
    public string OsmReference;
    public GeoCoordinate Coordinate;
    public double DistanceMeters;
    public bool IsTransit;
    public bool IsAreaOrStreet;
    public string City;
    public string Street;
    public string HouseNumber;
}

/// <summary>Pure Photon request/response validation shared by the UI and offline regressions.</summary>
public static class DestinationSearchData
{
    public const int MaximumResponseBytes = 256 * 1024;
    public const double MaximumDistanceMeters = 50000;
    public const string Attribution = "Place data: OpenStreetMap / Photon";

    public static bool TryBuildRequest(string endpoint, string query, GeoCoordinate origin, bool hasFreshLocation,
        out string url, out string error)
    {
        url = null;
        error = null;
        if (!hasFreshLocation || !origin.IsValid)
        { error = "Restore fresh phone GPS before searching."; return false; }
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || HasControl(query))
        { error = "Enter a nearby place name and city."; return false; }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { error = "The place search service is not configured."; return false; }
        string search = CleanQuery(query);
        if (search.Length == 0) { error = "Enter a nearby place name and city."; return false; }
        // Approximate origin is sufficient for relevance bias. Never send a GPS timestamp or room code.
        url = endpoint.TrimEnd('/') + "/?q=" + Uri.EscapeDataString(search) + "&limit=8&lang=en&lat=" +
            origin.Latitude.ToString("F3", CultureInfo.InvariantCulture) + "&lon=" +
            origin.Longitude.ToString("F3", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TrySelectPlace(string json, string query, GeoCoordinate origin, bool hasFreshLocation,
        out DestinationPlace selected, out string error)
        => TrySelect(json, query, origin, hasFreshLocation, null, out selected, out error);

    // The model supplies search hints only. Select an exact provider entity, never an
    // address centroid or a different business sharing the proposed street address.
    public static bool TrySelectResolvedPlace(string json, DestinationQueryHint hint, GeoCoordinate origin,
        bool hasFreshLocation, out DestinationPlace selected, out string error)
        => TrySelect(json, hint?.Name, origin, hasFreshLocation, hint, out selected, out error);

    public static bool TrySelectResolvedPlace(string json, DestinationQueryHint hint, string spokenQuery, GeoCoordinate origin,
        bool hasFreshLocation, out DestinationPlace selected, out string error)
    {
        if (!TrySelectResolvedPlace(json, hint, origin, hasFreshLocation, out selected, out error)) return false;
        if (MatchesSpokenContext(spokenQuery, hint.Name, selected)) return true;
        selected = null;
        error = "The suggested place does not match the location you said. Say the full place name and city.";
        return false;
    }

    private static bool TrySelect(string json, string query, GeoCoordinate origin, bool hasFreshLocation,
        DestinationQueryHint hint, out DestinationPlace selected, out string error)
    {
        selected = null;
        error = "The place service returned an invalid response. Try again.";
        if (!hasFreshLocation || !origin.IsValid)
        { error = "Restore fresh phone GPS before using a search result."; return false; }
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes || Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes || string.IsNullOrWhiteSpace(query) ||
            query.Length > 200 || HasControl(query)) return false;
        try
        {
            JObject envelope;
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
            {
                envelope = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) return false;
            }
            if (envelope["type"]?.Value<string>() != "FeatureCollection" || envelope["error"] != null ||
                !(envelope["features"] is JArray features) || features.Count > 32) return false;
            var matches = new List<DestinationPlace>();
            var seen = new HashSet<string>();
            int bestScore = 0;
            string normalizedQuery = Normalize(CleanQuery(query));
            if (normalizedQuery.Length == 0) return false;
            bool asksForTransit = Regex.IsMatch(normalizedQuery, @"\b(bus|tram|platform|station|stop)\b");
            foreach (JToken feature in features)
            {
                if (!TryReadPlace(feature as JObject, origin, out DestinationPlace place)) continue;
                int score = MatchScore(normalizedQuery, place);
                if (hint != null && (Normalize(place.Name) != normalizedQuery ||
                    place.IsAreaOrStreet || (!asksForTransit && place.IsTransit) ||
                    !MatchesLocality(hint.Locality, place) || !MatchesAddressHint(hint.Address, place))) continue;
                if (score == 0 || place.DistanceMeters > MaximumDistanceMeters || !seen.Add(place.OsmReference)) continue;
                // A building and its bus stop often have the same name. Never let provider order decide.
                score = score * 2 + (asksForTransit == place.IsTransit ? 1 : 0);
                if (score > bestScore) { matches.Clear(); bestScore = score; }
                if (score == bestScore) matches.Add(place);
            }
            if (matches.Count == 0)
            { error = "No reliable nearby match. Add the place name and city or street, then search again."; return false; }
            if (matches.Count != 1)
            {
                error = "Several matches: " + ShortDescription(matches[0]) + "; " + ShortDescription(matches[1]) +
                    ". Add a street or city and search again.";
                return false;
            }
            selected = matches[0];
            error = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool MatchesLocality(string locality, DestinationPlace place)
    {
        if (string.IsNullOrWhiteSpace(locality)) return true;
        string city = Normalize(locality);
        string mappedCity = Normalize(place.City ?? "");
        if (mappedCity == city) return true;
        // Campus straddles both municipalities; ECEB's OSM city is Champaign,
        // while its official postal address is Urbana. This exception is local.
        return (city == "urbana" || city == "champaign") &&
            (mappedCity == "urbana" || mappedCity == "champaign") &&
            WalkingRouteProgress.Distance(new GeoCoordinate(40.110, -88.227), place.Coordinate) < 15000;
    }

    private static bool MatchesSpokenContext(string spokenQuery, string canonicalName, DestinationPlace place)
    {
        if (string.IsNullOrWhiteSpace(spokenQuery)) return false;
        string query = Normalize(CleanQuery(spokenQuery));
        string name = Normalize(canonicalName);
        string context = "";
        Match explicitCity = Regex.Match(query, @"\b(?:in|near)\s+(.+)$");
        if (explicitCity.Success) context = explicitCity.Groups[1].Value;
        else if (spokenQuery.Contains(",")) context = Normalize(spokenQuery.Substring(spokenQuery.IndexOf(',') + 1));
        else if (query.StartsWith(name + " ", StringComparison.Ordinal)) context = query.Substring(name.Length).Trim();
        else
        {
            // Preserve trailing locality for acronyms such as ECEB Chicago.
            string initials = "";
            foreach (string word in name.Split(' '))
                if (word.Length > 0 && word != "and" && word != "of" && word != "the") initials += word[0];
            if (initials.Length >= 2 && query.StartsWith(initials + " ", StringComparison.Ordinal))
                context = query.Substring(initials.Length).Trim();
            // Retain a spoken city even when the campus name needs correction.
            else if (name == "alma mater" && (query == "armor mater" || query.StartsWith("armor mater ", StringComparison.Ordinal)))
                context = query.Substring("armor mater".Length).Trim();
            else if ((name == "alma mater" || name == "armory") && query.StartsWith("armor ", StringComparison.Ordinal))
                context = query.Substring("armor ".Length).Trim();
            else if (name == "alma mater" && query.StartsWith("alma ", StringComparison.Ordinal))
                context = query.Substring("alma ".Length).Trim();
        }
        string factualAddress = " " + NormalizeStreet(place.Address) + " ";
        foreach (string token in NormalizeStreet(context).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "statue" || token == "building" || token == "the" || token == "illinois" && factualAddress.Contains(" illinois ")) continue;
            if ((token == "urbana" || token == "champaign") && MatchesLocality(token, place)) continue;
            if (!factualAddress.Contains(" " + token + " ")) return false;
        }
        return true;
    }

    private static bool MatchesAddressHint(string address, DestinationPlace place)
    {
        if (string.IsNullOrWhiteSpace(address)) return true;
        string normalized = NormalizeStreet(address);
        Match number = Regex.Match(normalized, @"^\d+[a-z]?\b");
        if (number.Success && !string.IsNullOrEmpty(place.HouseNumber) &&
            number.Value != Normalize(place.HouseNumber)) return false;
        if (string.IsNullOrEmpty(place.Street)) return true;
        string street = NormalizeStreet(place.Street);
        if ((" " + normalized + " ").Contains(" " + street + " ")) return true;
        // Verified OSM/official-address discrepancy for this specific campus
        // building, not permission to ignore street direction for other places.
        return place.OsmReference == "R8840902" && number.Value == "306" &&
            normalized.StartsWith("306 north wright street", StringComparison.Ordinal) &&
            street == "south wright street";
    }

    private static string NormalizeStreet(string value)
    {
        string normalized = Normalize(value);
        string[] shortNames = { "n", "s", "e", "w", "st", "ave", "rd", "blvd", "dr", "ln" };
        string[] fullNames = { "north", "south", "east", "west", "street", "avenue", "road", "boulevard", "drive", "lane" };
        for (int i = 0; i < shortNames.Length; i++) normalized = Regex.Replace(normalized, @"\b" + shortNames[i] + @"\b", fullNames[i]);
        return normalized;
    }

    private static bool TryReadPlace(JObject feature, GeoCoordinate origin, out DestinationPlace place)
    {
        place = null;
        if (feature?["type"]?.Value<string>() != "Feature" || !(feature["geometry"] is JObject geometry) ||
            geometry["type"]?.Value<string>() != "Point" || !(geometry["coordinates"] is JArray coordinates) ||
            coordinates.Count != 2 || !(feature["properties"] is JObject properties) ||
            !TryNumber(coordinates[0], 180, out double lon) || !TryNumber(coordinates[1], 90, out double lat)) return false;
        string osmType = PlainText(properties["osm_type"], 1);
        if ((osmType != "N" && osmType != "W" && osmType != "R") || properties["osm_id"]?.Type != JTokenType.Integer ||
            properties["osm_id"].Value<long>() <= 0) return false;
        string street = PlainText(properties["street"], 100);
        string house = PlainText(properties["housenumber"], 20);
        string name = PlainText(properties["name"], 160);
        if (properties["name"] != null && name == null) return false;
        string streetAddress = ((house ?? "") + " " + (street ?? "")).Trim();
        if (string.IsNullOrEmpty(name)) name = streetAddress;
        if (string.IsNullOrEmpty(name)) return false;
        var address = new List<string>();
        foreach (string part in new[] { streetAddress, PlainText(properties["city"], 80),
            PlainText(properties["state"], 80), PlainText(properties["country"], 80) })
            if (!string.IsNullOrEmpty(part) && !address.Contains(part)) address.Add(part);
        // A locality is necessary for useful confirmation, even when the POI has no street address.
        if (address.Count == 0) return false;
        string key = PlainText(properties["osm_key"], 40);
        string value = PlainText(properties["osm_value"], 40);
        var coordinate = new GeoCoordinate(lat, lon);
        place = new DestinationPlace {
            Name = name, Address = string.Join(", ", address), Coordinate = coordinate,
            City = PlainText(properties["city"], 80), Street = street, HouseNumber = house,
            OsmReference = osmType + properties["osm_id"].Value<long>().ToString(CultureInfo.InvariantCulture),
            DistanceMeters = WalkingRouteProgress.Distance(origin, coordinate),
            IsTransit = key == "public_transport" || key == "railway" || value == "bus_stop" || value == "platform",
            IsAreaOrStreet = key == "boundary" || key == "highway" ||
                (key == "place" && value != "square")
        };
        return true;
    }

    private static int MatchScore(string query, DestinationPlace place)
    {
        string name = Normalize(place.Name);
        if (query == name) return 3;
        if (place.IsTransit)
        {
            query = Regex.Replace(query, @"\s+(bus stop|bus platform|tram stop|platform)$", "").Trim();
            if (query == name) return 3;
        }
        string all = " " + name + " " + Normalize(place.Address) + " ";
        int significantTokens = 0;
        foreach (string token in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "in" || token == "at" || token == "the") continue;
            significantTokens++;
            if (!all.Contains(" " + token + " ")) return 0;
        }
        if (significantTokens == 0) return 0;
        return (" " + query + " ").Contains(" " + name + " ") ? 2 : 1;
    }

    private static string CleanQuery(string query) => Regex.Replace(query.Trim(),
        @"^(?:(?:please\s+)?(?:take me to|navigate to|directions to|walking directions to|find|go to)(?:\s+|$))", "", RegexOptions.IgnoreCase).Trim();
    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
    private static string ShortDescription(DestinationPlace place)
    {
        string value = place.Name + ", " + place.Address;
        return value.Length <= 95 ? value : value.Substring(0, 92) + "...";
    }
    private static string PlainText(JToken token, int maximum)
    {
        if (token?.Type != JTokenType.String) return null;
        string value = token.Value<string>().Trim();
        return value.Length == 0 || value.Length > maximum || HasControl(value) ? null : value;
    }
    private static bool HasControl(string value)
    { foreach (char c in value) if (char.IsControl(c)) return true; return false; }
    private static bool TryNumber(JToken token, double bound, out double value)
    {
        value = 0;
        if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
        value = token.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value) && value >= -bound && value <= bound;
    }
}
