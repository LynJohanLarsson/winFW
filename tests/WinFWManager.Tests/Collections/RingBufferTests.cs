using WinFWManager.Core.Collections;

namespace WinFWManager.Tests.Collections;

public class RingBufferTests
{
    [Fact]
    public void Add_UnderCapacity_ContainsAllItems()
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1); buffer.Add(2); buffer.Add(3);
        buffer.ToList().Should().Equal(1, 2, 3);
        buffer.Count.Should().Be(3);
    }

    [Fact]
    public void Add_OverCapacity_DropsOldest()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1); buffer.Add(2); buffer.Add(3); buffer.Add(4);
        buffer.ToList().Should().Equal(2, 3, 4);
        buffer.Count.Should().Be(3);
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var buffer = new RingBuffer<int>(5);
        buffer.Add(1); buffer.Add(2);
        buffer.Clear();
        buffer.Count.Should().Be(0);
        buffer.ToList().Should().BeEmpty();
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAdds_NoExceptions()
    {
        var buffer = new RingBuffer<int>(100);
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                    buffer.Add(i * 50 + j);
            }));

        await Task.WhenAll(tasks);
        buffer.Count.Should().Be(100);
    }
}
