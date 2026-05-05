using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Autodesk.AutoCAD.DatabaseServices;

namespace Catchment2Structure
{
    public sealed class NamedId
    {
        public string Name { get; }
        public ObjectId Id { get; }

        public NamedId(string name, ObjectId id)
        {
            Name = name ?? "";
            Id = id;
        }

        public override string ToString() => Name;
    }

    public sealed class LayerItem
    {
        public string Name { get; set; } = "";
        public bool IsSelected { get; set; }
    }

    public sealed class RunOptions
    {
        // Assignment scope/options
        public ObjectId? PipeNetworkId { get; set; }
        public ObjectId? TargetCatchmentGroupId { get; set; }
        public bool OverwriteAssignments { get; set; }
        public bool PromptWhenMultipleStructures { get; set; }
        public bool PromptWhenNoStructureFound { get; set; }

        // Optional conversion
        public bool ConvertPolylines { get; set; }
        public List<string> LayersToConvert { get; set; } = new List<string>();
        public ObjectId CatchmentStyleId { get; set; } = ObjectId.Null;
        public ObjectId SurfaceId { get; set; } = ObjectId.Null;
        public bool ErasePolylinesAfter { get; set; }
        public bool OnlyProcessCreatedCatchments { get; set; }

        // Catchment group for newly created catchments
        public ObjectId? CreateCatchmentGroupId { get; set; } // existing group
        public string NewCatchmentGroupName { get; set; } = "Catchments";
    }

    public partial class OptionsWindow : Window
    {
        private readonly List<NamedId> _pipeNetworks;
        private readonly List<NamedId> _catchmentGroups;
        private readonly List<NamedId> _catchmentStyles;
        private readonly List<NamedId> _surfaces;
        private readonly List<LayerItem> _layers;

        public RunOptions? Options { get; private set; }

        private readonly Action<RunOptions>? _onRun;
        private readonly Action? _onClosed;

        public OptionsWindow(
            List<NamedId> pipeNetworks,
            List<NamedId> catchmentGroups,
            List<NamedId> catchmentStyles,
            List<NamedId> surfaces,
            List<string> layerNames,
            Action<RunOptions>? onRun = null,
            Action? onClosed = null)
        {
            InitializeComponent();

            _onRun = onRun;
            _onClosed = onClosed;
            Closed += (_, __) => _onClosed?.Invoke();

            _pipeNetworks = pipeNetworks ?? new List<NamedId>();
            _catchmentGroups = catchmentGroups ?? new List<NamedId>();
            _catchmentStyles = catchmentStyles ?? new List<NamedId>();
            _surfaces = surfaces ?? new List<NamedId>();
            _layers = (layerNames ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new LayerItem { Name = s, IsSelected = false })
                .ToList();

            // Bind picklists
            PipeNetworkCombo.ItemsSource = _pipeNetworks;
            CatchmentGroupCombo.ItemsSource = _catchmentGroups;
            CatchmentStyleCombo.ItemsSource = _catchmentStyles;
            SurfaceCombo.ItemsSource = _surfaces;

            // Group selection for newly created catchments: existing groups + "Create new..."
            var createGroupItems = new List<object> { "(Create new catchment group)" };
            createGroupItems.AddRange(_catchmentGroups.Cast<object>());
            CreateGroupCombo.ItemsSource = createGroupItems;

            // Layers grid
            LayersGrid.ItemsSource = _layers;

            // Defaults
            if (_pipeNetworks.Count > 0) PipeNetworkCombo.SelectedIndex = 0;
            if (_catchmentGroups.Count > 0) CatchmentGroupCombo.SelectedIndex = 0;
            if (_catchmentStyles.Count > 0) CatchmentStyleCombo.SelectedIndex = 0;
            if (_surfaces.Count > 0) SurfaceCombo.SelectedIndex = 0;

            CreateGroupCombo.SelectedIndex = 0;
            NewGroupNameText.Text = "Catchments";
            NewGroupNameText.IsEnabled = true;

            OverwriteCheck.IsChecked = false;
            PromptMultiCheck.IsChecked = true;

            ConvertPolylinesCheck.IsChecked = false;
            ErasePolylinesCheck.IsChecked = false;
            OnlyProcessCreatedCheck.IsChecked = true;

            ConvertPolylinesCheck_Checked(this, new RoutedEventArgs());

            UpdateSummaryPreview();
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var opts = new RunOptions();

            // Assignment scope
            opts.PipeNetworkId = (PipeNetworkCombo.SelectedItem as NamedId)?.Id;
            opts.TargetCatchmentGroupId = (CatchmentGroupCombo.SelectedItem as NamedId)?.Id;
            opts.OverwriteAssignments = OverwriteCheck.IsChecked == true;
            opts.PromptWhenMultipleStructures = PromptMultiCheck.IsChecked == true;
            opts.PromptWhenNoStructureFound = PromptNoStructCheck.IsChecked == true;

            // Conversion
            opts.ConvertPolylines = ConvertPolylinesCheck.IsChecked == true;
            opts.ErasePolylinesAfter = ErasePolylinesCheck.IsChecked == true;
            opts.OnlyProcessCreatedCatchments = OnlyProcessCreatedCheck.IsChecked == true;

            // Layers
            opts.LayersToConvert = _layers.Where(l => l.IsSelected).Select(l => l.Name).ToList();

            // Style/surface
            opts.CatchmentStyleId = (CatchmentStyleCombo.SelectedItem as NamedId)?.Id ?? ObjectId.Null;
            opts.SurfaceId = (SurfaceCombo.SelectedItem as NamedId)?.Id ?? ObjectId.Null;

            // Group choice for created catchments
            if (CreateGroupCombo.SelectedItem is NamedId gid)
            {
                opts.CreateCatchmentGroupId = gid.Id;
                opts.NewCatchmentGroupName = gid.Name;
            }
            else
            {
                opts.CreateCatchmentGroupId = null;
                opts.NewCatchmentGroupName = (NewGroupNameText.Text ?? "Catchments").Trim();
                if (string.IsNullOrWhiteSpace(opts.NewCatchmentGroupName))
                    opts.NewCatchmentGroupName = "Catchments";
            }

            Options = opts;

            // In modeless mode, Window.DialogResult cannot be set.
            // We preserve the Options property for backwards compatibility and
            // invoke the provided callback to trigger the command-side execution.
            _onRun?.Invoke(opts);
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Options = null;
            Close();
        }

        private void ConvertPolylinesCheck_Checked(object sender, RoutedEventArgs e)
        {
            bool enabled = ConvertPolylinesCheck.IsChecked == true;

            LayersGrid.IsEnabled = enabled;
            ErasePolylinesCheck.IsEnabled = enabled;
            OnlyProcessCreatedCheck.IsEnabled = enabled;
            CatchmentStyleCombo.IsEnabled = enabled;
            SurfaceCombo.IsEnabled = enabled;
            CreateGroupCombo.IsEnabled = enabled;
            NewGroupNameText.IsEnabled = enabled && (CreateGroupCombo.SelectedItem is string);
            UpdateSummaryPreview();
        }

        private void SelectAllLayers_Click(object sender, RoutedEventArgs e)
        {
            foreach (var li in _layers) li.IsSelected = true;
            LayersGrid.Items.Refresh();
            UpdateSummaryPreview();
        }

        private void SelectNoLayers_Click(object sender, RoutedEventArgs e)
        {
            foreach (var li in _layers) li.IsSelected = false;
            LayersGrid.Items.Refresh();
            UpdateSummaryPreview();
        }
        private void CreateGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            NewGroupNameText.IsEnabled = ConvertPolylinesCheck.IsChecked == true && (CreateGroupCombo.SelectedItem is string);
            UpdateSummaryPreview();
        }

        private void UpdateSummaryPreview()
        {
            if (SummaryBox == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Current Options");
            sb.AppendLine("--------------");
            sb.AppendLine($"Pipe Network: {(PipeNetworkCombo.SelectedItem as NamedId)?.Name ?? "(none)"}");
            sb.AppendLine($"Catchment Group: {(CatchmentGroupCombo.SelectedItem as NamedId)?.Name ?? "(none)"}");
            sb.AppendLine($"Overwrite existing: {(OverwriteCheck.IsChecked == true ? "Yes" : "No")}");
            sb.AppendLine($"Prompt when multiple: {(PromptMultiCheck.IsChecked == true ? "Yes" : "No")}");
            sb.AppendLine($"Prompt when none inside: {(PromptNoStructCheck.IsChecked == true ? "Yes" : "No")}");

            bool conv = ConvertPolylinesCheck.IsChecked == true;
            sb.AppendLine();
            sb.AppendLine($"Convert closed polylines: {(conv ? "Yes" : "No")}");

            if (conv)
            {
                sb.AppendLine($"Layers selected: {_layers.Count(l => l.IsSelected)}");
                sb.AppendLine($"Catchment style: {(CatchmentStyleCombo.SelectedItem as NamedId)?.Name ?? "(none)"}");
                sb.AppendLine($"Surface: {(SurfaceCombo.SelectedItem as NamedId)?.Name ?? "(none)"}");
                sb.AppendLine($"Erase polylines: {(ErasePolylinesCheck.IsChecked == true ? "Yes" : "No")}");
                sb.AppendLine($"Only process created: {(OnlyProcessCreatedCheck.IsChecked == true ? "Yes" : "No")}");

                if (CreateGroupCombo.SelectedItem is NamedId gid)
                    sb.AppendLine($"Create in group: {gid.Name}");
                else
                    sb.AppendLine($"Create group name: {(NewGroupNameText.Text ?? "").Trim()}");
            }

            SummaryBox.Text = sb.ToString();
        }
    }
}
