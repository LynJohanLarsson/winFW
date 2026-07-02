using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Core.Models;
using WinFWManager.Core.Services;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class DashboardView : UserControl
{
    private readonly DashboardViewModel _vm;

    // Redraws are deferred while a tooltip is open: rebuilding the canvas
    // destroys the tooltip's owner element mid-hover, which reads as a
    // "blinking" popup. The pending redraw runs as soon as the tooltip closes.
    private int _openTooltips;
    private bool _redrawPending;

    public DashboardView()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<DashboardViewModel>();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.GraphData))
            RedrawGraph();
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawGraph();
    }

    private void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.ClearDrillCommand.CanExecute(null))
        {
            _vm.ClearDrillCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Tracks open/close on the ToolTip itself so redraws can be
    /// deferred while the user is reading a popup. The ToolTip's own
    /// Opened/Closed events fire regardless of HOW it closes (mouse leave,
    /// programmatic IsOpen=false, ...), unlike the owner element's
    /// ToolTipClosing — relying on the latter leaks the counter and freezes
    /// the graph permanently.</summary>
    private ToolTip TrackTooltip(ToolTip tooltip)
    {
        tooltip.Opened += (_, _) => _openTooltips++;
        tooltip.Closed += (_, _) =>
        {
            _openTooltips = Math.Max(0, _openTooltips - 1);
            if (_openTooltips == 0 && _redrawPending)
            {
                _redrawPending = false;
                RedrawGraph();
            }
        };
        return tooltip;
    }

    /// <summary>Force-closes an element's tooltip (before a click mutates the
    /// graph) so the resulting redraw is not deferred behind the open popup.</summary>
    private static void CloseTooltip(FrameworkElement element)
    {
        if (element.ToolTip is ToolTip tt && tt.IsOpen)
            tt.IsOpen = false;
    }

    private void RedrawGraph()
    {
        if (_openTooltips > 0)
        {
            _redrawPending = true;
            return;
        }

        GraphCanvas.Children.Clear();

        var data = _vm.GraphData;
        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;

        if (w < 100 || h < 100 || data == null)
        {
            AddEmptyState(w, h);
            return;
        }

        var processNodes = data.Nodes.Where(n => n.Kind == GraphNodeKind.Process).ToList();
        var adapterNodes = data.Nodes.Where(n => n.Kind == GraphNodeKind.Adapter).ToList();
        var remoteNodes = data.Nodes
            .Where(n => n.Kind is GraphNodeKind.Remote or GraphNodeKind.RemoteGroup)
            .ToList();

        if (processNodes.Count == 0 && adapterNodes.Count == 0 && remoteNodes.Count == 0)
        {
            AddEmptyState(w, h);
            return;
        }

        // Theme brushes
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var dangerBrush = (SolidColorBrush)FindResource("DangerBrush");
        var primaryText = (SolidColorBrush)FindResource("PrimaryTextBrush");
        var secondaryText = (SolidColorBrush)FindResource("SecondaryTextBrush");
        var secondaryBg = (SolidColorBrush)FindResource("SecondaryBgBrush");
        var tertiaryBg = (SolidColorBrush)FindResource("TertiaryBgBrush");
        var wslBrush = (SolidColorBrush)FindResource("WslBrush");
        var hypervBrush = (SolidColorBrush)FindResource("HyperVBrush");

        // Three-column layout
        double procX = w * 0.08;
        double adapterX = w * 0.50;
        double remoteX = w * 0.92;
        double topPad = 20;
        double botPad = 20;
        double usableH = h - topPad - botPad;

        PositionColumn(processNodes, procX, topPad, usableH);
        PositionColumn(adapterNodes, adapterX, topPad, usableH);
        PositionColumn(remoteNodes, remoteX, topPad, usableH);

        var nodeLookup = data.Nodes.ToDictionary(n => n.Id, n => n);

        // Column headers
        AddLabel("PROCESSES", procX, 4, primaryText, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
        AddLabel("ADAPTERS", adapterX, 4, primaryText, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
        AddLabel("REMOTE ENDPOINTS", remoteX, 4, primaryText, 11, FontWeights.SemiBold, HorizontalAlignment.Center);

        // Edges (both layer pairs)
        foreach (var edge in data.Edges)
        {
            if (!nodeLookup.TryGetValue(edge.SourceId, out var src)) continue;
            if (!nodeLookup.TryGetValue(edge.TargetId, out var tgt)) continue;

            double thickness = Math.Max(1.5, (double)edge.TotalCount / data.MaxEdgeCount * 6.0);
            bool fullyBlocked = edge.AllowedCount == 0 && edge.BlockedCount > 0;
            var edgeBrush = edge.BlockedCount > edge.AllowedCount ? dangerBrush : successBrush;

            var line = new Line
            {
                X1 = src.X + 8,
                Y1 = src.Y,
                X2 = tgt.X - 8,
                Y2 = tgt.Y,
                Stroke = edgeBrush,
                StrokeThickness = thickness,
                Opacity = 0.5
            };
            if (fullyBlocked)
                line.StrokeDashArray = new DoubleCollection { 4, 3 };
            GraphCanvas.Children.Add(line);

            var tooltip = TrackTooltip(
                BuildEdgeTooltip(edge, src.Label, tgt.Label, primaryText, secondaryText, secondaryBg, tertiaryBg));

            // Invisible wider hit-test line for easy hovering
            var hitLine = new Line
            {
                X1 = src.X + 8,
                Y1 = src.Y,
                X2 = tgt.X - 8,
                Y2 = tgt.Y,
                Stroke = Brushes.Transparent,
                StrokeThickness = Math.Max(14, thickness + 8),
                ToolTip = tooltip
            };
            hitLine.MouseEnter += (_, _) => { line.Opacity = 0.9; line.StrokeThickness = thickness + 2; };
            hitLine.MouseLeave += (_, _) => { line.Opacity = 0.5; line.StrokeThickness = thickness; };
            GraphCanvas.Children.Add(hitLine);
        }

        // Process nodes: small circles, tertiary fill with accent border.
        foreach (var node in processNodes)
        {
            bool isBucket = node.Label == TrafficGraphBuilder.SystemProcessLabel
                            || node.Label == TrafficGraphBuilder.OthersProcessLabel;
            var edges = data.Edges.Where(e => e.SourceId == node.Id).ToList();
            DrawNode(node, 10, tertiaryBg, primaryText, secondaryText, secondaryBg, tertiaryBg,
                edges, stroke: accentBrush);

            var label = $"{node.Label}  ({node.ConnectionCount})";
            var tb = AddLabel(label, node.X + 14, node.Y - 8,
                isBucket ? secondaryText : primaryText, 11, FontWeights.Normal, HorizontalAlignment.Left);
            if (isBucket)
                tb.FontStyle = FontStyles.Italic;
        }

        // Adapter nodes: color by adapter type, label below the node.
        foreach (var node in adapterNodes)
        {
            var fill = node.AdapterType switch
            {
                AdapterType.WSL => wslBrush,
                AdapterType.HyperV or AdapterType.VSwitch => hypervBrush,
                _ => accentBrush
            };

            var edges = data.Edges.Where(e => e.SourceId == node.Id).ToList();
            DrawNode(node, 14, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, edges);

            var label = $"{node.Label}  ({node.ConnectionCount})";
            AddLabel(label, node.X, node.Y + 10, primaryText, 11, FontWeights.Normal, HorizontalAlignment.Center);
        }

        // Remote layer: group nodes, "+N more" nodes and individual remotes.
        foreach (var node in remoteNodes)
        {
            var nodeEdges = data.Edges.Where(e => e.TargetId == node.Id).ToList();

            if (node.Kind == GraphNodeKind.RemoteGroup)
            {
                bool isMore = node.Id.StartsWith("more:", StringComparison.Ordinal);
                // Expanded group header ("group:" with IsExpanded): a compact,
                // dimmed collapse affordance — the member nodes carry the data.
                bool isHeader = !isMore && node.IsExpanded;

                Brush fill;
                if (isMore)
                {
                    fill = secondaryText;
                }
                else
                {
                    var groupBrush = node.Group switch
                    {
                        RemoteGroupKind.WslGuest => wslBrush,
                        RemoteGroupKind.Lan => successBrush,
                        _ => accentBrush
                    };
                    fill = isHeader ? Dimmed(groupBrush, 0.45)
                        : node.Group == RemoteGroupKind.Lan ? Dimmed(groupBrush)
                        : groupBrush;
                }

                double size = isMore ? 12 : isHeader ? 13 : 18;
                DrawNode(node, size, fill, primaryText, secondaryText,
                    secondaryBg, tertiaryBg, nodeEdges,
                    hint: isMore || isHeader ? "Click to collapse" : "Click to expand");

                bool dimLabel = isMore || isHeader;
                var tb = AddLabel(node.Label, node.X - (isMore ? 12 : isHeader ? 13 : 16),
                    node.Y - 8, dimLabel ? secondaryText : primaryText, dimLabel ? 10.5 : 11,
                    FontWeights.Normal, HorizontalAlignment.Right);
                if (isMore)
                    tb.FontStyle = FontStyles.Italic;
            }
            else
            {
                bool mostlyBlocked = nodeEdges.Count > 0
                    && nodeEdges.Sum(e => e.BlockedCount) > nodeEdges.Sum(e => e.AllowedCount);

                var fill = node.IsWslGuest ? wslBrush : mostlyBlocked ? dangerBrush : successBrush;
                DrawNode(node, 10, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, nodeEdges);

                var countryTag = !string.IsNullOrEmpty(node.Country) && node.Country != "Unknown"
                    ? $"  [{node.Country}]" : "";
                var label = $"({node.ConnectionCount})  {node.Label}{countryTag}";
                AddLabel(label, node.X - 14, node.Y - 8, secondaryText, 10.5,
                    FontWeights.Normal, HorizontalAlignment.Right);
            }
        }
    }

    private static void PositionColumn(List<GraphNode> nodes, double x, double topPad, double usableH)
    {
        if (nodes.Count == 0) return;
        double step = usableH / (nodes.Count + 1);
        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].X = x;
            nodes[i].Y = topPad + step * (i + 1);
        }
    }

    private static Brush Dimmed(SolidColorBrush brush, double opacity = 0.6)
    {
        var b = new SolidColorBrush(brush.Color) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static ToolTip BuildEdgeTooltip(GraphEdge edge, string sourceLabel, string targetLabel,
        Brush primaryText, Brush secondaryText, Brush bgBrush, Brush headerBg)
    {
        var panel = new StackPanel { MinWidth = 200 };
        panel.Children.Add(MakeTooltipHeader($"{sourceLabel}  →  {targetLabel}", primaryText, headerBg));

        // Stats
        var stats = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        stats.Children.Add(MakeStatRow("Total", edge.TotalCount.ToString(), primaryText, secondaryText));
        stats.Children.Add(MakeStatRow("Allowed", edge.AllowedCount.ToString(),
            (SolidColorBrush)new BrushConverter().ConvertFrom("#4CAF50")!, secondaryText));
        stats.Children.Add(MakeStatRow("Blocked", edge.BlockedCount.ToString(),
            (SolidColorBrush)new BrushConverter().ConvertFrom("#F44336")!, secondaryText));

        if (edge.TotalCount > 0)
        {
            var pct = (double)edge.AllowedCount / edge.TotalCount * 100;
            stats.Children.Add(MakeStatRow("Allow Rate", $"{pct:F0}%", secondaryText, secondaryText));
        }

        panel.Children.Add(stats);

        // Top Ports section
        if (edge.TopPorts.Count > 0)
        {
            panel.Children.Add(MakeSeparator(headerBg));

            var portsLabel = new TextBlock
            {
                Text = "Top Ports:",
                Foreground = secondaryText,
                FontSize = 11,
                Margin = new Thickness(10, 4, 10, 2)
            };
            panel.Children.Add(portsLabel);

            var allowBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#4CAF50")!;
            var blockBrush = (SolidColorBrush)new BrushConverter().ConvertFrom("#F44336")!;

            var portsList = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
            foreach (var p in edge.TopPorts)
            {
                var row = new TextBlock
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320
                };
                row.Inlines.Add(new System.Windows.Documents.Run($"{p.Port}/{p.Protocol}") { Foreground = primaryText, FontWeight = FontWeights.SemiBold });
                int allowed = p.Count - p.BlockedCount;
                if (allowed > 0)
                    row.Inlines.Add(new System.Windows.Documents.Run($"  ✓{allowed}") { Foreground = allowBrush });
                if (p.BlockedCount > 0)
                    row.Inlines.Add(new System.Windows.Documents.Run($"  ⛔{p.BlockedCount}") { Foreground = blockBrush });
                if (p.DropReasons.Count > 0)
                    row.Inlines.Add(new System.Windows.Documents.Run($"  — {string.Join(", ", p.DropReasons)}") { Foreground = secondaryText });
                portsList.Children.Add(row);
            }
            panel.Children.Add(portsList);
        }

        // Drop reasons not already attributed to a specific port above
        // (e.g. network-layer drops that carry no port information).
        var portReasons = edge.TopPorts.SelectMany(p => p.DropReasons).ToHashSet(StringComparer.Ordinal);
        var unattributed = edge.DropReasons.Where(r => !portReasons.Contains(r)).ToList();
        if (unattributed.Count > 0)
        {
            panel.Children.Add(MakeSeparator(headerBg));

            var reasonsList = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
            foreach (var reason in unattributed)
            {
                reasonsList.Children.Add(new TextBlock
                {
                    Text = $"⛔ {reason}",
                    Foreground = secondaryText,
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
            panel.Children.Add(reasonsList);
        }

        return WrapTooltip(panel, bgBrush, headerBg);
    }

    private static Border MakeTooltipHeader(string text, Brush primaryText, Brush headerBg) => new()
    {
        Background = headerBg,
        CornerRadius = new CornerRadius(4, 4, 0, 0),
        Padding = new Thickness(10, 6, 10, 6),
        Child = new TextBlock
        {
            Text = text,
            Foreground = primaryText,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        }
    };

    private static Border MakeSeparator(Brush headerBg) => new()
    {
        BorderBrush = headerBg,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Margin = new Thickness(8, 2, 8, 2)
    };

    private static ToolTip WrapTooltip(UIElement content, Brush bgBrush, Brush headerBg) => new()
    {
        Content = content,
        Background = bgBrush,
        BorderBrush = headerBg,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(0)
    };

    private static ToolTip BuildNodeTooltip(GraphNode node, List<GraphEdge> edges,
        Brush primaryText, Brush secondaryText, Brush bgBrush, Brush headerBg, string? hint)
    {
        var panel = new StackPanel { MinWidth = 220 };

        var icon = node.Kind switch
        {
            GraphNodeKind.Process => "⚙",          // gear
            GraphNodeKind.Adapter => "🖥",    // desktop computer
            _ => "🌐"                         // globe
        };
        panel.Children.Add(MakeTooltipHeader($"{icon}  {node.Label}", primaryText, headerBg));

        // Info
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };

        info.Children.Add(MakeStatRow("Connections", node.ConnectionCount.ToString(), primaryText, secondaryText));

        if (node.AdapterType != null)
            info.Children.Add(MakeStatRow("Type", node.AdapterType.ToString()!, secondaryText, secondaryText));
        if (!string.IsNullOrEmpty(node.Country) && node.Country != "Unknown")
            info.Children.Add(MakeStatRow("Country", node.Country, secondaryText, secondaryText));

        panel.Children.Add(info);

        // Connected nodes
        if (edges.Count > 0)
        {
            panel.Children.Add(MakeSeparator(headerBg));

            var connLabel = new TextBlock
            {
                Text = node.IsLocal ? "Talking to:" : "Via adapters:",
                Foreground = secondaryText,
                FontSize = 11,
                Margin = new Thickness(10, 4, 10, 2)
            };
            panel.Children.Add(connLabel);

            var connList = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
            foreach (var e in edges.OrderByDescending(e => e.TotalCount).Take(8))
            {
                var otherId = node.IsLocal ? e.TargetId : e.SourceId;
                var row = new TextBlock
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                row.Inlines.Add(new System.Windows.Documents.Run(StripIdPrefix(otherId)) { Foreground = primaryText });
                row.Inlines.Add(new System.Windows.Documents.Run($"  ({e.TotalCount})") { Foreground = secondaryText });
                connList.Children.Add(row);
            }
            if (edges.Count > 8)
            {
                connList.Children.Add(new TextBlock
                {
                    Text = $"...and {edges.Count - 8} more",
                    Foreground = secondaryText,
                    FontSize = 10,
                    FontStyle = FontStyles.Italic
                });
            }
            panel.Children.Add(connList);
        }

        if (hint != null)
        {
            panel.Children.Add(MakeSeparator(headerBg));
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = secondaryText,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(10, 4, 10, 8)
            });
        }

        return WrapTooltip(panel, bgBrush, headerBg);
    }

    private static string StripIdPrefix(string id)
    {
        int idx = id.IndexOf(':');
        return idx > 0 ? id[(idx + 1)..] : id;
    }

    private static Grid MakeStatRow(string label, string value, Brush valueBrush, Brush labelBrush)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock { Text = label, Foreground = labelBrush, FontSize = 11 };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock { Text = value, Foreground = valueBrush, FontWeight = FontWeights.SemiBold, FontSize = 11 };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        return grid;
    }

    private void DrawNode(GraphNode node, double size, Brush fill, Brush primaryText,
        Brush secondaryText, Brush bgBrush, Brush headerBg, List<GraphEdge> edges,
        Brush? stroke = null, string? hint = null)
    {
        var tooltip = TrackTooltip(
            BuildNodeTooltip(node, edges, primaryText, secondaryText, bgBrush, headerBg, hint));

        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            Stroke = stroke ?? fill,
            StrokeThickness = 1.5,
            Opacity = 0.9,
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        ellipse.MouseLeftButtonDown += (_, e) =>
        {
            CloseTooltip(ellipse);
            Focus();
            _vm.ToggleNode(node);
            e.Handled = true;
        };
        Canvas.SetLeft(ellipse, node.X - size / 2);
        Canvas.SetTop(ellipse, node.Y - size / 2);
        GraphCanvas.Children.Add(ellipse);

        // Larger invisible hit area for the node too
        var hitArea = new Ellipse
        {
            Width = size + 16,
            Height = size + 16,
            Fill = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        hitArea.MouseEnter += (_, _) => { ellipse.Opacity = 1.0; ellipse.StrokeThickness = 3; };
        hitArea.MouseLeave += (_, _) => { ellipse.Opacity = 0.9; ellipse.StrokeThickness = 1.5; };
        hitArea.MouseLeftButtonDown += (_, e) =>
        {
            CloseTooltip(hitArea);
            Focus();
            _vm.ToggleNode(node);
            e.Handled = true;
        };
        Canvas.SetLeft(hitArea, node.X - (size + 16) / 2);
        Canvas.SetTop(hitArea, node.Y - (size + 16) / 2);
        GraphCanvas.Children.Add(hitArea);
    }

    private TextBlock AddLabel(string text, double x, double y, Brush foreground,
                               double fontSize, FontWeight weight, HorizontalAlignment align)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize,
            FontWeight = weight
        };

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = tb.DesiredSize.Width;

        double left = align switch
        {
            HorizontalAlignment.Right => x - textWidth,
            HorizontalAlignment.Center => x - textWidth / 2,
            _ => x
        };

        // Keep labels inside the canvas.
        double maxLeft = GraphCanvas.ActualWidth - textWidth - 2;
        if (maxLeft > 2)
            left = Math.Clamp(left, 2, maxLeft);

        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, y);
        GraphCanvas.Children.Add(tb);
        return tb;
    }

    private void AddEmptyState(double w, double h)
    {
        if (w < 10 || h < 10) return;

        var tb = new TextBlock
        {
            Text = _vm.IsGraphFiltered
                ? "No traffic matches the current filters"
                : "No traffic data — start monitoring to see the graph",
            Foreground = (SolidColorBrush)FindResource("SecondaryTextBrush"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, (w - tb.DesiredSize.Width) / 2);
        Canvas.SetTop(tb, (h - tb.DesiredSize.Height) / 2);
        GraphCanvas.Children.Add(tb);
    }
}
