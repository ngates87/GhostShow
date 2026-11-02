namespace GhostShow.Tests;

static class Eventually
{
    /// <summary>Polls until condition holds; fails the test if it doesn't within the timeout (default 5 s).</summary>
    public static async Task True(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {because}.");
            await Task.Delay(5);
        }
    }
}
