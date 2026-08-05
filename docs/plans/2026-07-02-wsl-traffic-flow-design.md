# WSL Traffic Flow & TCPIP Provider Migration — Design

## Goal

Make WSL→host traffic visible in WinFW Manager with accurate flow information (which adapter, allowed or dropped, why), across WSL's three networking modes (NAT prioritised), by migrating ETW capture from the kernel `NetworkTCPIP` provider to the `Microsoft-Windows-TCPIP` manifest provider.

## Empirical findings that drive this design

Verified by ETW spikes on a live machine (Windows 11, WSL2 NAT mode, adapter `vEthernet (WSL (Hyper-V firewall))` = ifIndex 33, subnet 172.26.112.0/20):

1. **`Microsoft-Windows-WFP` is a dead end** — ~1 event/20s via the Dynamic parser, no per-packet 5-tuple or LUID.
2. **`Microsoft-Windows-TCPIP` (manifest, GUID `2f07e2ee-15db-40f1-90ef-9d7ba282188a`) is rich**: connection lifecycle events with LocalAddress/RemoteAddress + PID; UDP endpoint messages with sockaddrs + PID; packet-drop events with IfIndex, addresses, reason and direction.
3. **WSL→host traffic IS visible** via this provider, including when the firewall drops it:
   - `TcpipNetworkPacketDrops if=33 Source=172.26.127.184 Dest=172.26.112.1 [Reason=256 PathDirection=1]`
   - `TcpipTransportPacketDrops LocalSockAddr=172.26.112.1:9099 RemoteSockAddr=172.26.127.184:44216 [Reason=4]`
4. **WSL→internet (NAT-forwarded) remains invisible** to all host-level ETW — 0 WSL-subnet events despite sustained guest traffic. Pre-NAT capture would need pktmon/NDIS; out of scope (documented limitation, unchanged).
5. The current kernel provider carries no interface identity, no drops (Action is hard-coded Allow), and misses the entire blocked-traffic story.

## Section 1 — Capture layer & event model

Rewrite `EtwTrafficMonitor` around the `Microsoft-Windows-TCPIP` manifest provider (replacing the kernel provider entirely — single clean source):

- **Connection lifecycle (TCP):** `TcpRequestConnect`, `TcpConnectTcbComplete`, `TcpConnectionRundown`, `TcpDisconnectTcbComplete` → local/remote sockaddr, PID, state. Modelled as connection-oriented events (one row per connection with state updates), not one row per packet.
- **UDP:** `UdpEndpointSendMessages` / `UdpEndpointReceiveMessages` → sockaddrs + PID.
- **Drops/blocks:** `TcpipNetworkPacketDrops` (IfIndex, addresses, Reason, PathDirection) + `TcpipTransportPacketDrops` (5-tuple, Reason). The two are correlated on address-pair within a short window (~2s, bounded dictionary) into a single drop event carrying both interface and ports. Uncorrelated halves are emitted anyway.
- **Supplementary:** `TcpRstSend`, `IcmpSendRecv`.
- Emits enriched `TrafficEvent`s (real `Action` = Allow/Drop/Block, `InterfaceName` via IfIndex, drop reason) on the existing `IObservable<TrafficEvent>` stream — ViewModel contract unchanged.
- ETW field interpretation is isolated in a parser layer (`TcpIpEventParser`) that takes field values, not `TraceEvent`, so it is unit-testable without a live session.

## Section 2 — Interface attribution & WSL modes

**IfIndex → adapter (authoritative):**
- `NetworkInterfaceService` gains an `ifIndex → NetworkAdapterInfo` map (InterfaceIndex is already captured per adapter) + `ResolveByIfIndex(int)`.
- Enrichment order: (1) event has IfIndex → exact adapter; (2) otherwise → existing `ResolveAdapter(local, remote)` subnet fallback. No new P/Invoke needed.

**WSL mode detection (new `WslNetworkModeDetector` in Core):**
- Reads `%USERPROFILE%\.wslconfig` (`networkingMode=` under `[wsl2]`), verified against the adapter landscape.
- **NAT** (default, priority 1): WSL vEthernet adapter with own subnet; current logic works, IfIndex makes it exact.
- **Mirrored**: no WSL adapter; guest shares host IP. WSL traffic cannot be distinguished by IP/interface → detected and surfaced as an info banner ("WSL runs mirrored — WSL traffic appears as host traffic"); `IsWslTraffic` tagging disabled rather than guessing wrong.
- **Bridged** (deprecated but occurs): detected via `vmSwitch=` in `.wslconfig`; guest IP fetched opportunistically via `wsl hostname -I` (cached) so traffic to/from that IP can be tagged WSL. Failure degrades silently to subnet logic.
- Detected mode exposed as a property and shown in the Network Interfaces tab.

## Section 3 — UI: enriched columns & flow visualisation

**Traffic Monitor:**
- **Action column becomes real**: Allow (green) / Drop/Block (red) with tooltip showing a readable drop reason (mapped from Reason codes; unknown codes shown as `Drop (reason N)`).
- **NIC column** shows the exact adapter (IfIndex) with a marker for attribution source: exact vs derived (subnet) — e.g. dimmed italic for derived.
- **New Flow column**: compact path notation, e.g. `WSL guest → vEthernet (WSL) ⛔` for a dropped WSL→host, `Ethernet → internet ✓` for allowed outbound. Built from direction + adapter + action.
- Existing filters unchanged; Action becomes filterable ("drop" / "!allow").

**Dashboard graph:**
- Extends the existing NIC↔remote edge graph into a three-layer topology: source (WSL guest / host process) → adapter node → remote.
- WSL guest becomes its own node (yellow) linked to the WSL adapter node; edges coloured green/red by allow/drop ratio; tooltips show drop reasons + top ports.
- Blocked flows drawn as dashed red edges — you see where in the path traffic stops.
- Mirrored mode: the WSL node is replaced by the banner (no false topology).

**Network Interfaces tab:** badge showing detected WSL mode (NAT/Mirrored/Bridged) + guest IP when known.

## Section 4 — Error handling, performance & testing

**Error handling:**
- Admin requirement unchanged (`RequiresAdmin` flow as today).
- Unknown Reason codes → `Drop (reason N)`, mapping table grows empirically.
- Drop correlation window ~2s with bounded dictionary; uncorrelated halves still emitted.
- `.wslconfig` missing/unreadable → assume NAT, verify against adapters. `wsl hostname -I` failure → Bridged tagging degrades to subnet logic.

**Performance:**
- Provider is chatty (~300–500 events/s observed). Hard filter in the callback on event name (O(1) hash lookup, no payload reads) — only lifecycle/drop/RST/UDP-message events are processed.
- Existing 100ms batching + 50k ring buffer kept. Connection aggregation deduplicates (one connection ≠ 50 packet rows) → lower UI pressure than today.
- Adapter/IfIndex map cached, refreshed on `NetworkChange.NetworkAddressChanged` (no polling).

**Testing:**
- Unit tests (no ETW): Reason-code mapping, sockaddr decoding (IPv4/IPv6/`::ffff:` dual-stack), drop correlation, IfIndex resolution, `.wslconfig` parsing for all three modes (fixture files), flow string builder.
- Manual verification matrix: host→WSL (Allow, yellow), WSL→host without rule (red Drop with reason), WSL→host with Hyper-V allow rule (green Allow), host→internet (Ethernet attribution via IfIndex).

## Out of scope

- WSL→internet (NAT-forwarded) capture — requires pktmon/NDIS on the WSL adapter; limitation stays documented in README.
- Per-process attribution inside the WSL guest (host sees only the guest IP).
