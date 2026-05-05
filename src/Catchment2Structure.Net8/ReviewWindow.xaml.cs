using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace Catchment2Structure
{
    public partial class ReviewWindow : Window
    {
        private readonly List<ReviewItem> _items;
        private ObjectId[] _lastHighlighted = Array.Empty<ObjectId>();

        // When true, the caller should proceed to the final summary.
        public bool Finished { get; private set; }

        // When non-null, the caller should perform an assignment prompt for this item,
        // then re-open the review window.
        public ReviewItem? AssignRequestedItem { get; private set; }

        public ReviewWindow(List<ReviewItem> items)
        {
            InitializeComponent();
            _items = items ?? new List<ReviewItem>();
            ItemsGrid.ItemsSource = _items;
            UpdateButtons();
        }

        private ReviewItem? SelectedItem => ItemsGrid.SelectedItem as ReviewItem;

        private void UpdateButtons()
        {
            bool has = SelectedItem != null;
            GoToBtn.IsEnabled = has;
            AssignBtn.IsEnabled = has;
            SkipBtn.IsEnabled = has;
        }

        private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void GoTo_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedItem;
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

                    var ids = new List<ObjectId> { it.CatchmentId };
                    if (it.CandidateStructureIds != null && it.CandidateStructureIds.Count > 0)
                        ids.AddRange(it.CandidateStructureIds);

                    CommandsNet8.ZoomToObjectIds(ed, tr, ids.ToArray());
                    Highlight(tr, ids);

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ReviewWindow GoTo: {ex.Message}");
            }
        }

        private void Assign_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedItem;
            if (it == null) return;

            // Request assignment from the command context (so we can prompt in the editor safely).
            AssignRequestedItem = it;
            Finished = false;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedItem;
            if (it == null) return;

            it.IsResolved = true;
            it.IsAssigned = false;
            it.Notes = "Skipped";
            ItemsGrid.Items.Refresh();
            UpdateButtons();
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            Finished = true;
            AssignRequestedItem = null;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // Treat Close as Finish so the command can continue to summary.
            Finished = true;
            AssignRequestedItem = null;

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
                System.Diagnostics.Trace.WriteLine($"Catchment2Structure ReviewWindow Close unhighlight: {ex.Message}");
            }

            Close();
        }

        private void Highlight(Transaction tr, IEnumerable<ObjectId> ids)
        {
            ObjectId[] arr = (ids ?? Array.Empty<ObjectId>()).ToArray();
            _lastHighlighted = arr;

            foreach (ObjectId id in arr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                ent?.Highlight();
            }
        }

        private void UnhighlightLast(Transaction tr)
        {
            if (_lastHighlighted == null || _lastHighlighted.Length == 0)
                return;

            foreach (ObjectId id in _lastHighlighted)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                ent?.Unhighlight();
            }

            _lastHighlighted = Array.Empty<ObjectId>();
        }
    }
}
