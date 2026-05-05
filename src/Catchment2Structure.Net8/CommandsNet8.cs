using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

[assembly: CommandClass(typeof(Catchment2Structure.CommandsNet8))]

namespace Catchment2Structure
{
    public class CommandsNet8 : IExtensionApplication
    {
        private static OptionsWindow? _optionsWin;
        private static RunOptions? _pendingOptions;
        private static PolylineIssuesWindow? _polyIssuesWin;

        /// <summary>Max lines of detail per category in the results summary (avoids huge output).</summary>
        private const int MaxDetailLines = 50;

        /// <summary>Above this vertex count, self-intersection check is skipped for performance (O(n²)).</summary>
        private const int MaxVerticesForSelfIntersectionCheck = 500;

        public void Initialize() { }
        public void Terminate()
        {
            try { _optionsWin?.Close(); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure Terminate (options): {ex.Message}"); }
            _optionsWin = null;
            try { _polyIssuesWin?.Close(); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure Terminate (poly issues): {ex.Message}"); }
            _polyIssuesWin = null;
        }

        [CommandMethod("C2S")]
        public void AssignCatchmentsToStructures()
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;
            Database db = doc.Database;

            CivilDocument civDoc = CivilApplication.ActiveDocument;

            // Gather UI lists from the drawing (read-only transaction).
            List<NamedId> pipeNetworks;
            List<NamedId> catchmentGroups;
            List<NamedId> catchmentStyles;
            List<NamedId> surfaces;
            List<string> layers;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                pipeNetworks = GetPipeNetworks(tr, civDoc);
                catchmentGroups = GetCatchmentGroups(tr, civDoc);
                catchmentStyles = GetCatchmentStyles(tr, civDoc);
                surfaces = GetSurfaces(tr, civDoc);
                layers = GetLayerNames(tr, db);
                tr.Commit();
            }

            // Modeless UI: allow interacting with Civil 3D while the options window is open.
            // When the user clicks Run, we stash the options and fire a follow-on command
            // (C2S_RUN) so interactive editor prompts still work.
            if (_optionsWin != null)
            {
                try { _optionsWin.Activate(); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure Activate options: {ex.Message}"); }
                return;
            }

            void OnClosed() => _optionsWin = null;

            void OnRun(RunOptions opts)
            {
                _pendingOptions = opts;
                try
                {
                    // Ensure the work runs in command context.
                    doc.SendStringToExecute("_.C2S_RUN\n", true, false, false);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Catchment2Structure SendStringToExecute: {ex.Message}");
                    _pendingOptions = null;
                }
            }

            _optionsWin = new OptionsWindow(pipeNetworks, catchmentGroups, catchmentStyles, surfaces, layers, OnRun, OnClosed);
            ShowModelessWindowSafe(_optionsWin);
        }

        private static void ShowModelessWindowSafe(System.Windows.Window win)
        {
            // Prefer AutoCAD/Civil 3D's host-managed modeless show (keeps focus/owner correct).
            // Fall back to vanilla WPF Show() if the API method is not available.
            try
            {
                var appType = typeof(Autodesk.AutoCAD.ApplicationServices.Application);

                var mi = appType.GetMethod("ShowModelessWindow", new[] { typeof(System.Windows.Window) });
                if (mi != null)
                {
                    mi.Invoke(null, new object[] { win });
                    return;
                }

                mi = appType.GetMethod("ShowModelessDialog", new[] { typeof(System.Windows.Window) });
                if (mi != null)
                {
                    mi.Invoke(null, new object[] { win });
                    return;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ShowModelessWindow: {ex.Message}");
            }

            win.Show();
        }

        [CommandMethod("C2S_RUN")]
        public void AssignCatchmentsToStructures_Run()
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            RunOptions? opts = _pendingOptions;
            _pendingOptions = null;

            if (opts == null)
            {
                doc.Editor.WriteMessage("\nNo pending options found. Run C2S first.\n");
                return;
            }

            RunWithOptions(doc, opts);
        }

        private static void RunWithOptions(Document doc, RunOptions opts)
        {
            Editor ed = doc.Editor;
            Database db = doc.Database;

            CivilDocument civDoc = CivilApplication.ActiveDocument;

            int assigned = 0;
            int skipped = 0;
            int noStruct = 0;
            int converted = 0;

            var assignedDetails = new List<string>();
            var skippedDetails = new List<string>();
            var noStructDetails = new List<string>();
            var reviewItems = new List<ReviewItem>();

            List<ObjectId> createdCatchments = new List<ObjectId>();
            var polyIssues = new List<PolylineIssue>();

            using (doc.LockDocument())
            {
                // Phase 1: Optional polyline -> catchment conversion (short transaction).
                if (opts.ConvertPolylines)
                {
                    using (Transaction tr1 = db.TransactionManager.StartTransaction())
                    {
                        ObjectId createGroupId = ResolveCreateGroupId(tr1, civDoc, opts);
                        createdCatchments = ConvertClosedPolylinesToCatchments(tr1, civDoc, db, opts, createGroupId, polyIssues);
                        converted = createdCatchments.Count;
                        tr1.Commit();
                    }
                    ed.WriteMessage($"\nConverted {converted} closed polylines to catchments.\n");
                    if (polyIssues.Count > 0)
                        ed.WriteMessage($"\nWARNING: {polyIssues.Count} polyline(s) could not be converted (non-simple/self-intersecting boundary). An issues window will be shown after the run.\n");
                }

                // Phase 2: Load structures and catchment ID list (short read transaction).
                List<(ObjectId Id, Point2d XY, Point3d XYZ)> structures;
                List<ObjectId> catchmentIdsToProcessList;
                using (Transaction tr2 = db.TransactionManager.StartTransaction())
                {
                    structures = LoadStructuresForScope(tr2, civDoc, opts.PipeNetworkId);
                    catchmentIdsToProcessList = (opts.ConvertPolylines && opts.OnlyProcessCreatedCatchments)
                        ? new List<ObjectId>(createdCatchments)
                        : EnumerateCatchmentsForScope(tr2, civDoc, opts.TargetCatchmentGroupId).ToList();
                    tr2.Commit();
                }

                // Phase 3: Process each catchment in its own short transaction.
                foreach (ObjectId cid in catchmentIdsToProcessList)
                {
                    using (Transaction tr3 = db.TransactionManager.StartTransaction())
                    {
                        var c = tr3.GetObject(cid, OpenMode.ForWrite) as Catchment;
                        if (c == null) continue;

                        if (!opts.OverwriteAssignments && c.ReferenceDischargeObjectId != ObjectId.Null)
                        {
                            skipped++;
                            skippedDetails.Add($"{c.Name} (existing assignment)");
                            tr3.Commit();
                            continue;
                        }

                        Point2dCollection poly = c.BoundaryPolyline2d;
                        if (poly == null || poly.Count < 3)
                        {
                            skipped++;
                            skippedDetails.Add($"{c.Name} (bad boundary)");
                            tr3.Commit();
                            continue;
                        }

                        var bounds = Geo.Bounds(poly);

                        var candidates = new List<ObjectId>();
                        foreach (var st in structures)
                        {
                            if (!Geo.InBounds(st.XY, bounds, 0.0))
                                continue;
                            if (!Geo.IsPointInPolygonOrOnEdge(st.XY, poly, 1e-6))
                                continue;
                            candidates.Add(st.Id);
                        }

                        if (candidates.Count == 0)
                        {
                            if (opts.PromptWhenNoStructureFound)
                            {
                                reviewItems.Add(new ReviewItem
                                {
                                    Type = ReviewItemType.NoStructureInside,
                                    CatchmentId = c.ObjectId,
                                    CatchmentName = c.Name
                                });
                                tr3.Commit();
                                continue;
                            }
                            noStruct++;
                            noStructDetails.Add(c.Name);
                            tr3.Commit();
                            continue;
                        }

                        Point2d decisionPt = GetCatchmentDecisionPoint(c, poly);
                        ObjectId nearest = PickNearestStructure(decisionPt, candidates, tr3);
                        if (nearest == ObjectId.Null)
                            nearest = candidates[0];

                        if (candidates.Count == 1 || !opts.PromptWhenMultipleStructures)
                        {
                            c.ReferenceDischargeObjectId = nearest;
                            string structName;
                            try
                            {
                                var s = tr3.GetObject(nearest, OpenMode.ForRead) as Structure;
                                structName = (s != null) ? s.Name : nearest.Handle.ToString();
                            }
                            catch (System.Exception ex)
                            {
                                System.Diagnostics.Trace.WriteLine($"Catchment2Structure get structure name: {ex.Message}");
                                structName = nearest.Handle.ToString();
                            }
                            assignedDetails.Add($"{c.Name} -> {structName}");
                            assigned++;
                        }
                        else
                        {
                            reviewItems.Add(new ReviewItem
                            {
                                Type = ReviewItemType.MultipleStructures,
                                CatchmentId = c.ObjectId,
                                CatchmentName = c.Name,
                                CandidateStructureIds = candidates.ToList(),
                                NearestCandidateId = nearest
                            });
                        }

                        tr3.Commit();
                    }
                }
            }

            // Results summary (command line + modal results window)
            // Post-run review for ambiguous / unresolved catchments
            if (reviewItems.Count > 0)
            {
                // Keep reopening the review window until the user clicks Finish.
                while (true)
                {
                    var reviewWin = new ReviewWindow(reviewItems);
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(reviewWin);

                    // If the user clicked Finish/Close, stop reviewing and move on to summary.
                    if (reviewWin.Finished)
                        break;

                    // If the user requested an assignment, perform the editor prompt in command context,
                    // then reopen the review window.
                    ReviewItem? req = reviewWin.AssignRequestedItem;
if (req == null)
    continue;

Document? activeDocMaybe = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
if (activeDocMaybe == null)
    continue;

Document activeDoc = activeDocMaybe;
Editor activeEd = activeDoc.Editor;

                    try
                    {
                        using (activeDoc.LockDocument())
                        using (var tr = activeDoc.TransactionManager.StartTransaction())
                        {
                            // Zoom + highlight context: catchment + candidates (if any)
                            var ids = new List<ObjectId> { req.CatchmentId };
                            if (req.CandidateStructureIds != null && req.CandidateStructureIds.Count > 0)
                                ids.AddRange(req.CandidateStructureIds);

                            ZoomToObjectIds(activeEd, tr, ids.ToArray());

                            foreach (ObjectId id in ids)
                            {
                                var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                                ent?.Highlight();
                            }

                            // Open catchment for write
                            var c = tr.GetObject(req.CatchmentId, OpenMode.ForWrite) as Catchment;
                            if (c == null)
                            {
                                req.Notes = "Could not open catchment";
                                req.IsResolved = true;
                                req.IsAssigned = false;
                                tr.Commit();
                                continue;
                            }

                            ObjectId picked = ObjectId.Null;

                            if (req.Type == ReviewItemType.MultipleStructures && req.CandidateStructureIds.Count > 0)
                                picked = PromptPickStructureFromCandidates(activeEd, tr, req.CandidateStructureIds, req.NearestCandidateId);
                            else
                                picked = PromptPickAnyStructure(activeEd, tr);

                            if (picked == ObjectId.Null)
                            {
                                req.Notes = "Review skipped by user";
                                req.IsResolved = true;
                                req.IsAssigned = false;
                                tr.Commit();
                                continue;
                            }

                            // Assign
                            c.ReferenceDischargeObjectId = picked;

                            string pickedName;
                            try
                            {
                                var ps = tr.GetObject(picked, OpenMode.ForRead) as Structure;
                                pickedName = (ps != null) ? ps.Name : picked.Handle.ToString();
                            }
                            catch (System.Exception ex)
                            {
                                System.Diagnostics.Trace.WriteLine($"Catchment2Structure get structure name (review): {ex.Message}");
                                pickedName = picked.Handle.ToString();
                            }

                            req.IsResolved = true;
                            req.IsAssigned = true;
                            req.AssignedStructureId = picked;
                            req.AssignedStructureName = pickedName;
                            req.Notes = "Assigned (review)";

                            tr.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Catchment2Structure review assignment: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            using (activeDoc.LockDocument())
                            using (var tr2 = activeDoc.TransactionManager.StartTransaction())
                            {
                                var ids2 = new List<ObjectId> { req.CatchmentId };
                                if (req.CandidateStructureIds != null && req.CandidateStructureIds.Count > 0)
                                    ids2.AddRange(req.CandidateStructureIds);

                                foreach (ObjectId id in ids2)
                                {
                                    var ent = tr2.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                                    ent?.Unhighlight();
                                }
                                tr2.Commit();
                            }
                        }
                        catch (System.Exception ex2)
                        {
                            System.Diagnostics.Trace.WriteLine($"Catchment2Structure unhighlight: {ex2.Message}");
                        }
                    }
                }

                // Apply results from review window to summary counts/details
                foreach (ReviewItem it in reviewItems)
                {
                    if (it.IsResolved && it.IsAssigned && it.AssignedStructureId != ObjectId.Null)
                    {
                        assigned++;
                        assignedDetails.Add($"{it.CatchmentName} -> {it.AssignedStructureName}");
                        continue;
                    }

                    // Not assigned (skipped/unresolved)
                    if (it.Type == ReviewItemType.NoStructureInside)
                    {
                        noStruct++;
                        if (it.IsResolved)
                            noStructDetails.Add($"{it.CatchmentName} (review skipped)");
                        else
                            noStructDetails.Add($"{it.CatchmentName} (review not completed)");
                    }
                    else
                    {
                        skipped++;
                        if (it.IsResolved)
                            skippedDetails.Add($"{it.CatchmentName} (review skipped)");
                        else
                            skippedDetails.Add($"{it.CatchmentName} (review not completed)");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("C2S complete");
            sb.AppendLine("--------------------------");
            sb.AppendLine($"Assigned: {assigned}");
            if (assignedDetails.Count > 0)
            {
                int shown = 0;
                foreach (string s in assignedDetails)
                {
                    if (shown >= MaxDetailLines) break;
                    sb.AppendLine("  - " + s);
                    shown++;
                }
                if (assignedDetails.Count > MaxDetailLines)
                    sb.AppendLine($"  ... and {assignedDetails.Count - MaxDetailLines} more");
            }

            sb.AppendLine($"Skipped (existing/bad/skip): {skipped}");
            if (skippedDetails.Count > 0)
            {
                int shown = 0;
                foreach (string s in skippedDetails)
                {
                    if (shown >= MaxDetailLines) break;
                    sb.AppendLine("  - " + s);
                    shown++;
                }
                if (skippedDetails.Count > MaxDetailLines)
                    sb.AppendLine($"  ... and {skippedDetails.Count - MaxDetailLines} more");
            }

            sb.AppendLine($"No-structure found: {noStruct}");
            if (noStructDetails.Count > 0)
            {
                int shown = 0;
                foreach (string s in noStructDetails)
                {
                    if (shown >= MaxDetailLines) break;
                    sb.AppendLine("  - " + s);
                    shown++;
                }
                if (noStructDetails.Count > MaxDetailLines)
                    sb.AppendLine($"  ... and {noStructDetails.Count - MaxDetailLines} more");
            }

            sb.AppendLine();
            sb.AppendLine("Options");
            sb.AppendLine("-------");
            sb.AppendLine($"Overwrite existing: {opts.OverwriteAssignments}");
            sb.AppendLine($"Prompt on multiple: {opts.PromptWhenMultipleStructures}");
            sb.AppendLine($"Convert polylines: {opts.ConvertPolylines}");
            if (opts.ConvertPolylines)
            {
                sb.AppendLine($"Converted: {converted}");
				if (polyIssues.Count > 0)
				{
					sb.AppendLine($"Conversion issues: {polyIssues.Count}");
					foreach (var it in polyIssues.Take(20))
						sb.AppendLine($"  - Polyline {it.Handle} ({it.Layer})");
					if (polyIssues.Count > 20)
						sb.AppendLine($"  - ... {polyIssues.Count - 20} more");
				}
                sb.AppendLine($"Only process created: {opts.OnlyProcessCreatedCatchments}");
            }
            sb.AppendLine();
            sb.AppendLine($"Pipe network scope: {(opts.PipeNetworkId.HasValue ? "Selected network" : "All networks")}");
            sb.AppendLine($"Catchment scope: {(opts.TargetCatchmentGroupId.HasValue ? "Selected group" : "All groups")}");

            string summary = sb.ToString();
            ed.WriteMessage("\n" + summary);

            // Show polyline conversion issues (modeless so users can click Civil 3D while it stays open).
            if (polyIssues.Count > 0)
            {
                try
                {
                    if (_polyIssuesWin != null)
                    {
                        try { _polyIssuesWin.Close(); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure close poly issues: {ex.Message}"); }
                        _polyIssuesWin = null;
                    }

                    _polyIssuesWin = new PolylineIssuesWindow(polyIssues);
                    _polyIssuesWin.Closed += (_, __) => _polyIssuesWin = null;
                    ShowModelessWindowSafe(_polyIssuesWin);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Catchment2Structure show poly issues window: {ex.Message}");
                }
            }

            try
            {
                var rw = new ResultsWindow(summary);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(rw);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure show results window: {ex.Message}");
            }
        }

        // ------------------------- UI backing lists -------------------------

        private static List<NamedId> GetPipeNetworks(Transaction tr, CivilDocument civDoc)
        {
            var list = new List<NamedId>();
            foreach (ObjectId netId in civDoc.GetPipeNetworkIds())
            {
                var net = tr.GetObject(netId, OpenMode.ForRead) as Network;
                if (net == null) continue;
                list.Add(new NamedId(net.Name, netId));
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<NamedId> GetCatchmentGroups(Transaction tr, CivilDocument civDoc)
        {
            var list = new List<NamedId>();
            var groups = civDoc.GetCatchmentGroups();
            for (int i = 0; i < groups.Count; i++)
            {
                ObjectId gid = groups[i];
                var g = tr.GetObject(gid, OpenMode.ForRead) as CatchmentGroup;
                if (g == null) continue;
                list.Add(new NamedId(g.Name, gid));
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<NamedId> GetCatchmentStyles(Transaction tr, CivilDocument civDoc)
        {
            var list = new List<NamedId>();
            CatchmentStyleCollection styles = civDoc.Styles.CatchmentStyles;
            for (int i = 0; i < styles.Count; i++)
            {
                ObjectId sid = styles[i];
                var s = tr.GetObject(sid, OpenMode.ForRead) as CatchmentStyle;
                if (s == null) continue;
                list.Add(new NamedId(s.Name, sid));
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<NamedId> GetSurfaces(Transaction tr, CivilDocument civDoc)
        {
            var list = new List<NamedId>();
            foreach (ObjectId sid in civDoc.GetSurfaceIds())
            {
                var s = tr.GetObject(sid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                if (s == null) continue;
                list.Add(new NamedId(s.Name, sid));
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> GetLayerNames(Transaction tr, Database db)
        {
            var list = new List<string>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                list.Add(ltr.Name);
            }
            return list;
        }

        // ------------------------- Polyline conversion -------------------------

        private static ObjectId ResolveCreateGroupId(Transaction tr, CivilDocument civDoc, RunOptions opts)
        {
            if (opts.CreateCatchmentGroupId.HasValue)
                return opts.CreateCatchmentGroupId.Value;

            // Create new group
            var groups = civDoc.GetCatchmentGroups();
            return groups.Add(opts.NewCatchmentGroupName);
        }

		private static List<ObjectId> ConvertClosedPolylinesToCatchments(Transaction tr, CivilDocument civDoc, Database db, RunOptions opts, ObjectId catchmentGroupId, List<PolylineIssue> issues)
        {
            var created = new List<ObjectId>();

            var layerSet = new HashSet<string>(opts.LayersToConvert, StringComparer.OrdinalIgnoreCase);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

			issues ??= new List<PolylineIssue>();

			int idx = 1;
            foreach (ObjectId entId in ms)
            {
                var pl = tr.GetObject(entId, OpenMode.ForRead) as Polyline;
                if (pl == null) continue;
                if (!pl.Closed) continue;
                if (!layerSet.Contains(pl.Layer)) continue;

                var pts = new Point3dCollection();
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    Point2d p2 = pl.GetPoint2dAt(i);
                    pts.Add(new Point3d(p2.X, p2.Y, 0.0));
                }

                // Ensure closure
                if (pts.Count >= 2)
                {
                    Point3d first = pts[0];
                    Point3d last = pts[pts.Count - 1];
                    if (!first.IsEqualTo(last))
                        pts.Add(first);
                }

				try
				{
					string name = $"CATCH-{idx:0000}";
					ObjectId newCatchmentId = Catchment.Create(name, opts.CatchmentStyleId, catchmentGroupId, opts.SurfaceId, pts);
					created.Add(newCatchmentId);
					idx++;

					if (opts.ErasePolylinesAfter)
					{
						pl.UpgradeOpen();
						pl.Erase();
					}
				}
				catch (System.Exception ex)
				{
					Point3d? focus = null;
					string reason = ex.Message;
					if (TryFindSelfIntersection2D(pts, out Point3d ip))
					{
						focus = ip;
						reason = $"{ex.Message} (intersection near X={ip.X:0.###}, Y={ip.Y:0.###})";
					}
					issues.Add(new PolylineIssue(pl.ObjectId, pl.Handle.ToString(), pl.Layer, pl.NumberOfVertices, reason, focus));
					continue;
				}
            }

            return created;
        }

		private static bool TryFindSelfIntersection2D(Point3dCollection pts, out Point3d intersection)
		{
			intersection = default;
			if (pts == null || pts.Count < 4) return false;
			// Skip full O(n²) check for very large polylines to avoid long freezes.
			if (pts.Count > MaxVerticesForSelfIntersectionCheck) return false;

			int segCount = pts.Count - 1; // last point equals first
			for (int i = 0; i < segCount; i++)
			{
				var a1 = pts[i];
				var a2 = pts[i + 1];

				for (int j = i + 1; j < segCount; j++)
				{
					// Skip adjacent segments (share a vertex) and the wrap-around adjacency.
					if (Math.Abs(i - j) <= 1) continue;
					if (i == 0 && j == segCount - 1) continue;

					var b1 = pts[j];
					var b2 = pts[j + 1];

					if (TryIntersectSegments2D(a1, a2, b1, b2, out Point2d ip))
					{
						intersection = new Point3d(ip.X, ip.Y, 0.0);
						return true;
					}
				}
			}

			return false;
		}

		private static bool TryIntersectSegments2D(Point3d a1, Point3d a2, Point3d b1, Point3d b2, out Point2d ip)
		{
			ip = default;

			// Line segment intersection (2D), robust enough for typical CAD polylines.
			double x1 = a1.X, y1 = a1.Y;
			double x2 = a2.X, y2 = a2.Y;
			double x3 = b1.X, y3 = b1.Y;
			double x4 = b2.X, y4 = b2.Y;

			double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
			if (Math.Abs(den) < 1e-12)
				return false; // parallel/colinear

			double pre = (x1 * y2 - y1 * x2);
			double post = (x3 * y4 - y3 * x4);
			double x = (pre * (x3 - x4) - (x1 - x2) * post) / den;
			double y = (pre * (y3 - y4) - (y1 - y2) * post) / den;

			// Check if intersection point lies within both segments (with tolerance)
			const double tol = 1e-9;
			if (x < Math.Min(x1, x2) - tol || x > Math.Max(x1, x2) + tol) return false;
			if (y < Math.Min(y1, y2) - tol || y > Math.Max(y1, y2) + tol) return false;
			if (x < Math.Min(x3, x4) - tol || x > Math.Max(x3, x4) + tol) return false;
			if (y < Math.Min(y3, y4) - tol || y > Math.Max(y3, y4) + tol) return false;

			// Exclude pure endpoint touching between adjacent segments; we already skip adjacent, but keep a small guard.
			ip = new Point2d(x, y);
			return true;
		}

        // ------------------------- Scope enumerators -------------------------

        private static IEnumerable<ObjectId> EnumerateCatchmentsForScope(Transaction tr, CivilDocument civDoc, ObjectId? groupId)
        {
            if (groupId.HasValue)
            {
                var g = tr.GetObject(groupId.Value, OpenMode.ForRead) as CatchmentGroup;
                if (g == null) yield break;

                foreach (ObjectId cid in g.GetAllCatchmentIds())
                    yield return cid;

                yield break;
            }

            var groups = civDoc.GetCatchmentGroups();
            for (int i = 0; i < groups.Count; i++)
            {
                var g = tr.GetObject(groups[i], OpenMode.ForRead) as CatchmentGroup;
                if (g == null) continue;

                foreach (ObjectId cid in g.GetAllCatchmentIds())
                    yield return cid;
            }
        }

        private static List<(ObjectId Id, Point2d XY, Point3d XYZ)> LoadStructuresForScope(Transaction tr, CivilDocument civDoc, ObjectId? pipeNetworkId)
        {
            var result = new List<(ObjectId Id, Point2d XY, Point3d XYZ)>();

            IEnumerable<ObjectId> nets = pipeNetworkId.HasValue
                ? new[] { pipeNetworkId.Value }
                : civDoc.GetPipeNetworkIds().Cast<ObjectId>();

            foreach (ObjectId netId in nets)
            {
                var net = tr.GetObject(netId, OpenMode.ForRead) as Network;
                if (net == null) continue;

                ObjectIdCollection structIds = net.GetStructureIds();
                foreach (ObjectId sid in structIds)
                {
                    var s = tr.GetObject(sid, OpenMode.ForRead) as Structure;
                    if (s == null) continue;

                    Point3d p = s.Position;
                    result.Add((sid, new Point2d(p.X, p.Y), p));
                }
            }

            return result;
        }

        // ------------------------- Nearest-by-discharge selection -------------------------

        private static Point2d GetCatchmentDecisionPoint(Catchment c, Point2dCollection poly)
        {
            const string fallbackMessage = "Catchment2Structure: Discharge point not found via API; using polygon centroid.";
            try
            {
                var t = c.GetType();
                object? v = t.GetProperty("DischargePoint")?.GetValue(c) ??
                    t.GetProperty("DischargePointLocation")?.GetValue(c) ??
                    t.GetProperty("DischargePointPoint")?.GetValue(c) ??
                    t.GetProperty("OutfallPoint")?.GetValue(c);

                if (v is Point3d p3) return new Point2d(p3.X, p3.Y);
                if (v is Point2d p2) return p2;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"{fallbackMessage} {ex.Message}");
                return Geo.Centroid(poly);
            }

            System.Diagnostics.Trace.WriteLine(fallbackMessage);
            return Geo.Centroid(poly);
        }

        private static ObjectId PickNearestStructure(Point2d from, List<ObjectId> candidateIds, Transaction tr)
        {
            ObjectId best = ObjectId.Null;
            double bestD2 = double.MaxValue;

            foreach (ObjectId id in candidateIds)
            {
                var s = tr.GetObject(id, OpenMode.ForRead) as Structure;
                if (s == null) continue;

                Point3d p = s.Position;
                double dx = p.X - from.X;
                double dy = p.Y - from.Y;
                double d2 = dx * dx + dy * dy;

                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = id;
                }
            }

            return best;
        }

        
        
                        
        private static void ZoomToExtents(Editor ed, Extents3d extWcs)
        {
            if (ed == null)
                return;

            try
            {
                ViewTableRecord view = ed.GetCurrentView();

                // Transform WCS extents corners into DCS using the current view definition.
                // Build WCS->DCS using a clear, ordered transform sequence.
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

                const double pad = 1.25;
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
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ZoomToExtents: {ex.Message}");
            }
        }


        private static void ZoomToCatchment(Editor ed, Point2dCollection poly)
        {
            if (ed == null || poly == null || poly.Count < 2)
                return;

            try
            {
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

                for (int i = 0; i < poly.Count; i++)
                {
                    Point2d p = poly[i];
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }

                if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
                    return;

                var ext = new Extents3d(new Point3d(minX, minY, 0.0), new Point3d(maxX, maxY, 0.0));
                ZoomToExtents(ed, ext);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ZoomToCatchment: {ex.Message}");
            }
        }

        internal static void ZoomToObjectIds(Editor ed, Transaction tr, ObjectId[] ids)
        {
            if (ed == null || tr == null || ids == null || ids.Length == 0)
                return;

            try
            {
                Extents3d? ext = null;

                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent == null) continue;

                    try
                    {
                        Extents3d e = ent.GeometricExtents;
                        if (ext == null) ext = e;
                        else
                        {
                            var ex = ext.Value;
                            ex.AddExtents(e);
                            ext = ex;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"Catchment2Structure ZoomToObjectIds extents: {ex.Message}");
                    }
                }

                if (ext == null) return;
                ZoomToExtents(ed, ext.Value);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ZoomToObjectIds: {ex.Message}");
            }
        }

        internal static ObjectId PromptPickAnyStructure(Editor ed, Transaction tr)
        {
            const string msg = "\nSelect a structure to associate (Enter/Esc to skip): ";

            while (true)
            {
                var peo = new PromptEntityOptions(msg) { AllowNone = true };
                peo.SetRejectMessage("\nPlease select a Civil 3D Structure object.");
                peo.AddAllowedClass(typeof(Autodesk.Civil.DatabaseServices.Structure), exactMatch: false);

                PromptEntityResult per = ed.GetEntity(peo);

                if (per.Status == PromptStatus.None || per.Status == PromptStatus.Cancel)
                    return ObjectId.Null;

                if (per.Status != PromptStatus.OK)
                    return ObjectId.Null;

                var s = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Structure;
                if (s != null)
                    return per.ObjectId;

                ed.WriteMessage("\nSelected object is not a Structure. Try again, or press Enter to skip.");
            }
        }

        internal static ObjectId PromptPickStructureFromCandidates(Editor ed, Transaction tr, List<ObjectId> candidates, ObjectId defaultId)
        {
            if (candidates == null || candidates.Count == 0)
                return ObjectId.Null;

            const string msg = "\nMultiple structures inside catchment. Select the correct structure (Enter/Esc to skip): ";

            while (true)
            {
                var peo = new PromptEntityOptions(msg) { AllowNone = true };
                peo.SetRejectMessage("\nPlease select one of the highlighted candidate structures.");
                peo.AddAllowedClass(typeof(Autodesk.Civil.DatabaseServices.Structure), exactMatch: false);

                PromptEntityResult per = ed.GetEntity(peo);

                if (per.Status == PromptStatus.None || per.Status == PromptStatus.Cancel)
                    return ObjectId.Null;

                if (per.Status != PromptStatus.OK)
                    return ObjectId.Null;

                if (!candidates.Contains(per.ObjectId))
                {
                    ed.WriteMessage("\nThat structure is not one of the candidates for this catchment. Try again, or press Enter to skip.");
                    continue;
                }

                var s = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Structure;
                if (s != null)
                    return per.ObjectId;

                ed.WriteMessage("\nSelected object is not a Structure. Try again, or press Enter to skip.");
            }
        }


// ------------------------- Multi-structure selection -------------------------

        private static ObjectId PromptUserToSelectStructure(Editor ed, Transaction tr, List<ObjectId> candidateIds, ObjectId defaultId)
        {
            var highlighted = new List<Autodesk.AutoCAD.DatabaseServices.Entity>();

            try
            {
                foreach (ObjectId id in candidateIds)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent == null) continue;
                    ent.Highlight();
                    highlighted.Add(ent);
                }

                const string msg = "\nMultiple structures found. Select structure (Enter = use nearest, Esc = skip): ";

                while (true)
                {
                    var peo = new PromptEntityOptions(msg) { AllowNone = true };
                    PromptEntityResult per = ed.GetEntity(peo);

                    if (per.Status == PromptStatus.None)
                        return defaultId;

                    if (per.Status != PromptStatus.OK)
                        return ObjectId.Null;

                    if (candidateIds.Contains(per.ObjectId))
                        return per.ObjectId;

                    ed.WriteMessage("\nThat object is not one of the highlighted candidate structures. Please select a highlighted structure (or press Enter to use nearest).");
                }
            }
            finally
            {
                foreach (var ent in highlighted)
                {
                    try { ent.Unhighlight(); } catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"Catchment2Structure Unhighlight: {ex.Message}"); }
                }
            }
        }
    }
}
