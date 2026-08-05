using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    // Graph chrome metrics.
    private const double HeaderBandHeight = 20;
    private const double TopPad = 40;
    private const double BotPad = 24;
    private const double MinCanvasHeight = 340;
    private const double MaxCanvasHeight = 820;
    private const double RowStep = 26;

    /// <summary>Gap between a node's edge and its label.</summary>
    private const double LabelGap = 7;

    /// <summary>Largest radius any node is drawn at, used when reserving label
    /// gutters before node sizes are known.</summary>
    private const double NodeMaxRadius = 11;

    /// <summary>Opacity applied to everything unrelated to the hovered node.</summary>
    private const double DimOpacity = 0.10;

    private const double EdgeRestOpacity = 0.55;
    private const double EdgeFocusOpacity = 0.95;

    private readonly DashboardViewModel _vm;

    // Redraws are deferred while a tooltip or context menu is open: rebuilding
    // the canvas destroys the popup's owner element mid-interaction, which reads
    // as a "blinking" popup (or a menu that closes under the cursor). The
    // pending redraw runs as soon as the last popup closes.
    private int _openPopups;
    private bool _redrawPending;

    // Hover focus-mode bookkeeping: which visuals belong to which node, the
    // drawn edge shapes, and every element's resting opacity so focus mode can
    // dim and restore without recomputing it.
    private readonly Dictionary<string, List<UIElement>> _nodeVisuals = new(StringComparer.Ordinal);
    private readonly List<EdgeVisual> _edgeVisuals = new();
    private readonly Dictionary<UIElement, double> _baseOpacity = new();

    private sealed record EdgeVisual(GraphEdge Edge, Path Shape, double Thickness);

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
        tooltip.Opened += (_, _) => _openPopups++;
        tooltip.Closed += (_, _) => ReleasePopup();
        return tooltip;
    }

    /// <summary>Same deferral as <see cref="TrackTooltip"/>, for context menus:
    /// a rebuild while a menu is open would drop the menu's placement target.</summary>
    private ContextMenu TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => _openPopups++;
        menu.Closed += (_, _) => ReleasePopup();
        return menu;
    }

    private void ReleasePopup()
    {
        _openPopups = Math.Max(0, _openPopups - 1);
        if (_openPopups == 0 && _redrawPending)
        {
            _redrawPending = false;
            RedrawGraph();
        }
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
        if (_openPopups > 0)
        {
            _redrawPending = true;
            return;
        }

        var data = _vm.GraphData;

        // Grow the canvas to fit the busiest column before drawing. Changing
        // Height raises SizeChanged, which redraws at the new size — so bail out
        // here and let that pass do the work.
        if (data != null)
        {
            double desired = DesiredHeight(data);
            if (Math.Abs(GraphCanvas.Height - desired) > 0.5)
            {
                GraphCanvas.Height = desired;
                return;
            }
        }

        GraphCanvas.Children.Clear();
        _nodeVisuals.Clear();
        _edgeVisuals.Clear();
        _baseOpacity.Clear();

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
        var warningBrush = (SolidColorBrush)FindResource("WarningBrush");
        var primaryText = (SolidColorBrush)FindResource("PrimaryTextBrush");
        var secondaryText = (SolidColorBrush)FindResource("SecondaryTextBrush");
        var secondaryBg = (SolidColorBrush)FindResource("SecondaryBgBrush");
        var tertiaryBg = (SolidColorBrush)FindResource("TertiaryBgBrush");
        var borderBrush = (SolidColorBrush)FindResource("BorderBrush");
        var wslBrush = (SolidColorBrush)FindResource("WslBrush");
        var hypervBrush = (SolidColorBrush)FindResource("HyperVBrush");

        // Three-column layout. The outer columns are inset by however much room
        // their labels actually need, because those labels hang outwards (away
        // from the edges) and would otherwise be clipped at the canvas edge or
        // clamped back on top of their own nodes.
        double procGutter = GutterFor(processNodes, ProcessLabelText, w);
        double remoteGutter = GutterFor(remoteNodes, RemoteLabelText, w);

        double procX = procGutter;
        double remoteX = w - remoteGutter;
        double adapterX = (procX + remoteX) / 2;
        double usableH = h - TopPad - BotPad;

        PositionColumn(processNodes, procX, TopPad, usableH);
        PositionColumn(adapterNodes, adapterX, TopPad, usableH);
        PositionColumn(remoteNodes, remoteX, TopPad, usableH);

        var nodeLookup = data.Nodes.ToDictionary(n => n.Id, n => n);

        // ---- Background chrome: header band + column guides, drawn first so
        // every node and edge sits on top of it.
        DrawChrome(w, h, tertiaryBg, borderBrush, new[] { procX, adapterX, remoteX });

        AddLabel("PROCESSES", procX, 3, secondaryText, 10, FontWeights.SemiBold, HorizontalAlignment.Center);
        AddLabel("ADAPTERS", adapterX, 3, secondaryText, 10, FontWeights.SemiBold, HorizontalAlignment.Center);
        AddLabel("REMOTE ENDPOINTS", remoteX, 3, secondaryText, 10, FontWeights.SemiBold, HorizontalAlignment.Center);

        // ---- Edges (both layer pairs), as curves so crossings stay readable.
        foreach (var edge in data.Edges)
        {
            if (!nodeLookup.TryGetValue(edge.SourceId, out var src)) continue;
            if (!nodeLookup.TryGetValue(edge.TargetId, out var tgt)) continue;

            double thickness = Math.Max(1.4, (double)edge.TotalCount / data.MaxEdgeCount * 6.0);
            bool fullyBlocked = edge.AllowedCount == 0 && edge.BlockedCount > 0;

            var geometry = BuildEdgeGeometry(src.X + 9, src.Y, tgt.X - 9, tgt.Y);
            var edgeBrush = BlendEdgeBrush(edge, successBrush.Color, warningBrush.Color, dangerBrush.Color);

            var curve = new Path
            {
                Data = geometry,
                Stroke = edgeBrush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = EdgeRestOpacity
            };
            if (fullyBlocked)
                curve.StrokeDashArray = new DoubleCollection { 4, 3 };
            AddVisual(curve, EdgeRestOpacity);

            var tooltip = TrackTooltip(
                BuildEdgeTooltip(edge, src.Label, tgt.Label, primaryText, secondaryText, secondaryBg, tertiaryBg));

            // Invisible wider hit-test curve for easy hovering / right-clicking.
            var hitCurve = new Path
            {
                Data = geometry,
                Stroke = Brushes.Transparent,
                StrokeThickness = Math.Max(14, thickness + 8),
                ToolTip = tooltip,
                ContextMenu = TrackMenu(BuildEdgeMenu(edge, src, tgt))
            };
            hitCurve.MouseEnter += (_, _) =>
            {
                curve.Opacity = EdgeFocusOpacity;
                curve.StrokeThickness = thickness + 2;
            };
            hitCurve.MouseLeave += (_, _) =>
            {
                curve.Opacity = EdgeRestOpacity;
                curve.StrokeThickness = thickness;
            };
            AddVisual(hitCurve, 1.0);

            _edgeVisuals.Add(new EdgeVisual(edge, curve, thickness));
        }

        // ---- Process nodes: tertiary fill with accent border, sized by volume.
        int maxProcCount = processNodes.Count > 0 ? processNodes.Max(n => n.ConnectionCount) : 1;
        foreach (var node in processNodes)
        {
            bool isBucket = node.Label == TrafficGraphBuilder.SystemProcessLabel
                            || node.Label == TrafficGraphBuilder.OthersProcessLabel;
            var edges = data.Edges.Where(e => e.SourceId == node.Id).ToList();
            double size = NodeDiameter(node.ConnectionCount, maxProcCount, 9, 18);

            DrawNode(node, size, tertiaryBg, primaryText, secondaryText, secondaryBg, tertiaryBg,
                edges, stroke: accentBrush);

            // Label hangs to the LEFT, away from the edges leaving this node.
            var tb = AddRichLabel(node.X - size / 2 - LabelGap, node.Y - 8, HorizontalAlignment.Right,
                node.Id,
                (node.Label, isBucket ? secondaryText : primaryText, FontWeights.Normal, 11),
                ($"  {node.ConnectionCount:N0}", accentBrush, FontWeights.SemiBold, 11));
            if (isBucket)
                tb.FontStyle = FontStyles.Italic;
        }

        // ---- Adapter nodes: color by adapter type, label below the node.
        int maxNicCount = adapterNodes.Count > 0 ? adapterNodes.Max(n => n.ConnectionCount) : 1;
        foreach (var node in adapterNodes)
        {
            var fill = node.AdapterType switch
            {
                AdapterType.WSL => wslBrush,
                AdapterType.HyperV or AdapterType.VSwitch => hypervBrush,
                _ => accentBrush
            };

            var edges = data.Edges.Where(e => e.SourceId == node.Id).ToList();
            double size = NodeDiameter(node.ConnectionCount, maxNicCount, 12, 22);

            DrawNode(node, size, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, edges);

            AddRichLabel(node.X, node.Y + size / 2 + 4, HorizontalAlignment.Center,
                node.Id,
                (node.Label, primaryText, FontWeights.Normal, 11),
                ($"  {node.ConnectionCount:N0}", accentBrush, FontWeights.SemiBold, 11));
        }

        // ---- Remote layer: group nodes, "+N more" nodes and individual remotes.
        int maxRemoteCount = remoteNodes.Count > 0 ? remoteNodes.Max(n => n.ConnectionCount) : 1;
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

                double size = isMore ? 12 : isHeader ? 13 : 20;
                DrawNode(node, size, fill, primaryText, secondaryText,
                    secondaryBg, tertiaryBg, nodeEdges,
                    hint: isMore || isHeader ? "Click to collapse" : "Click to expand");

                // Label hangs to the RIGHT, away from the edges arriving here.
                bool dimLabel = isMore || isHeader;
                var tb = AddRichLabel(node.X + size / 2 + LabelGap, node.Y - 8, HorizontalAlignment.Left,
                    node.Id,
                    (node.Label, dimLabel ? secondaryText : primaryText, FontWeights.Normal,
                        dimLabel ? 10.5 : 11.5));
                if (isMore)
                    tb.FontStyle = FontStyles.Italic;
            }
            else
            {
                bool mostlyBlocked = nodeEdges.Count > 0
                    && nodeEdges.Sum(e => e.BlockedCount) > nodeEdges.Sum(e => e.AllowedCount);

                var fill = node.IsWslGuest ? wslBrush : mostlyBlocked ? dangerBrush : successBrush;
                double size = NodeDiameter(node.ConnectionCount, maxRemoteCount, 8, 16);
                DrawNode(node, size, fill, primaryText, secondaryText, secondaryBg, tertiaryBg, nodeEdges);

                var name = string.IsNullOrEmpty(node.Hostname) ? node.Label : node.Hostname!;

                // Label hangs to the RIGHT, away from the edges arriving here.
                // Name first, count last — matching the process column now that
                // both outer columns read outwards from their nodes.
                AddRichLabel(node.X + size / 2 + LabelGap, node.Y - 8, HorizontalAlignment.Left,
                    node.Id,
                    (name, primaryText, FontWeights.Normal, 10.5),
                    (CountryTag(node), secondaryText, FontWeights.Normal, 10.5),
                    ($"  {node.ConnectionCount:N0}", accentBrush, FontWeights.SemiBold, 10.5));
            }
        }
    }

    /// <summary>Label text for a process node. Kept next to the drawing code so
    /// the width measured for the gutter matches what actually gets drawn.</summary>
    private static string ProcessLabelText(GraphNode node)
        => $"{node.Label}  {node.ConnectionCount:N0}";

    /// <summary>Label text for a remote-layer node (group header, "+N more"
    /// bucket, or an individual endpoint).</summary>
    private static string RemoteLabelText(GraphNode node)
    {
        if (node.Kind == GraphNodeKind.RemoteGroup)
            return node.Label;

        var name = string.IsNullOrEmpty(node.Hostname) ? node.Label : node.Hostname!;
        return $"{name}{CountryTag(node)}  {node.ConnectionCount:N0}";
    }

    private static string CountryTag(GraphNode node)
        => !string.IsNullOrEmpty(node.Country) && node.Country != "Unknown"
            ? $"  [{node.Country}]" : "";

    /// <summary>
    /// Horizontal space to reserve outside an outer column for its labels.
    /// Measured from the widest label rather than taken as a fraction of the
    /// canvas, since label width does not scale with canvas width — but capped
    /// at 30% so one very long name cannot crush the graph area.
    /// </summary>
    private static double GutterFor(List<GraphNode> nodes, Func<GraphNode, string> labelText, double w)
    {
        double widest = 0;
        foreach (var node in nodes)
            widest = Math.Max(widest, MeasureTextWidth(labelText(node)));

        return Math.Clamp(widest + LabelGap + NodeMaxRadius + 4, 44, w * 0.30);
    }

    /// <summary>Measures a label at the largest size/weight any run uses, so the
    /// result is a safe over-estimate of the drawn width.</summary>
    private static double MeasureTextWidth(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    /// <summary>Canvas height that gives the busiest column room to breathe,
    /// clamped so a wide fan-out cannot grow the panel without bound.</summary>
    private static double DesiredHeight(TrafficGraphData data)
    {
        int rows = 0;
        foreach (var kind in new[] { GraphNodeKind.Process, GraphNodeKind.Adapter })
            rows = Math.Max(rows, data.Nodes.Count(n => n.Kind == kind));
        rows = Math.Max(rows, data.Nodes.Count(n => n.Kind is GraphNodeKind.Remote or GraphNodeKind.RemoteGroup));

        double needed = TopPad + BotPad + rows * RowStep;
        return Math.Clamp(needed, MinCanvasHeight, MaxCanvasHeight);
    }

    /// <summary>Header band behind the column titles plus a faint vertical
    /// guide down each column, so the three layers read as columns even where
    /// no nodes are drawn.</summary>
    private void DrawChrome(double w, double h, Brush bandBrush, Brush guideBrush, double[] columnX)
    {
        var band = new Rectangle
        {
            Width = w,
            Height = HeaderBandHeight,
            Fill = bandBrush,
            RadiusX = 3,
            RadiusY = 3,
            Opacity = 0.30,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(band, 0);
        Canvas.SetTop(band, 0);
        AddVisual(band, 0.30);

        foreach (var x in columnX)
        {
            var guide = new Line
            {
                X1 = x, Y1 = HeaderBandHeight + 6,
                X2 = x, Y2 = h - 4,
                Stroke = guideBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 5 },
                Opacity = 0.20,
                IsHitTestVisible = false
            };
            AddVisual(guide, 0.20);
        }
    }

    /// <summary>Cubic curve between two columns, flattened horizontally so the
    /// line leaves and enters each node roughly level with it.</summary>
    private static PathGeometry BuildEdgeGeometry(double x1, double y1, double x2, double y2)
    {
        double dx = (x2 - x1) * 0.45;
        var figure = new PathFigure { StartPoint = new Point(x1, y1) };
        figure.Segments.Add(new BezierSegment(
            new Point(x1 + dx, y1),
            new Point(x2 - dx, y2),
            new Point(x2, y2),
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Edge colour on a green → amber → red ramp driven by the blocked share,
    /// so a mostly-allowed path stays green and a mixed one reads as amber.
    /// A straight green→red interpolation would pass through mud instead.
    /// </summary>
    private static SolidColorBrush BlendEdgeBrush(GraphEdge edge, Color success, Color warning, Color danger)
    {
        double blocked = edge.TotalCount == 0 ? 0 : (double)edge.BlockedCount / edge.TotalCount;

        Color color = blocked <= 0 ? success
            : blocked >= 1 ? danger
            : blocked < 0.5 ? Lerp(success, warning, blocked / 0.5)
            : Lerp(warning, danger, (blocked - 0.5) / 0.5);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Node diameter scaled by connection volume. Square-root scaling
    /// makes the node's AREA proportional to the count, which is how size is
    /// read visually.</summary>
    private static double NodeDiameter(int count, int maxCount, double min, double max)
    {
        double share = Math.Clamp(count / (double)Math.Max(1, maxCount), 0, 1);
        return min + (max - min) * Math.Sqrt(share);
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

    // ---- Focus mode ------------------------------------------------------

    /// <summary>Registers a canvas child and remembers its resting opacity so
    /// focus mode can dim and restore it.</summary>
    private void AddVisual(UIElement element, double baseOpacity, string? nodeId = null)
    {
        element.Opacity = baseOpacity;
        _baseOpacity[element] = baseOpacity;
        GraphCanvas.Children.Add(element);

        if (nodeId != null)
        {
            if (!_nodeVisuals.TryGetValue(nodeId, out var list))
                _nodeVisuals[nodeId] = list = new List<UIElement>();
            list.Add(element);
        }
    }

    /// <summary>
    /// Dims everything not on a path through <paramref name="nodeId"/>, so a
    /// single node's traffic stands out of a dense graph. Passing null restores
    /// every element to its resting opacity.
    /// </summary>
    private void ApplyFocus(string? nodeId)
    {
        if (nodeId == null)
        {
            foreach (var (element, opacity) in _baseOpacity)
                element.Opacity = opacity;
            foreach (var ev in _edgeVisuals)
                ev.Shape.StrokeThickness = ev.Thickness;
            return;
        }

        // Nodes one hop away in either direction stay lit along with the hovered one.
        var lit = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        foreach (var ev in _edgeVisuals)
        {
            if (ev.Edge.SourceId == nodeId) lit.Add(ev.Edge.TargetId);
            else if (ev.Edge.TargetId == nodeId) lit.Add(ev.Edge.SourceId);
        }

        foreach (var ev in _edgeVisuals)
        {
            bool onPath = ev.Edge.SourceId == nodeId || ev.Edge.TargetId == nodeId;
            ev.Shape.Opacity = onPath ? EdgeFocusOpacity : DimOpacity;
            ev.Shape.StrokeThickness = onPath ? ev.Thickness + 1.5 : ev.Thickness;
        }

        foreach (var (id, visuals) in _nodeVisuals)
        {
            bool onPath = lit.Contains(id);
            foreach (var element in visuals)
                element.Opacity = onPath ? _baseOpacity[element] : DimOpacity;
        }
    }

    // ---- Tooltips --------------------------------------------------------

    private static ToolTip BuildEdgeTooltip(GraphEdge edge, string sourceLabel, string targetLabel,
        Brush primaryText, Brush secondaryText, Brush bgBrush, Brush headerBg)
    {
        var panel = new StackPanel { MinWidth = 200 };
        panel.Children.Add(MakeTooltipHeader($"{sourceLabel}  →  {targetLabel}", primaryText, headerBg));

        // Stats
        var stats = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        stats.Children.Add(MakeStatRow("Total", edge.TotalCount.ToString("N0"), primaryText, secondaryText));
        stats.Children.Add(MakeStatRow("Allowed", edge.AllowedCount.ToString("N0"), AllowBrush, secondaryText));
        stats.Children.Add(MakeStatRow("Blocked", edge.BlockedCount.ToString("N0"), BlockBrush, secondaryText));

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
                row.Inlines.Add(new Run($"{p.Port}/{p.Protocol}") { Foreground = primaryText, FontWeight = FontWeights.SemiBold });
                int allowed = p.Count - p.BlockedCount;
                if (allowed > 0)
                    row.Inlines.Add(new Run($"  ✓{allowed:N0}") { Foreground = AllowBrush });
                if (p.BlockedCount > 0)
                    row.Inlines.Add(new Run($"  ⛔{p.BlockedCount:N0}") { Foreground = BlockBrush });
                if (p.DropReasons.Count > 0)
                    row.Inlines.Add(new Run($"  — {string.Join(", ", p.DropReasons)}") { Foreground = secondaryText });
                portsList.Children.Add(row);
            }
            panel.Children.Add(portsList);
        }

        // Drop reasons not already attributed to a specific port above
        // (e.g. network-layer drops that carry no port information).
        var unattributed = UnattributedDropReasons(edge);
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

        panel.Children.Add(MakeHintFooter("Right-click for filter actions", secondaryText, headerBg));

        return WrapTooltip(panel, bgBrush, headerBg);
    }

    /// <summary>Drop reasons on the edge that no individual top port accounts
    /// for (e.g. network-layer drops carrying no port).</summary>
    private static List<string> UnattributedDropReasons(GraphEdge edge)
    {
        var portReasons = edge.TopPorts.SelectMany(p => p.DropReasons).ToHashSet(StringComparer.Ordinal);
        return edge.DropReasons.Where(r => !portReasons.Contains(r)).ToList();
    }

    private static readonly SolidColorBrush AllowBrush = Freeze(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush BlockBrush = Freeze(Color.FromRgb(0xF4, 0x43, 0x36));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

    /// <summary>Italic hint line closing a tooltip.</summary>
    private static StackPanel MakeHintFooter(string text, Brush secondaryText, Brush headerBg)
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeSeparator(headerBg));
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = secondaryText,
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(10, 4, 10, 8)
        });
        return panel;
    }

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
        panel.Children.Add(MakeTooltipHeader($"{NodeIcon(node)}  {node.Label}", primaryText, headerBg));

        // Info
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };

        info.Children.Add(MakeStatRow("Connections", node.ConnectionCount.ToString("N0"), primaryText, secondaryText));

        int blocked = edges.Sum(e => e.BlockedCount);
        int allowed = edges.Sum(e => e.AllowedCount);
        if (blocked + allowed > 0)
        {
            info.Children.Add(MakeStatRow("Allowed", allowed.ToString("N0"), AllowBrush, secondaryText));
            info.Children.Add(MakeStatRow("Blocked", blocked.ToString("N0"), BlockBrush, secondaryText));
        }

        if (!string.IsNullOrEmpty(node.Hostname))
            info.Children.Add(MakeStatRow("Hostname", node.Hostname!, secondaryText, secondaryText));
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
                row.Inlines.Add(new Run(StripIdPrefix(otherId)) { Foreground = primaryText });
                row.Inlines.Add(new Run($"  ({e.TotalCount:N0})") { Foreground = secondaryText });
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

        var footer = hint == null
            ? "Right-click for details and filters"
            : $"{hint}  ·  right-click for more";
        panel.Children.Add(MakeHintFooter(footer, secondaryText, headerBg));

        return WrapTooltip(panel, bgBrush, headerBg);
    }

    private static string NodeIcon(GraphNode node) => node.Kind switch
    {
        GraphNodeKind.Process => "⚙",   // gear
        GraphNodeKind.Adapter => "🖥",  // desktop computer
        _ => "🌐"                       // globe
    };

    private static string StripIdPrefix(string id)
    {
        int idx = id.IndexOf(':');
        return idx > 0 ? id[(idx + 1)..] : id;
    }

    private static Grid MakeStatRow(string label, string value, Brush valueBrush, Brush labelBrush)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1), MinWidth = 190 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock { Text = label, Foreground = labelBrush, FontSize = 11 };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        return grid;
    }

    // ---- Context menus ---------------------------------------------------

    private static MenuItem MenuHeader(string text) => new()
    {
        Header = text,
        FontWeight = FontWeights.SemiBold,
        IsEnabled = false
    };

    /// <summary>Read-only label/value row inside a context menu.</summary>
    private MenuItem MenuInfo(string label, string value)
    {
        var secondaryText = (SolidColorBrush)FindResource("SecondaryTextBrush");
        var primaryText = (SolidColorBrush)FindResource("PrimaryTextBrush");
        return new MenuItem
        {
            Header = MakeStatRow(label, value, primaryText, secondaryText),
            IsEnabled = false
        };
    }

    private static MenuItem MenuAction(string text, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        if (enabled)
            item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Right-click menu for a graph node: a read-only stat block, clipboard
    /// actions, on-demand reverse DNS, and the same include/exclude filter
    /// vocabulary the Traffic Monitor grid uses.
    /// </summary>
    private ContextMenu BuildNodeMenu(GraphNode node, List<GraphEdge> edges)
    {
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(MenuHeader($"{NodeIcon(node)}  {node.Label}"));
        items.Add(new Separator());

        // ---- Read-only detail block
        items.Add(MenuInfo("Connections", node.ConnectionCount.ToString("N0")));

        int allowed = edges.Sum(e => e.AllowedCount);
        int blocked = edges.Sum(e => e.BlockedCount);
        if (allowed + blocked > 0)
        {
            items.Add(MenuInfo("Allowed", $"{allowed:N0}  ({(double)allowed / (allowed + blocked) * 100:F1}%)"));
            items.Add(MenuInfo("Blocked", $"{blocked:N0}  ({(double)blocked / (allowed + blocked) * 100:F1}%)"));
        }
        items.Add(MenuInfo(node.IsLocal ? "Peers" : "Adapters", edges.Count.ToString("N0")));

        if (node.AdapterType != null)
            items.Add(MenuInfo("Adapter type", node.AdapterType.ToString()!));
        if (!string.IsNullOrEmpty(node.Country) && node.Country != "Unknown")
            items.Add(MenuInfo("Country", node.Country!));
        if (!string.IsNullOrEmpty(node.Hostname))
            items.Add(MenuInfo("Hostname", node.Hostname!));

        var topPorts = edges
            .SelectMany(e => e.TopPorts)
            .GroupBy(p => (p.Port, p.Protocol))
            .OrderByDescending(g => g.Sum(p => p.Count))
            .Take(3)
            .ToList();
        if (topPorts.Count > 0)
        {
            items.Add(MenuInfo("Top ports",
                string.Join("  ", topPorts.Select(g => $"{g.Key.Port}/{g.Key.Protocol}"))));
        }

        // ---- Clipboard + lookups
        items.Add(new Separator());
        if (node.Kind == GraphNodeKind.Remote)
        {
            var ip = DashboardViewModel.RemoteIpOf(node);
            items.Add(MenuAction("Copy IP Address", () => DashboardViewModel.CopyToClipboard(ip)));
            items.Add(MenuAction("Copy Details", () => DashboardViewModel.CopyToClipboard(NodeDetailsText(node, edges))));
            items.Add(MenuAction(
                _vm.HostnameResolved(ip) ? "Hostname Resolved" : "Resolve Hostname (reverse DNS)",
                () => _ = _vm.ResolveHostnameAsync(ip),
                enabled: !_vm.HostnameResolved(ip)));
        }
        else
        {
            items.Add(MenuAction("Copy Name", () => DashboardViewModel.CopyToClipboard(node.Label)));
            items.Add(MenuAction("Copy Details", () => DashboardViewModel.CopyToClipboard(NodeDetailsText(node, edges))));
        }

        // ---- Drill / expand
        items.Add(new Separator());
        if (node.Kind == GraphNodeKind.RemoteGroup)
        {
            bool expanded = _vm.IsGroupExpanded(node);
            items.Add(MenuAction(expanded ? "Collapse Group" : "Expand Group", () => _vm.ToggleNode(node)));
        }

        bool drillable = DashboardViewModel.DrillValueOf(node) != null;
        if (_vm.IsDrilledTo(node))
        {
            items.Add(MenuAction("Clear Drill", () => _vm.ClearDrillCommand.Execute(null)));
        }
        else
        {
            items.Add(MenuAction("Drill Into This Node", () => _vm.DrillTo(node), enabled: drillable));
        }

        // ---- Include / exclude filters
        AddFilterItems(items, node);

        items.Add(new Separator());
        // Always enabled: menus are built during a redraw, so a state computed
        // here could be stale by the time the user actually right-clicks.
        items.Add(MenuAction("Clear All Filters", () => _vm.ClearFiltersCommand.Execute(null)));

        return menu;
    }

    /// <summary>Adds the include/exclude filter block appropriate to the node's
    /// layer. Aggregate buckets ("(others)", "+N more") have no single value to
    /// filter on, so they get nothing.</summary>
    private void AddFilterItems(ItemCollection items, GraphNode node)
    {
        switch (node.Kind)
        {
            case GraphNodeKind.Process when node.Label != TrafficGraphBuilder.OthersProcessLabel:
                items.Add(new Separator());
                items.Add(MenuHeader("Include"));
                items.Add(MenuAction("  Filter by Process", () => _vm.FilterByProcessName(node.Label)));
                items.Add(MenuHeader("Exclude"));
                items.Add(MenuAction("  Exclude Process", () => _vm.ExcludeProcessName(node.Label)));
                break;

            case GraphNodeKind.Adapter:
                items.Add(new Separator());
                items.Add(MenuHeader("Include"));
                items.Add(MenuAction("  Filter by NIC", () => _vm.FilterByNicName(node.Label)));
                items.Add(MenuHeader("Exclude"));
                items.Add(MenuAction("  Exclude NIC", () => _vm.ExcludeNicName(node.Label)));
                break;

            case GraphNodeKind.Remote:
                var ip = DashboardViewModel.RemoteIpOf(node);
                items.Add(new Separator());
                items.Add(MenuHeader("Include"));
                items.Add(MenuAction("  Filter by Dest IP", () => _vm.FilterByDestIp(ip)));
                items.Add(MenuAction("  Filter by Source IP", () => _vm.FilterBySourceIp(ip)));
                items.Add(MenuHeader("Exclude"));
                items.Add(MenuAction("  Exclude Dest IP", () => _vm.ExcludeDestIp(ip)));
                items.Add(MenuAction("  Exclude Source IP", () => _vm.ExcludeSourceIp(ip)));
                break;
        }
    }

    /// <summary>Right-click menu for an edge: its stats, per-port filter
    /// shortcuts, and a one-click filter narrowing to just this path.</summary>
    private ContextMenu BuildEdgeMenu(GraphEdge edge, GraphNode src, GraphNode tgt)
    {
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(MenuHeader($"{src.Label}  →  {tgt.Label}"));
        items.Add(new Separator());

        items.Add(MenuInfo("Total", edge.TotalCount.ToString("N0")));
        items.Add(MenuInfo("Allowed", edge.AllowedCount.ToString("N0")));
        items.Add(MenuInfo("Blocked", edge.BlockedCount.ToString("N0")));
        if (edge.TotalCount > 0)
            items.Add(MenuInfo("Allow rate", $"{(double)edge.AllowedCount / edge.TotalCount * 100:F1}%"));

        var unattributed = UnattributedDropReasons(edge);
        if (unattributed.Count > 0)
            items.Add(MenuInfo("Drop reasons", string.Join(", ", unattributed)));

        items.Add(new Separator());
        items.Add(MenuAction("Copy Details",
            () => DashboardViewModel.CopyToClipboard(EdgeDetailsText(edge, src, tgt))));

        // Per-port filter shortcuts.
        if (edge.TopPorts.Count > 0)
        {
            items.Add(new Separator());
            items.Add(MenuHeader("Filter by Port"));
            foreach (var p in edge.TopPorts)
            {
                int port = p.Port;
                string proto = p.Protocol;
                int blockedOnPort = p.BlockedCount;
                var suffix = blockedOnPort > 0 ? $"  ({p.Count:N0}, {blockedOnPort:N0} blocked)" : $"  ({p.Count:N0})";
                items.Add(MenuAction($"  {port}/{proto}{suffix}", () => _vm.FilterByPort(port, proto)));
            }
            items.Add(MenuHeader("Exclude Port"));
            foreach (var p in edge.TopPorts)
            {
                int port = p.Port;
                items.Add(MenuAction($"  Exclude {port}", () => _vm.ExcludePort(port)));
            }
        }

        // Narrow to this exact path where both ends map to filter fields.
        // A grouped/aggregated remote ("Internet (450)", "+N more") has no
        // single address to filter on, so the item is disabled there.
        items.Add(new Separator());
        bool canIsolate = CanIsolatePath(src, tgt);
        items.Add(MenuAction("Filter to This Path", () => IsolatePath(src, tgt), enabled: canIsolate));
        // Always enabled: menus are built during a redraw, so a state computed
        // here could be stale by the time the user actually right-clicks.
        items.Add(MenuAction("Clear All Filters", () => _vm.ClearFiltersCommand.Execute(null)));

        return menu;
    }

    private static bool CanIsolatePath(GraphNode src, GraphNode tgt)
        => (src.Kind == GraphNodeKind.Process && src.Label != TrafficGraphBuilder.OthersProcessLabel
                && tgt.Kind == GraphNodeKind.Adapter)
            || (src.Kind == GraphNodeKind.Adapter && tgt.Kind == GraphNodeKind.Remote);

    private void IsolatePath(GraphNode src, GraphNode tgt)
    {
        if (src.Kind == GraphNodeKind.Process && tgt.Kind == GraphNodeKind.Adapter)
        {
            _vm.FilterByProcessName(src.Label);
            _vm.FilterByNicName(tgt.Label);
        }
        else if (src.Kind == GraphNodeKind.Adapter && tgt.Kind == GraphNodeKind.Remote)
        {
            _vm.FilterByNicName(src.Label);
            _vm.FilterByDestIp(DashboardViewModel.RemoteIpOf(tgt));
        }
    }

    /// <summary>Plain-text dump of a node for "Copy Details".</summary>
    private static string NodeDetailsText(GraphNode node, List<GraphEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{node.Kind}: {node.Label}");
        sb.AppendLine($"Connections : {node.ConnectionCount:N0}");

        int allowed = edges.Sum(e => e.AllowedCount);
        int blocked = edges.Sum(e => e.BlockedCount);
        if (allowed + blocked > 0)
        {
            sb.AppendLine($"Allowed     : {allowed:N0}");
            sb.AppendLine($"Blocked     : {blocked:N0}");
        }
        if (!string.IsNullOrEmpty(node.Hostname))
            sb.AppendLine($"Hostname    : {node.Hostname}");
        if (node.AdapterType != null)
            sb.AppendLine($"Adapter type: {node.AdapterType}");
        if (!string.IsNullOrEmpty(node.Country) && node.Country != "Unknown")
            sb.AppendLine($"Country     : {node.Country}");

        if (edges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(node.IsLocal ? "Talking to:" : "Via adapters:");
            foreach (var e in edges.OrderByDescending(e => e.TotalCount))
            {
                var other = StripIdPrefix(node.IsLocal ? e.TargetId : e.SourceId);
                sb.AppendLine($"  {e.TotalCount,8:N0}  {other}   (blocked {e.BlockedCount:N0})");
            }
        }

        return sb.ToString();
    }

    /// <summary>Plain-text dump of an edge for "Copy Details".</summary>
    private static string EdgeDetailsText(GraphEdge edge, GraphNode src, GraphNode tgt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{src.Label}  ->  {tgt.Label}");
        sb.AppendLine($"Total   : {edge.TotalCount:N0}");
        sb.AppendLine($"Allowed : {edge.AllowedCount:N0}");
        sb.AppendLine($"Blocked : {edge.BlockedCount:N0}");

        if (edge.TopPorts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top ports:");
            foreach (var p in edge.TopPorts)
            {
                sb.Append($"  {p.Port}/{p.Protocol}  total {p.Count:N0}, blocked {p.BlockedCount:N0}");
                if (p.DropReasons.Count > 0)
                    sb.Append($"  — {string.Join(", ", p.DropReasons)}");
                sb.AppendLine();
            }
        }

        var unattributed = UnattributedDropReasons(edge);
        if (unattributed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Other drop reasons: {string.Join(", ", unattributed)}");
        }

        return sb.ToString();
    }

    // ---- Node / label drawing --------------------------------------------

    private void DrawNode(GraphNode node, double size, Brush fill, Brush primaryText,
        Brush secondaryText, Brush bgBrush, Brush headerBg, List<GraphEdge> edges,
        Brush? stroke = null, string? hint = null)
    {
        var tooltip = TrackTooltip(
            BuildNodeTooltip(node, edges, primaryText, secondaryText, bgBrush, headerBg, hint));
        var menu = TrackMenu(BuildNodeMenu(node, edges));

        // Soft halo behind the node, so dense columns still read as distinct
        // points against the edges passing behind them.
        var halo = new Ellipse
        {
            Width = size + 12,
            Height = size + 12,
            Fill = fill,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(halo, node.X - (size + 12) / 2);
        Canvas.SetTop(halo, node.Y - (size + 12) / 2);
        AddVisual(halo, 0.16, node.Id);

        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            Stroke = stroke ?? fill,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            ContextMenu = menu
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
        AddVisual(ellipse, 0.92, node.Id);

        // Larger invisible hit area for the node too
        var hitArea = new Ellipse
        {
            Width = size + 18,
            Height = size + 18,
            Fill = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            ContextMenu = menu
        };
        hitArea.MouseEnter += (_, _) =>
        {
            ApplyFocus(node.Id);
            ellipse.StrokeThickness = 3;
        };
        hitArea.MouseLeave += (_, _) =>
        {
            ApplyFocus(null);
            ellipse.StrokeThickness = 1.5;
        };
        hitArea.MouseLeftButtonDown += (_, e) =>
        {
            CloseTooltip(hitArea);
            Focus();
            _vm.ToggleNode(node);
            e.Handled = true;
        };
        Canvas.SetLeft(hitArea, node.X - (size + 18) / 2);
        Canvas.SetTop(hitArea, node.Y - (size + 18) / 2);
        AddVisual(hitArea, 1.0, node.Id);
    }

    private TextBlock AddLabel(string text, double x, double y, Brush foreground,
                               double fontSize, FontWeight weight, HorizontalAlignment align)
        => AddRichLabel(x, y, align, null, (text, foreground, weight, fontSize));

    /// <summary>
    /// Places a canvas label built from one or more differently styled runs,
    /// clamped to stay inside the canvas. Passing a nodeId ties the label to
    /// that node for focus-mode dimming.
    /// </summary>
    private TextBlock AddRichLabel(double x, double y, HorizontalAlignment align, string? nodeId,
        params (string Text, Brush Brush, FontWeight Weight, double Size)[] runs)
    {
        var tb = new TextBlock();
        foreach (var (text, brush, weight, size) in runs)
        {
            if (string.IsNullOrEmpty(text)) continue;
            tb.Inlines.Add(new Run(text) { Foreground = brush, FontWeight = weight, FontSize = size });
        }

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
        AddVisual(tb, 1.0, nodeId);
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
