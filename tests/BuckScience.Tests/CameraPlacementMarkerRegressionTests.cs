using BuckScience.Application.Cameras;
using BuckScience.Domain.Entities;
using BuckScience.Domain.Helpers;

namespace BuckScience.Tests
{
    /// <summary>
    /// Regression cover for the reported bug: after editing an existing camera's placement or
    /// facing direction, the camera's map pin turned grey, stopped opening the details pane and
    /// stopped showing its name on hover.
    ///
    /// Cause: every edit that touches location name, direction or coordinates ends the current
    /// placement and starts a new one (<see cref="Camera.Move"/>). When the camera did not
    /// actually move, the ended record carries identical coordinates to the new current one, so
    /// the property map drew a grey "historical" marker directly on top of the live camera pin,
    /// hiding its colour and taking its click and hover.
    ///
    /// Fix: placement history now reports <c>IsAtCurrentLocation</c>, and the map skips drawing a
    /// historical marker for any placement that coincides with where the camera sits today.
    /// </summary>
    public class CameraPlacementMarkerRegressionTests
    {
        private static GetCameraPlacementHistory.PlacementHistoryItem Item(
            int id, double lat, double lng, float direction, DateTime start, DateTime? end)
            => new(
                id,
                LocationName: "North Ridge Stand",
                Latitude: lat,
                Longitude: lng,
                DirectionDegrees: direction,
                StartDateTime: start,
                EndDateTime: end,
                IsCurrentPlacement: end == null,
                Duration: end?.Subtract(start),
                IsAtCurrentLocation: false);

        // ---------- The shared "same spot" rule ----------

        [Fact]
        public void IsSameLocation_TreatsIdenticalCoordinatesAsSameSpot()
        {
            Assert.True(CameraPlacementHelper.IsSameLocation(44.5, -89.5, 44.5, -89.5));
        }

        [Fact]
        public void IsSameLocation_TreatsSubToleranceDriftAsSameSpot()
        {
            Assert.True(CameraPlacementHelper.IsSameLocation(44.5, -89.5, 44.50005, -89.50005));
        }

        [Fact]
        public void IsSameLocation_TreatsRealMoveAsDifferentSpot()
        {
            Assert.False(CameraPlacementHelper.IsSameLocation(44.5, -89.5, 44.51, -89.51));
        }

        [Fact]
        public void IsSamePlacement_DirectionChangeAloneIsNotTheSamePlacement()
        {
            // Direction differs, so the edit still warrants a new placement record...
            Assert.False(CameraPlacementHelper.IsSamePlacement(44.5, -89.5, 0f, 44.5, -89.5, 90f));
            // ...but the two records still describe the same physical spot.
            Assert.True(CameraPlacementHelper.IsSameLocation(44.5, -89.5, 44.5, -89.5));
        }

        // ---------- The flag the map relies on ----------

        [Fact]
        public void DirectionOnlyEdit_FlagsEndedPlacementAsAtCurrentLocation()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var edit = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

            var history = new[]
            {
                Item(2, 44.5, -89.5, 90f, edit, null),   // current, after the direction edit
                Item(1, 44.5, -89.5, 0f, start, edit),   // ended by the edit, never moved
            };

            var flagged = GetCameraPlacementHistory.MarkPlacementsAtCurrentLocation(history);

            var ended = Assert.Single(flagged, p => !p.IsCurrentPlacement);
            Assert.True(
                ended.IsAtCurrentLocation,
                "An ended placement at the camera's current coordinates must be flagged so the map " +
                "does not stack a grey historical marker on top of the live camera pin.");
        }

        [Fact]
        public void GenuineMove_DoesNotFlagOldPlacementAsAtCurrentLocation()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var moved = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

            var history = new[]
            {
                Item(2, 44.5, -89.5, 90f, moved, null),      // current, new spot
                Item(1, 44.6, -89.6, 0f, start, moved),      // genuinely different spot
            };

            var flagged = GetCameraPlacementHistory.MarkPlacementsAtCurrentLocation(history);

            var old = Assert.Single(flagged, p => !p.IsCurrentPlacement);
            Assert.False(
                old.IsAtCurrentLocation,
                "A camera that really moved must keep a historical marker at its previous location.");
        }

        [Fact]
        public void SuccessiveDirectionEdits_FlagEveryEndedPlacementAtTheSameSpot()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Three direction edits without moving the camera: before the fix this piled three
            // grey markers directly on top of the pin.
            var history = new[]
            {
                Item(4, 44.5, -89.5, 270f, t.AddMonths(3), null),
                Item(3, 44.5, -89.5, 180f, t.AddMonths(2), t.AddMonths(3)),
                Item(2, 44.5, -89.5, 90f, t.AddMonths(1), t.AddMonths(2)),
                Item(1, 44.5, -89.5, 0f, t, t.AddMonths(1)),
            };

            var flagged = GetCameraPlacementHistory.MarkPlacementsAtCurrentLocation(history);

            Assert.All(flagged, p => Assert.True(p.IsAtCurrentLocation));
        }

        [Fact]
        public void CurrentPlacement_IsAlwaysFlaggedAtCurrentLocation()
        {
            var history = new[]
            {
                Item(1, 44.5, -89.5, 0f, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
            };

            var flagged = GetCameraPlacementHistory.MarkPlacementsAtCurrentLocation(history);

            Assert.True(Assert.Single(flagged).IsAtCurrentLocation);
        }

        [Fact]
        public void HistoryWithNoCurrentPlacement_IsLeftUnflagged()
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var history = new[] { Item(1, 44.5, -89.5, 0f, start, start.AddDays(1)) };

            var flagged = GetCameraPlacementHistory.MarkPlacementsAtCurrentLocation(history);

            Assert.False(Assert.Single(flagged).IsAtCurrentLocation);
        }

        // ---------- The domain behaviour the bug rides on ----------

        [Fact]
        public void EditingDirectionOnly_LeavesTwoPlacementsAtIdenticalCoordinates()
        {
            var camera = new Camera("Browning", "Strike Force");
            camera.PlaceInProperty(1);
            camera.PlaceAt(44.5, -89.5, 0f, DateTime.UtcNow.AddDays(-1), "North Ridge Stand");

            // Direction-only edit, exactly what UpdateCamera does
            camera.Move(44.5, -89.5, 90f, "North Ridge Stand");

            Assert.Equal(2, camera.PlacementHistories.Count);

            var current = camera.GetCurrentPlacement();
            Assert.NotNull(current);
            var ended = Assert.Single(camera.PlacementHistories.Where(p => !p.IsCurrentPlacement));

            Assert.True(
                CameraPlacementHelper.IsSameLocation(
                    ended.Latitude, ended.Longitude, current!.Latitude, current.Longitude),
                "A direction-only edit must leave the ended placement at the camera's current spot - " +
                "this is the data shape that used to bury the live pin under a grey marker.");
            Assert.Equal(90f, camera.DirectionDegrees);
        }
    }
}
