# Dynamic Dashboard (NSX-style) — Design

## Goal

Give the Dashboard the same filter system as the Traffic Monitor and make the
traffic graph interactive: three-layer topology (Process → Adapter → Remote),
remote grouping with expand/collapse, and click-drilldown. Existing dashboard
structure (stat cards + graph + top lists) is kept; no flow animation.

## Components

**1. Shared filtering — `TrafficEventFilter` (Core, testable).**
The Traffic Monitor's filter semantics (8 fields: Src/Dst IP, Src/Dst Port,
Protocol, Process, NIC, Action; comma-separated terms, `!` negation, exact
port matching) move out of `TrafficMonitorViewModel` into a pure Core class
with a `Matches(TrafficEvent)` predicate. Both ViewModels consume it; the
Traffic Monitor's behavior is unchanged. The Dashboard's filter row replaces
the single `GraphFilter` box, and the filters gate everything on the tab:
stat cards, graph and top lists (today only the graph is filtered).

**2. Three-layer graph — `TrafficGraphBuilder` (Core, testable).**
Graph aggregation moves out of `DashboardViewModel.BuildGraphData` (untested
private method) into a pure builder: input = filtered events + adapter list +
expand state + drill selection; output = nodes/edges. Graph models
(`GraphNode`/`GraphEdge`/`TrafficGraphData`) move from the UI project to
Core.Models so the builder is unit-testable.

- Layers: **Processes** (top ~8 by event count, remainder aggregated into
  "(others)", PID-less drop traffic under "(system)") → **Adapters** →
  **Remotes**.
- Edges aggregate per adjacent layer pair with allowed/blocked counts, top
  ports and drop reasons (as today).

**3. Remote grouping & expand.**
Remotes collapse into group nodes — **WSL guest**, **LAN** (private, non-WSL),
**Internet** — showing flow counts. Clicking a group node expands it into its
top ~10 remote IPs plus an "+N more" node; clicking the group header again
collapses. Expand state lives in the ViewModel and is an input to the builder.

**4. Click-drilldown.**
Clicking any node (process, adapter, remote IP or group) sets a single drill
selection: the whole dashboard (stats, graph, top lists) filters to events
touching that node. The active selection renders as a chip above the graph
(`⊘ chrome.exe ✕`); ✕ or Esc clears it. Drill is ANDed with the text filters.
Drill semantics per node kind: process → ProcessName match; adapter →
InterfaceName match; remote IP → source or destination equals IP; group →
membership of the remote endpoint in that group.

## Testing

Unit tests (no WPF): `TrafficEventFilter` term/negation/port semantics parity
with the Traffic Monitor's current behavior; `TrafficGraphBuilder` layer
aggregation, grouping, expand, drill and "(others)"/"(system)" buckets.
Rendering (canvas drawing, hit-testing) stays code-behind and is verified
manually.

## Out of scope

Flow animation, full dashboard redesign, time-range selection.
