namespace Acp.Portal.Domain;

public enum ProjectStatus
{
    /// <summary>Created, but nothing released yet. Clients cannot see it.</summary>
    Draft = 0,

    /// <summary>At least one version released. Clients with a grant can see it.</summary>
    Live = 1,

    /// <summary>Past its end date. Grants no longer resolve.</summary>
    Ended = 2,
}

public enum UserKind
{
    /// <summary>A member of ACP's own tenant, signing in with corporate SSO.</summary>
    AcpStaff = 0,

    /// <summary>An external client contact. A B2B guest for now; see docs/implementation-plan.md §1.2.</summary>
    ClientUser = 1,
}

public enum ProjectRole
{
    /// <summary>ACP. Can grant access and release. Only a Lead may release.</summary>
    Lead = 0,

    /// <summary>ACP. Can upload and preview, cannot release.</summary>
    Member = 1,

    /// <summary>ACP. Read-only.</summary>
    Viewer = 2,

    /// <summary>External. Sees released versions only, filtered to the companies on the grant.</summary>
    Client = 3,
}

public enum ReportVersionState
{
    /// <summary>Uploaded to Blob; the import job has not finished.</summary>
    Importing = 0,

    /// <summary>Imported, but a template check failed. <see cref="Entities.ReportVersion.CheckFailureMessage"/> says what to fix.</summary>
    CheckFailed = 1,

    /// <summary>Checks passed. Visible to ACP in preview, invisible to clients.</summary>
    Draft = 2,

    /// <summary>Released by a second person. This is what clients see.</summary>
    Released = 3,

    /// <summary>Was released; a newer version replaced it. Kept so roll-back stays a single update.</summary>
    Superseded = 4,
}

public enum UploadJobState
{
    Queued = 0,
    Processing = 1,
    Done = 2,

    /// <summary>Gave up after retries. Shows on the health page for the platform owner.</summary>
    Failed = 3,
}
