using BuckScience.Application.Abstractions;
using BuckScience.Domain.Helpers;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace BuckScience.Application.Cameras;

public static class UpdateCamera
{
    public sealed record Command(
        int Id,
        string LocationName,
        string Brand,
        string? Model,
        double Latitude,
        double Longitude,
        float DirectionDegrees,
        bool IsActive
    );

    // Enforce both property scope and ownership
    public static async Task<bool> HandleAsync(
        Command cmd,
        IAppDbContext db,
        GeometryFactory geometryFactory,
        int userId,
        int propertyId,
        CancellationToken ct)
    {
        // Use explicit join to avoid LINQ translation errors with navigation properties
        var camera = await db.Cameras
            .Include(c => c.PlacementHistories)
            .Join(db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .Where(x => x.Camera.Id == cmd.Id &&
                       x.Camera.PropertyId == propertyId &&
                       x.Property.ApplicationUserId == userId)
            .Select(x => x.Camera)
            .FirstOrDefaultAsync(ct);

        if (camera is null)
            return false;

        camera.SetBrand(cmd.Brand);
        camera.SetModel(cmd.Model);

        // Check if location, direction, or location name changed
        var currentPlacement = camera.GetCurrentPlacement();
        bool locationChanged = currentPlacement == null ||
                              !CameraPlacementHelper.IsSamePlacement(
                                  currentPlacement.Latitude, currentPlacement.Longitude, currentPlacement.DirectionDegrees,
                                  cmd.Latitude, cmd.Longitude, cmd.DirectionDegrees) ||
                              !string.Equals(currentPlacement.LocationName, cmd.LocationName, StringComparison.OrdinalIgnoreCase);

        if (locationChanged)
        {
            camera.Move(cmd.Latitude, cmd.Longitude, cmd.DirectionDegrees, cmd.LocationName);
        }

        camera.SetActive(cmd.IsActive);

        await db.SaveChangesAsync(ct);
        return true;
    }
}