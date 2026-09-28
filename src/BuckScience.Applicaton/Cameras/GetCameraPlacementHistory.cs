using BuckScience.Application.Abstractions;
using BuckScience.Domain.Helpers;
using Microsoft.EntityFrameworkCore;

namespace BuckScience.Application.Cameras;

public static class GetCameraPlacementHistory
{
    public sealed record PlacementHistoryItem(
        int Id,
        string LocationName,
        double Latitude,
        double Longitude,
        float DirectionDegrees,
        DateTime StartDateTime,
        DateTime? EndDateTime,
        bool IsCurrentPlacement,
        TimeSpan? Duration,
        // True when this placement sits at the same spot as the camera's current placement.
        // An edit that changes only direction or location name ends the old placement and
        // starts a new one at identical coordinates, so the ended record describes the same
        // spot the camera occupies today and must not be drawn as a separate map marker.
        bool IsAtCurrentLocation
    );

    public static async Task<IReadOnlyList<PlacementHistoryItem>> HandleAsync(
        IAppDbContext db,
        int userId,
        int cameraId,
        CancellationToken ct)
    {
        // Verify camera ownership through property and get placement history
        var placementHistory = await db.Cameras
            .AsNoTracking()
            .Include(c => c.PlacementHistories)
            .Join(db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .Where(x => x.Camera.Id == cameraId && x.Property.ApplicationUserId == userId)
            .SelectMany(x => x.Camera.PlacementHistories)
            .OrderByDescending(ph => ph.StartDateTime) // Most recent first
            .Select(ph => new PlacementHistoryItem(
                ph.Id,
                ph.LocationName,
                ph.Latitude,
                ph.Longitude,
                ph.DirectionDegrees,
                ph.StartDateTime,
                ph.EndDateTime,
                ph.EndDateTime == null, // IsCurrentPlacement
                ph.EndDateTime != null ? ph.EndDateTime.Value - ph.StartDateTime : (DateTime.UtcNow - ph.StartDateTime), // Duration
                false // IsAtCurrentLocation - cross-row comparison, resolved below
            ))
            .ToListAsync(ct);

        return MarkPlacementsAtCurrentLocation(placementHistory);
    }

    // Flags every placement that coincides with the current one. Kept separate from the query
    // because it compares rows against each other, which does not translate to SQL.
    public static IReadOnlyList<PlacementHistoryItem> MarkPlacementsAtCurrentLocation(
        IReadOnlyList<PlacementHistoryItem> placements)
    {
        var current = placements.FirstOrDefault(p => p.IsCurrentPlacement);
        if (current is null)
            return placements;

        return placements
            .Select(p => p.IsCurrentPlacement
                ? p with { IsAtCurrentLocation = true }
                : p with
                {
                    IsAtCurrentLocation = CameraPlacementHelper.IsSameLocation(
                        p.Latitude, p.Longitude, current.Latitude, current.Longitude)
                })
            .ToList();
    }
}
