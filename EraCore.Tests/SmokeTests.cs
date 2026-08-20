using Xunit;

namespace MinorShift.Emuera.Tests;

public class SmokeTests
{
    [Fact]
    public void Trivial_green_test_proves_runner_discovers_tests()
    {
        Assert.Equal(2, 1 + 1);
    }
}
