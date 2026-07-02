using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Core.Models;
using WinFWManager.ViewModels;

namespace WinFWManager.Views;

public partial class DashboardView : UserControl
{
    private readonly DashboardViewModel _vm;

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

    private void RedrawGraph()
    {
        GraphCanvas.Children.Clear();

        var data = _vm.GraphData;
        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;

        if (w < 100 || h < 100 || data == null)
        {
            AddEmptyState(w, h);
            return;
        }

        var localNodes = data.Nodes.Where(n => n.IsLocal).ToList();
        var remoteNodes = data.Nodes.Where(n => !n.IsLocal).ToList();

        if (localNodes.Count == 0 && remoteNodes.Count == 0)
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

        // Layout positions
        double leftX = 120;
        double rightX = w - 120;
        double topPad = 20;
        double botPad = 20;
        double usableH = h - topPad - botPad;

        // Position local nodes
        if (localNodes.Count > 0)
        {
            double step = usableH / (localNodes.Count + 1);
            for (int i = 0; i < localNodes.Count; i++)
            {
                localNodes[i].X = leftX;
                localNodes[i].Y = topPad + step * (i + 1);
            }
        }

        // Position remote nodes
        if (remoteNodes.Count > 0)
        {
            double step = usableH / (remoteNodes.Count + 1);
            for (int i = 0; i < remoteNodes.Count; i++)
            {
                remoteNodes[i].X = rightX;
                remoteNodes[i].Y = topPad + step * (i + 1);
            }
        }

        // Build lookup for node positions
        var nodeLookup = data.Nodes.ToDictionary(n => n.Id, n => n);

        // Draw column labels
        AddLabel("LOCAL ADAPTERS", leftX, 4, primaryText, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
        AddLabel("REMOTE ENDPOINTS", rightX, 4, primaryText, 11, FontWeights.SemiBold, HorizontalAlignment.Center);

        // Draw edges
        foreach (var edge in data.Edges)
        {
            if (!nodeLookup.TryGetValue(edge.SourceId, out var src)) continue;
            if (!nodeLookup.TryGetValue(edge.TargetId, out var tgt)) continue;

            double thickness = Math.Max(1.5, (double)edge.TotalCount / data.MaxEdgeCount * 6.0);
            bool fullyBlocked = edge.AllowedCount == 0 && edge.BlockedCount > 0;
            var edgeBrush = fullyBlocked || edge.BlockedCount > edge.AllowedCount ? dangerBrush : successBrush;

            // Visible line
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

            // Invisible wider hit-test line for easy hovering
            var hitLine = new Line
            {
                X1 = src.X + 8,
                Y1 = src.Y,
                X2 = tgt.X - 8,
                Y2 = tgt.Y,
                Stroke = Brushes.Transparent,
                StrokeThickness = Math.Max(14, thickness + 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = BuildEdgeTooltip(edge, edgeBrush, primaryText, secondaryText, secondaryBg, tertiaryBg)
            };
            // Highlight the visible line on hover
            hitLine.MouseEnter += (_, _) => { line.Opacity = 0.9; line.StrokeThickness = thickness + 2; };
            hitLine.MouseLeave += (_, _) => { line.Opacity = 0.5; line.StrokeThickness = thickness; };
            GraphCanvas.Children.Add(hitLine);
        }

        // Draw local nodes
        foreach (var node in localNodes)
        {
            var fill = node.IsWslGuest ? wslBrush : node.AdapterType switch
            {
                AdapterType.WSL => wslBrush,
                AdapterType.HyperV or AdapterType.VSwitch => hypervBrush,
                _ => accentBrush
            };

            // Gather edge info for this NIC
            var nicEdges = data.Edges.Where(e => e.SourceId == node.Id).ToList();
            DrawNode(node, 14, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, nicEdges, data);

            // Label to the right of node
            var label = $"{node.Label}  ({node.ConnectionCount})";
            AddLabel(label, node.X + 18, node.Y - 8, primaryText, 11, FontWeights.Normal, HorizontalAlignment.Left);
        }

        // Draw remote nodes
        foreach (var node in remoteNodes)
        {
            bool mostlyBlocked = false;
            var nodeEdges = data.Edges.Where(e => e.TargetId == node.Id).ToList();
            if (nodeEdges.Count > 0)
                mostlyBlocked = nodeEdges.Sum(e => e.BlockedCount) > nodeEdges.Sum(e => e.AllowedCount);

            var fill = node.IsWslGuest ? wslBrush : mostlyBlocked ? dangerBrush : successBrush;
            DrawNode(node, 10, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, nodeEdges, data);

            // Label to the left of node
            var countryTag = !string.IsNullOrEmpty(node.Country) && node.Country != "Unknown"
                ? $"  [{node.Country}]" : "";
            var label = $"({node.ConnectionCount})  {node.Label}{countryTag}";
            AddLabel(label, node.X - 18, node.Y - 8, secondaryText, 10.5, FontWeights.Normal, HorizontalAlignment.Right);
        }
    }

    private static ToolTip BuildEdgeTooltip(GraphEdge edge, Brush edgeBrush,
        Brush primaryText, Brush secondaryText, Brush bgBrush, Brush headerBg)
    {
        var panel = new StackPanel { MinWidth = 200 };

        // Header
        var header = new Border
        {
            Background = headerBg,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Text = $"{edge.SourceId}  \u2192  {edge.TargetId}",
                Foreground = primaryText,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            }
        };
        panel.Children.Add(header);

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
            var sep = new Border
            {
                BorderBrush = headerBg,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(8, 2, 8, 2)
            };
            panel.Children.Add(sep);

            var portsLabel = new TextBlock
            {
                Text = "Top Ports:",
                Foreground = secondaryText,
                FontSize = 11,
                Margin = new Thickness(10, 4, 10, 2)
            };
            panel.Children.Add(portsLabel);

            var portsList = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };
            foreach (var p in edge.TopPorts)
            {
                var row = new TextBlock
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                row.Inlines.Add(new System.Windows.Documents.Run($"{p.Port}/{p.Protocol}") { Foreground = primaryText, FontWeight = FontWeights.SemiBold });
                row.Inlines.Add(new System.Windows.Documents.Run($"  ×{p.Count}") { Foreground = secondaryText });
                portsList.Children.Add(row);
            }
            panel.Children.Add(portsList);
        }

        // Drop reasons section
        if (edge.DropReasons.Count > 0)
        {
            var sep = new Border
            {
                BorderBrush = headerBg,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(8, 2, 8, 2)
            };
            panel.Children.Add(sep);

            var reasonsList = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
            foreach (var reason in edge.DropReasons)
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

        return new ToolTip
        {
            Content = panel,
            Background = bgBrush,
            BorderBrush = headerBg,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0)
        };
    }

    private static ToolTip BuildNodeTooltip(GraphNode node, List<GraphEdge> edges,
        Brush primaryText, Brush secondaryText, Brush bgBrush, Brush headerBg)
    {
        var panel = new StackPanel { MinWidth = 220 };

        // Header
        var headerText = node.IsLocal ? $"\ud83d\udda5  {node.Label}" : $"\ud83c\udf10  {node.Label}";
        var header = new Border
        {
            Background = headerBg,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new TextBlock
            {
                Text = headerText,
                Foreground = primaryText,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            }
        };
        panel.Children.Add(header);

        // Info
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };

        info.Children.Add(MakeStatRow("Connections", node.ConnectionCount.ToString(), primaryText, secondaryText));

        if (node.AdapterType != null)
            info.Children.Add(MakeStatRow("Type", node.AdapterType.ToString()!, secondaryText, secondaryText));
        if (!string.IsNullOrEmpty(node.Country) && node.Country != "Unknown")
            info.Children.Add(MakeStatRow("Country", node.Country, secondaryText, secondaryText));

        panel.Children.Add(info);

        // Connected endpoints
        if (edges.Count > 0)
        {
            var sep = new Border
            {
                BorderBrush = headerBg,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(8, 2, 8, 2)
            };
            panel.Children.Add(sep);

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
                var targetId = node.IsLocal ? e.TargetId : e.SourceId;
                var row = new TextBlock
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                row.Inlines.Add(new System.Windows.Documents.Run(targetId) { Foreground = primaryText });
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

        return new ToolTip
        {
            Content = panel,
            Background = bgBrush,
            BorderBrush = headerBg,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0)
        };
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
        Brush secondaryText, Brush bgBrush, Brush headerBg, List<GraphEdge> edges, TrafficGraphData data)
    {
        var tooltip = BuildNodeTooltip(node, edges, primaryText, secondaryText, bgBrush, headerBg);

        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            Stroke = fill,
            StrokeThickness = 1.5,
            Opacity = 0.9,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip
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
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip
        };
        hitArea.MouseEnter += (_, _) => { ellipse.Opacity = 1.0; ellipse.StrokeThickness = 3; };
        hitArea.MouseLeave += (_, _) => { ellipse.Opacity = 0.9; ellipse.StrokeThickness = 1.5; };
        Canvas.SetLeft(hitArea, node.X - (size + 16) / 2);
        Canvas.SetTop(hitArea, node.Y - (size + 16) / 2);
        GraphCanvas.Children.Add(hitArea);
    }

    private void AddLabel(string text, double x, double y, Brush foreground,
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

        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, y);
        GraphCanvas.Children.Add(tb);
    }

    private void AddEmptyState(double w, double h)
    {
        if (w < 10 || h < 10) return;

        var tb = new TextBlock
        {
            Text = "No traffic data \u2014 start monitoring to see the graph",
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
