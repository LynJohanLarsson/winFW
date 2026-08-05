namespace WinFWManager.Core.Models;

/// <summary>Which layer of the traffic graph a node belongs to.</summary>
public enum GraphNodeKind
{
    Process,
    Adapter,
    RemoteGroup,
    Remote
}

/// <summary>Classification bucket for remote endpoints.</summary>
public enum RemoteGroupKind
{
    WslGuest,
    Lan,
    Internet
}

public class GraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsLocal { get; set; }
    public int ConnectionCount { get; set; }
    public string? Country { get; set; }
    public AdapterType? AdapterType { get; set; }
    public bool IsWslGuest { get; set; }

    /// <summary>Reverse-DNS name, filled in on demand from the dashboard's
    /// hostname cache (the graph builder never resolves names itself).</summary>
    public string? Hostname { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Graph layer this node belongs to.</summary>
    public GraphNodeKind Kind { get; set; } = GraphNodeKind.Remote;

    /// <summary>Remote grouping bucket, set on remote-layer nodes.</summary>
    public RemoteGroupKind? Group { get; set; }

    /// <summary>True when this node belongs to an expanded remote group.</summary>
    public bool IsExpanded { get; set; }
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
    /// <summary>Total events on this port (allowed + blocked).</summary>
    public int Count { get; set; }
    /// <summary>How many of <see cref="Count"/> were blocked/dropped.</summary>
    public int BlockedCount { get; set; }
    /// <summary>Distinct drop reasons observed on this port, sorted.</summary>
    public List<string> DropReasons { get; set; } = new();
}

public class TrafficGraphData
{
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
    public int MaxEdgeCount { get; set; } = 1;
}
