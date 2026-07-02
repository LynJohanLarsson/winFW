using WinFWManager.Core.Models;

namespace WinFWManager.ViewModels;

public class GraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsLocal { get; set; }
    public int ConnectionCount { get; set; }
    public string? Country { get; set; }
    public AdapterType? AdapterType { get; set; }
    public bool IsWslGuest { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public class GraphEdge
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public int AllowedCount { get; set; }
    public int BlockedCount { get; set; }
    public int TotalCount => AllowedCount + BlockedCount;

    /// <summary>Top destination ports with their hit counts, sorted descending.</summary>
    public List<PortCount> TopPorts { get; set; } = new();

    /// <summary>Distinct WFP drop reasons observed on this edge.</summary>
    public List<string> DropReasons { get; set; } = new();
}

public class PortCount
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "";
    public int Count { get; set; }
}

public class TrafficGraphData
{
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
    public int MaxEdgeCount { get; set; } = 1;
}
