using AiGateway.App;

namespace AiGateway.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void Second_instance_is_refused_until_the_first_one_goes_away()
    {
        var name = @"Local\AiGateway.Tests." + Guid.NewGuid().ToString("N");

        using (var first = SingleInstanceGuard.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceGuard.TryAcquire(name));
        }

        using var again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }
}
