// Port of core/tests/connect.rs.
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class ConnectTests
{
    [Fact(Skip = "network")]
    public async Task test_connection()
    {
        var session = new Session(SessionConfig.Default(), null);
        await using var _ = session;

        var result = await Record.ExceptionAsync(
            () => session.ConnectAsync(Credentials.WithPassword("test", "test"), false).WaitAsync(TimeSpan.FromSeconds(30)));

        // Authentication must not succeed with bad credentials, and there should be some error message.
        Assert.NotNull(result);
        Assert.IsNotType<TimeoutException>(result);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }
}
