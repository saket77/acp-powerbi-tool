namespace Acp.Portal.Infrastructure;

/// <summary>
/// A request that was understood, permitted, and still must not happen — a slug already taken,
/// a role that does not fit the person, the last lead on a project trying to remove themselves.
/// </summary>
/// <remarks>
/// Distinct from "not found", which is what *everything* to do with access returns. If a rule
/// violation would reveal that something exists, it is not one of these: it is a 404.
/// </remarks>
public class PortalRuleException(string reason) : Exception(reason);
