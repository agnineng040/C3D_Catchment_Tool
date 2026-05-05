using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Catchment2Structure
{
    /// <summary>
    /// Captures why a closed polyline could not be converted to a Catchment.
    /// </summary>
    public class PolylineIssue
    {
        public ObjectId PolylineId { get; }
        public string Handle { get; }
        public string Layer { get; }
        public int VertexCount { get; }
        public string Reason { get; }

        /// <summary>
        /// Optional "best guess" focus point (e.g., estimated self-intersection).
        /// </summary>
        public Point3d? FocusPoint { get; }

        public string Focus
            => FocusPoint.HasValue ? $"{FocusPoint.Value.X:0.###}, {FocusPoint.Value.Y:0.###}" : string.Empty;

        public PolylineIssue(ObjectId polylineId, string handle, string layer, int vertexCount, string reason, Point3d? focusPoint)
        {
            PolylineId = polylineId;
            Handle = handle ?? string.Empty;
            Layer = layer ?? string.Empty;
            VertexCount = vertexCount;
            Reason = reason ?? string.Empty;
            FocusPoint = focusPoint;
        }
    }
}
