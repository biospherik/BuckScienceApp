namespace BuckScience.Domain.Helpers
{
    /// <summary>
    /// Single source of truth for deciding whether two camera placements occupy the same
    /// physical spot. Used both when deciding that an edit warrants a new placement record
    /// and when deciding whether a historical placement is worth its own map marker.
    /// </summary>
    public static class CameraPlacementHelper
    {
        /// <summary>
        /// Latitude/longitude difference below which two placements are treated as the same spot.
        /// Roughly 11 metres, which is well inside the footprint of a map marker.
        /// </summary>
        public const double CoordinateTolerance = 0.0001;

        /// <summary>
        /// Difference in degrees below which two placements are treated as facing the same way.
        /// </summary>
        public const float DirectionTolerance = 0.1f;

        /// <summary>
        /// True when both placements sit at the same coordinates, regardless of facing direction.
        /// A camera whose direction was edited but which never moved satisfies this.
        /// </summary>
        public static bool IsSameLocation(
            double latitudeA, double longitudeA,
            double latitudeB, double longitudeB)
            => Math.Abs(latitudeA - latitudeB) <= CoordinateTolerance
            && Math.Abs(longitudeA - longitudeB) <= CoordinateTolerance;

        /// <summary>
        /// True when both placements sit at the same coordinates AND face the same way.
        /// </summary>
        public static bool IsSamePlacement(
            double latitudeA, double longitudeA, float directionA,
            double latitudeB, double longitudeB, float directionB)
            => IsSameLocation(latitudeA, longitudeA, latitudeB, longitudeB)
            && Math.Abs(directionA - directionB) <= DirectionTolerance;
    }
}
