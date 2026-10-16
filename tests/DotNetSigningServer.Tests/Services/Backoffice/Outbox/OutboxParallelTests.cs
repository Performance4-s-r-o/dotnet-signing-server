using System.Diagnostics;
using System.Net;
using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Backoffice.Outbox;
using Xunit;

namespace DotNetSigningServer.Tests.Services.Backoffice.Outbox;

public class OutboxParallelTests
{
    [Fact]
    public async Task DispatchDue_RunsAttemptsSideBySide_SoOneSlowRequestDoesNotHoldUpTheBatch()
    {
        using var host = new OutboxTestHost();
        const int items = 6;
        for (var i = 0; i < items; i++)
        {
            await host.EnqueueAsync(new { n = i });
            host.Service.Responses.Enqueue(async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("""{"id":"res_1","status":"queued"}""", System.Text.Encoding.UTF8, "application/json"),
                };
            });
        }

        var watch = Stopwatch.StartNew();
        var claimed = await host.Processor.DispatchDueAsync(CancellationToken.None);
        watch.Stop();

        Assert.Equal(items, claimed);
        // Sequentially this takes 3 s.
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(2000), $"took {watch.Elapsed}");
        Assert.Equal(items, await host.WithDbAsync(db => Task.FromResult(db.BackofficeOutboxItems.Count(i => i.Status == BackofficeOutboxStatus.Sent))));
    }
}
