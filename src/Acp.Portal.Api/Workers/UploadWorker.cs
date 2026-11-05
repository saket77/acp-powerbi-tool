using Acp.Portal.Infrastructure.Staging;

namespace Acp.Portal.Api.Workers;

/// <summary>
/// Drains the import queue, one job at a time, for as long as the portal is running.
/// </summary>
/// <remarks>
/// <para>
/// In the same process as the API rather than in an Azure Function. That is a deliberate
/// deviation from the wireframe — see docs/implementation-plan.md §1.4 — and it costs one thing
/// worth naming: an import holds a thread in the web app, and a deployment mid-import loses the
/// lock rather than the job. The queue's visibility timeout hands such a job back, which is why
/// there is a queue at all rather than a direct call.
/// </para>
/// <para>
/// One at a time on purpose. Imports are minutes long and Power BI rate-limits; the throughput
/// the portal needs is a few Databooks a day, and a queue that drains slowly is much easier to
/// reason about than one that drains in parallel.
/// </para>
/// </remarks>
public sealed class UploadWorker(
    IServiceScopeFactory scopes,
    ILogger<UploadWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Import worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;

            try
            {
                worked = await DrainOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Reaching here means the queue itself is unreachable — storage is down, or the
                // credentials are wrong. Keep the loop alive rather than taking the web app with
                // it; the health page is where this becomes visible.
                logger.LogError(e, "The import worker could not reach the queue. Retrying shortly.");
            }

            if (!worked) await Task.Delay(Idle, stoppingToken).ConfigureAwait(false);
        }

        logger.LogInformation("Import worker stopped.");
    }

    private async Task<bool> DrainOneAsync(CancellationToken stoppingToken)
    {
        // A scope per message: the pipeline uses a DbContext, and one shared across hours of
        // queue polling would accumulate every entity it ever tracked.
        await using var scope = scopes.CreateAsyncScope();

        var queue = scope.ServiceProvider.GetRequiredService<ImportQueue>();
        var message = await queue.ReceiveAsync(stoppingToken);

        if (message is null) return false;

        var pipeline = scope.ServiceProvider.GetRequiredService<UploadPipeline>();
        var finished = await pipeline.ProcessAsync(message.JobId, stoppingToken);

        if (finished)
        {
            await queue.CompleteAsync(message, stoppingToken);
        }
        else
        {
            // Left in place deliberately. The visibility timeout expires and the queue offers
            // the job again, which is the retry.
            logger.LogWarning(
                "Leaving job {Job} on the queue after {Count} delivery(s).",
                message.JobId, message.DequeueCount);
        }

        return true;
    }
}
