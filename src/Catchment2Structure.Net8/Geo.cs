
using System;
using Autodesk.AutoCAD.Geometry;

namespace Catchment2Structure
{
    internal static class Geo
    {
        /// <summary>
        /// Returns the 2D bounding box of the polygon. Throws if poly is null or empty.
        /// </summary>
        internal static (double minX, double minY, double maxX, double maxY) Bounds(Point2dCollection poly)
        {
            if (poly == null)
                throw new ArgumentNullException(nameof(poly));
            if (poly.Count == 0)
                throw new ArgumentException("Polygon must have at least one point.", nameof(poly));

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int i = 0; i < poly.Count; i++)
            {
                var pt = poly[i];
                if (pt.X < minX) minX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y > maxY) maxY = pt.Y;
            }
            return (minX, minY, maxX, maxY);
        }

        internal static bool InBounds(Point2d p, (double minX, double minY, double maxX, double maxY) b, double tol = 0.0)
            => p.X >= b.minX - tol && p.X <= b.maxX + tol && p.Y >= b.minY - tol && p.Y <= b.maxY + tol;

        /// <summary>
        /// Returns the area-weighted centroid. Throws if poly is null or has fewer than 2 points.
        /// For 2 points returns the midpoint.
        /// </summary>
        internal static Point2d Centroid(Point2dCollection poly)
        {
            if (poly == null)
                throw new ArgumentNullException(nameof(poly));
            int n = poly.Count;
            if (n < 2)
                throw new ArgumentException("Polygon must have at least 2 points for centroid.", nameof(poly));

            if (n == 2)
                return new Point2d(
                    (poly[0].X + poly[1].X) * 0.5,
                    (poly[0].Y + poly[1].Y) * 0.5);

            double a = 0.0;
            double cx = 0.0;
            double cy = 0.0;

            for (int i = 0; i < n; i++)
            {
                Point2d p0 = poly[i];
                Point2d p1 = poly[(i + 1) % n];

                double cross = p0.X * p1.Y - p1.X * p0.Y;
                a += cross;
                cx += (p0.X + p1.X) * cross;
                cy += (p0.Y + p1.Y) * cross;
            }

            a *= 0.5;
            if (Math.Abs(a) < 1e-12)
            {
                var b = Bounds(poly);
                return new Point2d((b.minX + b.maxX) * 0.5, (b.minY + b.maxY) * 0.5);
            }

            cx /= (6.0 * a);
            cy /= (6.0 * a);
            return new Point2d(cx, cy);
        }

        internal static bool IsPointInPolygonOrOnEdge(Point2d p, Point2dCollection poly, double tol)
        {
            if (poly == null || poly.Count == 0)
                return false;

            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2d a = poly[i];
                Point2d b = poly[(i + 1) % n];
                if (DistancePointToSegment(p, a, b) <= tol) return true;
            }

            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2d pi = poly[i];
                Point2d pj = poly[j];

                bool intersects = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                  (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X);

                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;

            double c1 = wx * vx + wy * vy;
            if (c1 <= 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) return Math.Sqrt((p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y));

            double t = c1 / c2;
            double px = a.X + t * vx, py = a.Y + t * vy;
            return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }
    }
}
