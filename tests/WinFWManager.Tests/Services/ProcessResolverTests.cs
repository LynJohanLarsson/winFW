using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class ProcessResolverTests
{
    private readonly ProcessResolver _resolver = new(cacheTtlSeconds: 60);

    [Fact]
    public void Resolve_CurrentProcess_ReturnsProcessInfo()
    {
        var pid = Environment.ProcessId;
        var info = _resolver.Resolve(pid);

        info.ProcessId.Should().Be(pid);
        info.Name.Should().NotBeNullOrEmpty();
        info.IsExited.Should().BeFalse();
    }

    [Fact]
    public void Resolve_InvalidPid_ReturnsExitedProcess()
    {
        var info = _resolver.Resolve(999999);

        info.ProcessId.Should().Be(999999);
        info.IsExited.Should().BeTrue();
        info.DisplayName.Should().Contain("exited");
    }

    [Fact]
    public void Resolve_SamePidTwice_ReturnsCached()
    {
        var pid = Environment.ProcessId;
        var first = _resolver.Resolve(pid);
        var second = _resolver.Resolve(pid);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void ClearCache_RemovesCachedEntries()
    {
        var pid = Environment.ProcessId;
        var first = _resolver.Resolve(pid);
        _resolver.ClearCache();
        var second = _resolver.Resolve(pid);

        first.Should().NotBeSameAs(second);
    }
}
