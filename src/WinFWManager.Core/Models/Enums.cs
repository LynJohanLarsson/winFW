namespace WinFWManager.Core.Models;

public enum TrafficDirection
{
    Inbound,
    Outbound
}

public enum TrafficAction
{
    Allow,
    Block,
    Drop
}

public enum FirewallProfile
{
    Domain,
    Private,
    Public,
    Any
}

public enum FirewallStore
{
    ActiveStore,
    PersistentStore,
    ConfigurableServiceStore,
    GPO
}

public enum AdapterType
{
    Physical,
    Virtual,
    VSwitch,
    WSL,
    HyperV,
    Loopback,
    Unknown
}

public enum TransportProtocol
{
    TCP,
    UDP,
    ICMP,
    ICMPv6,
    Other
}

public enum WslNetworkingMode
{
    Nat,
    Mirrored,
    Bridged
}
