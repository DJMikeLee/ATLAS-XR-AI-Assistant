using System;
using System.Collections.Generic;

/// <summary>A geographic position in degrees, independent of Unity world coordinates.</summary>
[Serializable]
public struct GeoCoordinate
{
    public double Latitude;
    public double Longitude;

    public GeoCoordinate(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public bool IsValid => WalkingRouteProgress.IsFinite(Latitude) && WalkingRouteProgress.IsFinite(Longitude)
        && Latitude >= -90 && Latitude <= 90 && Longitude >= -180 && Longitude <= 180;
}

/// <summary>Only a pedestrian routing provider may supply geometry and maneuvers.</summary>
[Serializable]
public sealed class WalkingRoute
{
    public List<GeoCoordinate> Geometry = new List<GeoCoordinate>();
    public List<RouteManeuver> Maneuvers = new List<RouteManeuver>();
    public double DistanceMeters;
    public double DurationSeconds;
    public string Attribution;
}

[Serializable]
public sealed class RouteManeuver
{
    public int GeometryIndex;
    public string Instruction;
    public string Modifier;
    public string StreetName;
}

public enum WalkingRouteProgressState
{
    NoRoute,
    WaitingForLocation,
    Navigating,
    PoorAccuracy,
    OffRoute,
    Arrived
}

/// <summary>
/// Matches distinct, fresh GPS fixes to pedestrian route geometry. Call Update once per GPS
/// fix using monotonic seconds, not once per frame or on a compass-only heartbeat. It neither
/// fetches routes nor invents instructions. Metres are measured along the supplied geometry.
/// </summary>
public sealed class WalkingRouteProgress
{
    private const double EarthRadius = 6371000;
    private const double Radians = Math.PI / 180;
    private readonly List<GeoCoordinate> geometry = new List<GeoCoordinate>();
    private readonly List<RouteManeuver> maneuvers = new List<RouteManeuver>();
    private double[] cumulative = new double[0];
    private double routeDuration;
    private double lastSampleTime = double.NaN;
    private double lastAcceptedTime = double.NaN;
    private bool hasProjection;
    private int maneuverIndex;
    private int offRouteVotes;
    private double offRouteSince = double.NaN;
    private double lastOffRouteVote = double.NaN;
    private int recoveryVotes;
    private double lastRecoveryVote = double.NaN;
    private int arrivalVotes;
    private double arrivalSince = double.NaN;
    private double lastArrivalVote = double.NaN;

    public bool HasRoute => geometry.Count >= 2;
    public bool IsLocationReliable { get; private set; }
    public double TotalDistanceMeters { get; private set; }
    public double ProgressMeters { get; private set; }
    public double DistanceRemainingMeters => Math.Max(0, TotalDistanceMeters - ProgressMeters);
    public double DurationRemainingSeconds => TotalDistanceMeters > 0
        ? routeDuration * DistanceRemainingMeters / TotalDistanceMeters : 0;
    public double DistanceToNextManeuverMeters => NextManeuver == null ? DistanceRemainingMeters
        : Math.Max(0, cumulative[NextManeuver.GeometryIndex] - ProgressMeters);
    public RouteManeuver NextManeuver => maneuverIndex < maneuvers.Count ? maneuvers[maneuverIndex] : null;
    public bool IsOffRoute { get; private set; }
    public bool HasArrived { get; private set; }
    public GeoCoordinate ProjectedPosition { get; private set; }
    public WalkingRouteProgressState State { get; private set; }

    /// <summary>Invalid routes clear all previous progress; caller may inspect HasRoute.</summary>
    public void Reset(WalkingRoute route)
    {
        geometry.Clear();
        maneuvers.Clear();
        cumulative = new double[0];
        routeDuration = TotalDistanceMeters = ProgressMeters = 0;
        lastSampleTime = lastAcceptedTime = double.NaN;
        hasProjection = IsLocationReliable = IsOffRoute = HasArrived = false;
        maneuverIndex = 0;
        ProjectedPosition = default(GeoCoordinate);
        ResetOffRouteVotes();
        ResetArrivalVotes();
        State = WalkingRouteProgressState.NoRoute;
        if (route == null || route.Geometry == null || route.Geometry.Count < 2 || route.Geometry.Count > 100000)
            return;
        foreach (GeoCoordinate point in route.Geometry)
            if (!point.IsValid) return;
        geometry.AddRange(route.Geometry);
        cumulative = new double[geometry.Count];
        for (int i = 1; i < geometry.Count; i++)
            cumulative[i] = cumulative[i - 1] + Distance(geometry[i - 1], geometry[i]);
        TotalDistanceMeters = cumulative[cumulative.Length - 1];
        if (!IsFinite(TotalDistanceMeters) || TotalDistanceMeters < 1)
        {
            geometry.Clear();
            cumulative = new double[0];
            TotalDistanceMeters = 0;
            return;
        }
        routeDuration = IsFinite(route.DurationSeconds) && route.DurationSeconds > 0 ? route.DurationSeconds : 0;
        if (route.Maneuvers != null)
        {
            foreach (RouteManeuver maneuver in route.Maneuvers)
            {
                if (maneuver == null || maneuver.GeometryIndex < 0 || maneuver.GeometryIndex >= geometry.Count
                    || string.IsNullOrWhiteSpace(maneuver.Instruction)) continue;
                maneuvers.Add(new RouteManeuver
                {
                    GeometryIndex = maneuver.GeometryIndex, Instruction = maneuver.Instruction,
                    Modifier = maneuver.Modifier, StreetName = maneuver.StreetName
                });
            }
            maneuvers.Sort((a, b) => a.GeometryIndex.CompareTo(b.GeometryIndex));
        }
        ProjectedPosition = geometry[0];
        State = WalkingRouteProgressState.WaitingForLocation;
    }

    public void Update(double latitude, double longitude, double accuracyMeters, double nowSeconds)
    {
        if (!HasRoute || !IsFinite(nowSeconds) || (!double.IsNaN(lastSampleTime) && nowSeconds <= lastSampleTime))
            return;
        lastSampleTime = nowSeconds;
        GeoCoordinate position = new GeoCoordinate(latitude, longitude);
        bool unknownAccuracy = double.IsNaN(accuracyMeters);
        IsLocationReliable = position.IsValid && (unknownAccuracy
            || (IsFinite(accuracyMeters) && accuracyMeters >= 0 && accuracyMeters <= 50));
        if (!IsLocationReliable)
        {
            ResetOffRouteVotes();
            ResetArrivalVotes();
            State = WalkingRouteProgressState.PoorAccuracy;
            return;
        }
        if (HasArrived)
        {
            State = WalkingRouteProgressState.Arrived;
            return;
        }

        double accuracy = unknownAccuracy ? 15 : accuracyMeters;
        double elapsed = double.IsNaN(lastAcceptedTime) ? 0 : Math.Max(0, nowSeconds - lastAcceptedTime);
        // A crossing/parallel path must not jump to a much later leg. After a long gap,
        // reacquire at most 100 m ahead; a larger displacement needs a fresh route.
        double maximumAdvance = Math.Min(100, Math.Max(25, elapsed * 3 + 10));
        double minimum = hasProjection ? Math.Max(0, ProgressMeters - 15) : 0;
        double maximum = hasProjection ? Math.Min(TotalDistanceMeters, ProgressMeters + maximumAdvance) : TotalDistanceMeters;
        Projection projection = Project(position, minimum, maximum);
        double exitDistance = Math.Max(30, accuracy * 1.5);
        double enterDistance = Math.Max(15, accuracy);
        if (projection.Distance > exitDistance)
        {
            recoveryVotes = 0;
            lastRecoveryVote = double.NaN;
            Vote(ref offRouteVotes, ref offRouteSince, ref lastOffRouteVote, nowSeconds);
            if (offRouteVotes >= 3 && nowSeconds - offRouteSince >= 3) IsOffRoute = true;
            ResetArrivalVotes();
            State = IsOffRoute ? WalkingRouteProgressState.OffRoute : WalkingRouteProgressState.Navigating;
            return;
        }
        if (IsOffRoute)
        {
            if (projection.Distance <= enterDistance)
            {
                if (double.IsNaN(lastRecoveryVote) || nowSeconds - lastRecoveryVote >= 1)
                {
                    recoveryVotes++;
                    lastRecoveryVote = nowSeconds;
                }
                if (recoveryVotes >= 2) IsOffRoute = false;
            }
            else { recoveryVotes = 0; lastRecoveryVote = double.NaN; }
            if (IsOffRoute) { State = WalkingRouteProgressState.OffRoute; return; }
        }
        ResetOffRouteVotes();
        hasProjection = true;
        lastAcceptedTime = nowSeconds;
        ProgressMeters = Math.Max(ProgressMeters, projection.Progress);
        ProjectedPosition = PositionAt(ProgressMeters);
        while (maneuverIndex + 1 < maneuvers.Count
               && cumulative[maneuvers[maneuverIndex].GeometryIndex] < ProgressMeters + 2)
            maneuverIndex++;

        double endDistance = Distance(position, geometry[geometry.Count - 1]);
        bool arrivalAccuracy = unknownAccuracy || accuracyMeters <= 25;
        bool nearDestination = arrivalAccuracy && endDistance <= (unknownAccuracy ? 12 : 20)
            && DistanceRemainingMeters <= (unknownAccuracy ? 15 : 25);
        if (nearDestination)
        {
            Vote(ref arrivalVotes, ref arrivalSince, ref lastArrivalVote, nowSeconds);
            HasArrived = arrivalVotes >= 3 && nowSeconds - arrivalSince >= 3;
        }
        else ResetArrivalVotes();
        State = HasArrived ? WalkingRouteProgressState.Arrived : WalkingRouteProgressState.Navigating;
    }

    private struct Projection
    {
        public double Distance;
        public double Progress;
    }

    private Projection Project(GeoCoordinate point, double minimum, double maximum)
    {
        Projection closest = new Projection { Distance = double.PositiveInfinity };
        for (int i = 0; i < geometry.Count - 1; i++)
        {
            double length = cumulative[i + 1] - cumulative[i];
            if (length < 0.001 || cumulative[i + 1] < minimum || cumulative[i] > maximum) continue;
            GeoCoordinate start = geometry[i], end = geometry[i + 1];
            double scale = Math.Max(0.000001, Math.Cos((start.Latitude + end.Latitude) * 0.5 * Radians));
            double x = LongitudeDelta(end.Longitude - start.Longitude) * Radians * EarthRadius * scale;
            double y = (end.Latitude - start.Latitude) * Radians * EarthRadius;
            double px = LongitudeDelta(point.Longitude - start.Longitude) * Radians * EarthRadius * scale;
            double py = (point.Latitude - start.Latitude) * Radians * EarthRadius;
            double ratio = (px * x + py * y) / (x * x + y * y);
            ratio = Clamp(ratio, Math.Max(0, (minimum - cumulative[i]) / length), Math.Min(1, (maximum - cumulative[i]) / length));
            GeoCoordinate projected = Interpolate(start, end, ratio);
            double distance = Distance(point, projected);
            // Prefer the earlier leg where two segments are equally near (loop crossings).
            if (distance < closest.Distance - 0.01)
                closest = new Projection { Distance = distance, Progress = cumulative[i] + ratio * length };
        }
        return closest;
    }

    private GeoCoordinate PositionAt(double progress)
    {
        for (int i = 1; i < geometry.Count; i++)
        {
            if (cumulative[i] < progress) continue;
            double length = cumulative[i] - cumulative[i - 1];
            return Interpolate(geometry[i - 1], geometry[i], length <= 0 ? 0 : (progress - cumulative[i - 1]) / length);
        }
        return geometry[geometry.Count - 1];
    }

    private static GeoCoordinate Interpolate(GeoCoordinate start, GeoCoordinate end, double amount)
    {
        double longitude = start.Longitude + LongitudeDelta(end.Longitude - start.Longitude) * amount;
        return new GeoCoordinate(start.Latitude + (end.Latitude - start.Latitude) * amount, LongitudeDelta(longitude));
    }

    public static double Distance(GeoCoordinate a, GeoCoordinate b)
    {
        double latitude = (b.Latitude - a.Latitude) * Radians;
        double longitude = LongitudeDelta(b.Longitude - a.Longitude) * Radians;
        double half = Math.Sin(latitude * 0.5) * Math.Sin(latitude * 0.5)
            + Math.Cos(a.Latitude * Radians) * Math.Cos(b.Latitude * Radians)
            * Math.Sin(longitude * 0.5) * Math.Sin(longitude * 0.5);
        return EarthRadius * 2 * Math.Asin(Math.Sqrt(Clamp(half, 0, 1)));
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
    private static double LongitudeDelta(double value) => (value + 540) % 360 - 180;

    private static void Vote(ref int count, ref double since, ref double last, double now)
    {
        if (!double.IsNaN(last) && now - last < 1) return;
        if (count == 0) since = now;
        count++;
        last = now;
    }

    private void ResetOffRouteVotes()
    {
        offRouteVotes = recoveryVotes = 0;
        offRouteSince = lastOffRouteVote = lastRecoveryVote = double.NaN;
    }

    private void ResetArrivalVotes()
    {
        arrivalVotes = 0;
        arrivalSince = lastArrivalVote = double.NaN;
    }
}
