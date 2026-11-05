using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Staging;

/// <summary>
/// Carries import jobs from the request that created them to the worker that runs them.
/// </summary>
/// <remarks>
/// A queue rather than a direct call because an import takes minutes and a consultant's browser
/// should not be holding a connection open for it — and because the queue's visibility timeout
/// is a lock the portal does not have to implement. A message that is being worked on is
/// invisible; a worker that dies without deleting it hands the job back automatically.
/// </remarks>
public sealed class ImportQueue(
    QueueServiceClient queues,
    IOptions<StorageOptions> options,
    ILogger<ImportQueue> logger)
{
    private readonly StorageOptions _options = options.Value;

    public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var queue = await QueueAsync(cancellationToken);
        await queue.SendMessageAsync(jobId.ToString(), cancellationToken);

        logger.LogInformation("Queued import job {Job}.", jobId);
    }

    /// <summary>Null when there is nothing waiting.</summary>
    public async Task<ImportMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var queue = await QueueAsync(cancellationToken);

        var messages = await queue.ReceiveMessagesAsync(
            maxMessages: 1,
            visibilityTimeout: TimeSpan.FromSeconds(_options.VisibilityTimeoutSeconds),
            cancellationToken);

        var message = messages.Value.FirstOrDefault();
        if (message is null) return null;

        if (!Guid.TryParse(message.MessageText, out var jobId))
        {
            // Nothing else writes to this queue, so this is either corruption or somebody
            // testing. Either way it would otherwise come back every visibility timeout forever.
            logger.LogError("Discarding an unreadable queue message: {Text}", message.MessageText);
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
            return null;
        }

        return new ImportMessage(jobId, message.MessageId, message.PopReceipt, message.DequeueCount);
    }

    public async Task CompleteAsync(ImportMessage message, CancellationToken cancellationToken = default)
    {
        var queue = await QueueAsync(cancellationToken);
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
    }

    private async Task<QueueClient> QueueAsync(CancellationToken cancellationToken)
    {
        var queue = queues.GetQueueClient(_options.ImportQueue);
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        return queue;
    }
}

/// <param name="DequeueCount">How many times this message has been handed out. Includes this time.</param>
public sealed record ImportMessage(Guid JobId, string MessageId, string PopReceipt, long DequeueCount);
