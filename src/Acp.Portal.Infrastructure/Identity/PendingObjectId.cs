namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Stand-in for a directory object id, used when a person has been invited but the directory
/// did not tell us their id. Carries a prefix rather than being null so that the unique index
/// on <c>Users.EntraObjectId</c> keeps doing its job.
/// </summary>
public static class PendingObjectId
{
    public const string Prefix = "pending:";

    public static string Create() => $"{Prefix}{Guid.NewGuid():N}";

    public static bool IsPending(string objectId) =>
        objectId.StartsWith(Prefix, StringComparison.Ordinal);
}
