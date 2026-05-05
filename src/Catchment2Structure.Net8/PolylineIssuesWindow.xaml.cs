using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Catchment2Structure
{
    public partial class PolylineIssuesWindow : Window
    {
        private readonly List<PolylineIssue> _issues;
        private ObjectId[] _lastHighlighted = Array.Empty<ObjectId>();

        public PolylineIssuesWindow(List<PolylineIssue> issues)
        {
            InitializeComponent();
            _issues = issues ?? new List<PolylineIssue>();
            IssuesGrid.ItemsSource = _issues;
            UpdateButtons();
        }

        private PolylineIssue? SelectedIssue => IssuesGrid.SelectedItem as PolylineIssue;

        private void UpdateButtons()
        {
            bool has = SelectedIssue != null;
            GoToBtn.IsEnabled = has;
            SelectBtn.IsEnabled = has;
            CopyHandleBtn.IsEnabled = has;
        }

        private void IssuesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void GoTo_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedIssue;
            if (it == null) return;

            try
            {
                Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;

                using (doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    UnhighlightLast(tr);

                    // Always select/highlight the polyline.
                    var ids = new[] { it.PolylineId };

                    // Zoom behavior:
                    // - If we have a focus point (estimated self-intersection), zoom tightly around it.
                    // - Otherwise, zoom to the polyline extents.
                    if (it.FocusPoint.HasValue)
                    {
                        Extents3d? polyExt = null;
                        try
                        {
                            var ent = tr.GetObject(it.PolylineId, OpenMode.ForRead) as Entity;
                            if (ent != null)
                                polyExt = ent.GeometricExtents;
                        }
                        catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure PolylineIssuesWindow GeometricExtents: {ex.Message}"); }

                        double size = 10.0;
                        if (polyExt.HasValue)
                        {
                            double w = Math.Abs(polyExt.Value.MaxPoint.X - polyExt.Value.MinPoint.X);
                            double h = Math.Abs(polyExt.Value.MaxPoint.Y - polyExt.Value.MinPoint.Y);
                            double m = Math.Max(w, h);
                            if (m > 0)
                                size = Math.Max(1.0, m * 0.12);
                        }

                        var fp = it.FocusPoint.Value;
                        var ext = new Extents3d(
                            new Point3d(fp.X - size, fp.Y - size, 0.0),
                            new Point3d(fp.X + size, fp.Y + size, 0.0));
                        ZoomToExtents(ed, ext);
                    }
                    else
                    {
                        CommandsNet8.ZoomToObjectIds(ed, tr, ids);
                    }

                    // Implied selection + highlight
                    try { ed.SetImpliedSelection(ids); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure SetImpliedSelection: {ex.Message}"); }
                    Highlight(tr, ids);

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure PolylineIssuesWindow GoTo: {ex.Message}");
            }
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedIssue;
            if (it == null) return;

            try
            {
                Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;

                using (doc.LockDocument())
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    UnhighlightLast(tr);
                    var ids = new[] { it.PolylineId };
                    try { ed.SetImpliedSelection(ids); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure SetImpliedSelection: {ex.Message}"); }
                    Highlight(tr, ids);
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure PolylineIssuesWindow Select: {ex.Message}");
            }
        }

        private void CopyHandle_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedIssue;
            if (it == null) return;

            try
            {
                Clipboard.SetText(it.Handle ?? string.Empty);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure CopyHandle: {ex.Message}");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    using (doc.LockDocument())
                    using (Transaction tr = doc.TransactionManager.StartTransaction())
                    {
                        UnhighlightLast(tr);
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure PolylineIssuesWindow Close: {ex.Message}");
            }

            Close();
        }

        private void Highlight(Transaction tr, IEnumerable<ObjectId> ids)
        {
            ObjectId[] arr = (ids ?? Array.Empty<ObjectId>()).ToArray();
            _lastHighlighted = arr;

            foreach (ObjectId id in arr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                ent?.Highlight();
            }
        }

        private void UnhighlightLast(Transaction tr)
        {
            if (_lastHighlighted == null || _lastHighlighted.Length == 0)
                return;

            foreach (ObjectId id in _lastHighlighted)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                ent?.Unhighlight();
            }

            _lastHighlighted = Array.Empty<ObjectId>();
        }

        private static void ZoomToExtents(Editor ed, Extents3d extWcs)
        {
            if (ed == null)
                return;

            try
            {
                ViewTableRecord view = ed.GetCurrentView();

                // Transform WCS extents corners into DCS using the current view definition.
                Matrix3d displacement = Matrix3d.Displacement(Point3d.Origin - view.Target);
                Matrix3d worldToPlane = Matrix3d.PlaneToWorld(view.ViewDirection).Inverse();
                Matrix3d twist = Matrix3d.Rotation(-view.ViewTwist, Vector3d.ZAxis, Point3d.Origin);

                Point3d p1 = extWcs.MinPoint;
                Point3d p2 = extWcs.MaxPoint;

                p1 = p1.TransformBy(displacement).TransformBy(worldToPlane).TransformBy(twist);
                p2 = p2.TransformBy(displacement).TransformBy(worldToPlane).TransformBy(twist);

                double minX = Math.Min(p1.X, p2.X);
                double minY = Math.Min(p1.Y, p2.Y);
                double maxX = Math.Max(p1.X, p2.X);
                double maxY = Math.Max(p1.Y, p2.Y);

                double w = maxX - minX;
                double h = maxY - minY;
                if (w <= 0) w = 1.0;
                if (h <= 0) h = 1.0;

                const double pad = 1.35;
                w *= pad;
                h *= pad;

                double cx = (minX + maxX) * 0.5;
                double cy = (minY + maxY) * 0.5;

                double viewAspect = view.Width / Math.Max(view.Height, 1e-9);
                double targetAspect = w / Math.Max(h, 1e-9);
                if (targetAspect > viewAspect)
                    h = w / Math.Max(viewAspect, 1e-9);
                else
                    w = h * viewAspect;

                view.CenterPoint = new Point2d(cx, cy);
                view.Width = w;
                view.Height = h;
                ed.SetCurrentView(view);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure PolylineIssuesWindow ZoomToExtents: {ex.Message}");
            }
        }
    }
}
