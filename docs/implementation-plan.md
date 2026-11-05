# MVP implementation plan

Built for [the wireframe](wireframe.md). Written to be executed — each phase has a definition
of done you can check without trusting me.

**The MVP in one sentence:** two fictional clients, each signing in with their own Microsoft
account, each seeing only their own Databook, with the consultant publishing it unaided and a
second person releasing it — and a test suite that proves Client A cannot reach Client B.

---

## Part 1 — Decisions I made for you

You said to figure these out. Here is what I decided and why, so you can overrule any of it.

### 1.1 What F2 means, and why you need one

The F-number is just **how much compute you rent**. F2 is the smallest, F4 is twice that, F64
is the big one. It is a slider, not a product choice — moving F2 → F4 takes minutes and no code
changes.

You need one because of two hard rules:

- **Embedding in production requires a capacity.** Without one, `GenerateToken` stops working
  after a grace period. This is the whole product, so there is no way around it.
- **The upload checks require a capacity**, because they run over the XMLA endpoint. XMLA
  read/write is on by default on every F SKU since June 2025, so F2 is fine for this.

**The cost lever is pause, not size.** A Fabric capacity bills compute per second while running
and stops billing when paused — only storage keeps ticking. F2 is roughly **$0.36–0.44/hour**
running, about **$240–260/month** if you leave it on.

So: **create an F2, keep it paused, resume it when you work.** A development day costs about a
dollar. `scripts/capacity.sh resume` / `pause` will be the first thing in the repo, and pausing
goes in the end-of-session habit. Figures are third-party and move — check the Azure pricing
page for Germany West Central before quoting any of it to partners.

We only need the capacity from **Phase 4** onward. Phases 0–3 cost nothing.

### 1.2 Everyone signs in with Microsoft — yes, this works

Your instinct was right and it is simpler than the wireframe assumes.

| Who | How | Setup needed |
| --- | --- | --- |
| You and ACP staff | Member of your Entra tenant, normal SSO | None |
| Client users | **B2B guest** invited by email | **None** |

The useful fact: **B2B guests can use their own personal Microsoft account with zero extra
configuration.** Microsoft account is on by default in External Identities. So your "two random
emails" can be two `outlook.com` accounts, or any work Microsoft account, and they sign in as
themselves. MFA comes from Entra security defaults.

One tenant, one app registration, two kinds of user. Nothing else to stand up.

**This deviates from the wireframe**, which specifies a separate Entra External ID tenant. That
is the right production answer — it keeps client identities out of ACP's corporate directory —
but it is a second tenant to own, brand and configure, and it buys nothing the MVP needs to
prove.

**How we keep the deviation cheap.** The portal will never ask the identity provider anything
except *who is this*. Authorization lives entirely in SQL:

```csharp
// The ONLY thing the IdP gives us. Everything else comes from our database.
public interface IIdentityResolver {
    Task<PortalUser> ResolveAsync(ClaimsPrincipal principal); // → stable UserId
}
```

Moving to External ID later is a new app registration, a different claim to read for the stable
id, and a config change. No business logic moves. I will write the seam deliberately and put a
test on it.

### 1.3 "YouTube for Power BI" — half of it exists

Splitting your analogy in two:

**The player is solved.** `powerbi-client-react` is Microsoft's official embed SDK. It takes a
token and a report id and renders an interactive report in a div. We will not write any of that.

**The rest is not available as an SDK.** The multi-tenant publishing, access control, template
validation and release workflow — the actual product — has only **commercial partner
accelerators** (Reporting Hub, EmbedFAST, Entelexos, EmbeDash). They are products you buy, not
libraries you import, and ACP already decided against buying, for the reason that still holds:
*consultants must publish without help, and the upload checks are specific to the Databook
template.* No partner product knows what `Template Version` means.

What we will lean on:

| Need | Use |
| --- | --- |
| Render the report | `powerbi-client-react` |
| Provision, import, tokens | Power BI REST API |
| Inspect the model for checks | `Microsoft.AnalysisServices.Tabular` (TOM) + ADOMD.NET over XMLA |
| Sign-in | `Microsoft.Identity.Web` (backend), MSAL (browser) |
| Invite guests, look up staff | Microsoft Graph |
| Reference implementation | `DOTNET5-AppOwnsData-Tutorial` — read it, don't fork it; it's .NET 5 |

### 1.4 Other deviations from the wireframe, with the reason

| Wireframe | MVP | Why | Cost to reverse |
| --- | --- | --- | --- |
| Entra External ID tenant | B2B guests in one tenant | §1.2 | Low — behind `IIdentityResolver` |
| Azure Functions for background work | `BackgroundService` in the API process, same Azure Storage Queue | One thing to run and debug; identical job code | Low — move the class, keep the queue |
| Azure Communication Services email | Log the email, show the invite link in the UI | Email provisioning is a side quest; the demo is in one room | Low |
| User-assigned managed identity | App registration + secret **locally**, managed identity **when deployed** | A managed identity cannot authenticate from your laptop | Medium — the profile-ownership rule below |
| KPI snapshot in the pipeline | Compute on read for the MVP | Not on the security path | Low |
| MSAL in the browser | OpenID Connect on the **server**, opaque cookie in the browser | See below | Low — it is one file |
| — | A second sign-in source for local work | See below | Low — delete two endpoints |
| Any access change ends the session | Only **removal and narrowing** do | See below | Trivial — one `if` |
| Power BI .NET SDK | Six typed `HttpClient` calls | Phase 4 above | Low |
| Release in Phase 5 | Release in Phase 4 | Gate 1 needs it | None — it moved earlier, not later |

**Why the sign-in moved to the server** (added in Phase 2). The plan said MSAL in the browser
*and* an HttpOnly cookie, and the wireframe's rule that no token is visible in the browser. All
three cannot be true at once: MSAL's whole job is to hold tokens in the browser. The portal
calls Graph and Power BI as itself and never as the person, so a token for the signed-in user
would be a credential with no purpose — and the safest place for one of those is nowhere. So
`Microsoft.Identity.Web` does the OpenID Connect exchange server-side, the principal is replaced
with the portal's own five claims before anything is written, and the browser receives an opaque
cookie. MSAL is still involved — it is what `Microsoft.Identity.Web` uses underneath — just not
in the browser. The visible consequence is that the app registration is a **Web** platform with
a client secret rather than a single-page application.

**Why there is a development sign-in** (added in Phase 2). Phases 1–3 need no Azure, so needing
a Microsoft tenant only to see a screen would have blocked three phases on an afternoon of
clicking. The portal therefore has a second claims source: a roster in configuration and a
picker. It is not a bypass — the same resolver, classification, session cookie and security
stamp run either way, which is exactly what makes it worth having, because the development loop
exercises the production code path instead of avoiding it. It refuses to start outside the
Development environment, and its endpoints are not mapped in any other mode.

**Why only removal ends a session** (added in Phase 3). Rule 4 of the API rules says any change
to a person's access ends their session on the next request. Taken literally that signs a client
out at the moment somebody grants them a Databook, which is confusing for no gain: a new grant
only ever widens what they can see, and the next query picks it up anyway. So the security stamp
is bumped when a grant is removed or an existing one is changed, and not when a brand-new one is
added. Revocation still bites immediately, which is what the rule is actually for.

> **The one that bites if you get it wrong.** Service principal profiles belong permanently to
> the identity that created them. If you create profiles locally with an app registration and
> then deploy with a managed identity, the deployed app **cannot see those profiles**. So: use
> **one identity per environment and never change it**, and treat dev profiles as disposable.
> Phase 4 includes a teardown script for exactly this reason.

---

## Part 2 — What you need to get

Only you can do these. Everything else I can build.

| # | Thing | Cost | When | Notes |
| --- | --- | --- | --- | --- |
| D1 | Microsoft 365 / Entra tenant you are admin of | Free | Phase 0 | A Microsoft 365 Developer tenant works. **Do not use Lutron's.** |
| D2 | Azure subscription on that tenant | Free tier + usage | Phase 0 | Pay-as-you-go; the only real spend is the capacity |
| D3 | Power BI Pro for yourself | Free 60-day trial | Phase 0 | Needed to see the service and support it |
| D4 | Two throwaway Microsoft accounts | Free | Phase 2 | Two `outlook.com` accounts. These are your two fictional clients |
| D5 | Fabric **F2** capacity, Germany West Central | ~$0.40/hr **while running** | Phase 4 | Create it paused. Resume only when working |
| D6 | **A sample `.pbix` meeting the template contract** | Free | Phase 5 | **See the warning below** |

### D6 is the one that can block you

The portal's checks require a `.pbix` containing table `Companies` with column `Ges.`, roles
`Client` and `ACP`, and a `Template Version` measure ≥ 1.6. Authoring that normally means
**Power BI Desktop, which is Windows-only — and you are on a Mac.**

Three ways out, best first:

1. **Ask Sejal for the sanitized template.** The wireframe already lists "sanitized `.pbix`
   template and fake-data file" as something Sejal owes the project. If that exists, D6 is free
   and the fake data is realistic.
2. **Build the model with TOM and download it.** We are writing TOM code for the checks anyway.
   A script can create a semantic model with the right tables, roles and measure directly in the
   workspace, after which the service's *Download this file* gives you a `.pbix`. No Windows.
   Slower to write, but it also gives us a generator for test fixtures.
3. **Rent Windows for an hour.** An Azure Windows VM, Power BI Desktop, build the sample, delete
   the VM. Costs about a dollar.

I would start by asking for (1) and write (2) if the answer is slow, because (2) doubles as the
test-fixture generator.

---

## Part 3 — What we are building

### In scope

- Sign-in for ACP staff (members) and client users (guests), all Microsoft accounts
- Create a client → automatic Power BI profile + workspace + capacity assignment
- Create projects under a client; the second project reuses the client's workspace
- Grant ACP staff to projects as Lead / Member / Viewer; grant client users with company codes
- Upload a `.pbix` → private Blob → queue → import as the client's profile → XMLA checks → draft
- Preview as a specific client ("View as") with exactly their RLS
- Release, requiring a **different** person from the uploader
- Client signs in, sees only their projects, opens the report with a 30-minute token scoped to
  one report, one model and their company codes
- Audit log of every grant change, upload, release and token issued
- **An isolation test suite that proves cross-client access fails**

### Out of scope for the MVP

Deliberately, with the reason:

| Not doing | Why |
| --- | --- |
| KPI snapshot pipeline | Not on the security path; compute on read |
| Client admins inviting colleagues | The demo has one person per client |
| Reminders, expiry, retention timers | Timer jobs are easy and prove nothing |
| Roll-back | One extra DB switch once the release path works |
| Activity tracking / most-read page | Reporting on usage, not the product |
| Templates workspace, dynamic binding | Explicitly "for later" in the wireframe |
| Break-glass | A governance control, not an MVP capability |
| Penetration test, load test, pilot | Wireframe phases 8–9, after partners say go |

---

## Part 4 — Shape of the code

```
acp-databook-portal/
├── docs/                       ← what is already here
├── infra/
│   ├── main.bicep              resource group, SQL, storage, app service, insights
│   └── capacity.bicep          F2, created paused
├── scripts/
│   ├── capacity.sh             resume | pause | status   ← use constantly
│   ├── tenant-setup.md         the clicks only you can do, as a checklist
│   ├── seed.sh                 two clients, two projects, four users
│   └── teardown-profiles.sh    delete orphaned SP profiles (see §1.4)
├── src/
│   ├── Acp.Portal.Domain/          entities, grant rules, no dependencies
│   ├── Acp.Portal.Infrastructure/  EF Core, Power BI client, Graph client, Blob, Queue
│   ├── Acp.Portal.Api/             ASP.NET Core BFF (.NET 10) + BackgroundService worker
│   └── Acp.Portal.Tests/           unit + the isolation suite
└── web/                            React + Vite + TypeScript
    ├── src/client/                 client-facing screens
    ├── src/acp/                    consultant screens
    └── src/shared/                 the six UI states, embed wrapper
```

Local development runs on **Azurite** (Blob + Queue emulator) and **SQL Server in Docker**, so
phases 1–3 need no Azure at all.

### Data model

```sql
Clients          (Id, Name, Slug, PowerBiProfileId, PowerBiWorkspaceId, CreatedAt)
Companies        (Id, ClientId, Code, Name)                    -- the Ges. codes
Projects         (Id, ClientId, Key, Name, EndDate, Status)    -- Draft|Live|Ended
Users            (Id, EntraObjectId, Email, DisplayName, Kind) -- AcpStaff|ClientUser
ProjectGrants    (Id, ProjectId, UserId, Role, EndDate)        -- Lead|Member|Viewer|Client
GrantCompanies   (GrantId, CompanyId)                          -- which codes this user sees
ReportVersions   (Id, ProjectId, Version, BlobPath, PowerBiReportId, PowerBiDatasetId,
                  State, UploadedBy, UploadedAt, ReleasedBy, ReleasedAt, ReleaseNote)
                                 -- Importing|CheckFailed|Draft|Released|Superseded
UploadJobs       (Id, ReportVersionId, State, Attempts, LastError, LockedUntil)
AuditEvents      (Id, At, UserId, Action, ProjectId, Detail)
```

**Two things that matter here.** `GrantCompanies` is the only source of a client user's company
codes — they are never read from a request. And `ReportVersions.State` is what makes release
atomic: releasing is one UPDATE, and the previously released row becomes `Superseded`, which is
also what makes roll-back a one-liner later.

SQL row-level security sits on `Projects` and `ReportVersions`, filtering on
`SESSION_CONTEXT('UserId')`, which the API sets on every connection. That is wall #2 and it
works even if application code forgets a filter.

### API surface

Taken from the wireframe's "Behind the scenes" panel, extended with what the MVP needs.

```
GET    /api/me
GET    /api/projects                                        both roles, scoped
GET    /api/projects/{key}/summary
GET    /api/projects/{key}/reports/{reportKey}/embed[?draft=true]

POST   /api/acp/clients                                     → provisions profile + workspace
GET    /api/acp/clients/{key}
POST   /api/acp/projects
GET    /api/acp/projects/{key}
POST   /api/acp/projects/{key}/grants
DELETE /api/acp/projects/{key}/grants/{grantId}
POST   /api/acp/projects/{key}/uploads                      → short-lived write-only SAS
POST   /api/acp/projects/{key}/uploads/{id}/complete        → enqueues the job
POST   /api/acp/projects/{key}/versions/{v}/release
GET    /api/acp/health
```

**Rules that apply to every one of them**, enforced in a filter rather than per-handler:

1. Power BI ids and company codes are read from the database, never from the request.
2. Not-found and not-allowed both return **404**, logged and rate-limited.
3. Every Power BI call runs as the client's own service principal profile.
4. Any change to a person's access ends their session on the next request.

---

## Part 5 — Phases

Each phase ends with something you can check yourself.

### Phase 0 — Tenant and subscription · *you, ~2 hours, no code*

I write `scripts/tenant-setup.md` as a click-by-click checklist. You work through it.

- Create the dev tenant (D1), subscription (D2), start the Pro trial (D3)
- App registration: redirect URIs, Graph scopes (`User.Read`, `User.ReadBasic.All`,
  `User.Invite.All`), Power BI delegated + application permissions
- Power BI tenant settings — **the ones that silently break everything if missed**:
  - Service principals may use the Power BI APIs → **on**, limited to one security group
  - Service principals may create and use **profiles** → **on**, same group
  - Embed content in apps → **on**
  - Publish to web → **off**
  - XMLA endpoint → **Read Write**
- Create the security group, put the app registration in it

**Done when:** a `curl` with a client-credentials token lists workspaces successfully. I give
you that command.

### Phase 1 — Skeleton and the loop · *me, no Azure needed*

Solution, projects, Docker Compose (SQL + Azurite), EF Core migrations, health endpoint, Vite
dev server proxying the API, `dotnet test` green, Bicep that deploys nothing yet.

**Done when:** `docker compose up && dotnet run` serves a page, and `dotnet test` passes.

### Phase 2 — Identity · *me + you for the invites* — **built**

`Microsoft.Identity.Web` on the API doing the OpenID Connect exchange server-side (see §1.4),
HttpOnly `SameSite=Strict` session cookie, `IIdentityResolver` mapping the token to a `Users`
row. Graph call to invite a guest. `/api/me` returns who you are and what kind.

Plus two things the plan did not name and the work needed:

- **A development sign-in**, so phases 1–3 need no tenant at all (§1.4).
- **`Users.SecurityStamp`**, checked on every request. This is where the wireframe's rule that a
  change to someone's access ends their session *on the next request* actually lives. Phase 3
  bumps it; Phase 2 issues and checks it.

You invite your two throwaway accounts (D4) through the UI.

**Done when:** you sign in as yourself and see `Kind: AcpStaff`; both client accounts sign in
with *their own* Microsoft accounts and see `Kind: ClientUser`; no token is visible in the
browser.

*Status: done against the development sign-in, which exercises the same code path. The last
sentence of the definition — your real account, two real Microsoft accounts — needs D1–D4 and
`scripts/tenant-setup.md`. Nothing in the code is waiting on it.*

### Phase 3 — Grants and SQL RLS · *me* — **built**

The data model, the grant rules, SQL RLS predicates, `SESSION_CONTEXT` wiring, the audit table,
and the 404-for-everything behaviour.

**Done when:** `Acp.Portal.Tests.Isolation` passes with Power BI entirely stubbed — a client
user asking for another client's project gets 404, and the same is true with the application's
own filters deliberately disabled, because SQL refuses independently.

*Status: done. Thirteen isolation tests against real SQL Server, built from the real migrations.*

**The predicate is uniform, with no exception for staff.** A row is visible if and only if the
session's user holds a live grant on it. The obvious alternative — filter clients, let staff
through — would have made the wall an exception rather than a rule, and exceptions get widened.
Consultants therefore see projects because they are *on* them, which is why creating a project
writes the creator's own Lead grant in the same transaction: without it they could not read back
what they had just made.

Two consequences worth knowing:

- **No block predicate on inserting a project**, because at the moment of that insert the grant
  does not exist yet. `ReportVersions` does get block predicates, where no such bootstrap exists.
- **`ProjectGrants` itself is not filtered**, because the predicate reads it and filtering it
  would recurse. Nothing exposes grants to clients; that stays an application rule.

**Two facts about `SESSION_CONTEXT`, checked rather than recalled.** A pooled connection handed
back out has its session context cleared, so one request's identity cannot leak into the next
even though it is physically the same connection. And values can be written `@read_only = 1`,
after which nothing in that session can change them — so injected SQL cannot re-stamp a
connection as somebody else. Both are load-bearing; both were verified against SQL Server 2022
before the design depended on them.

**Row-level security is invisible to EF Core.** `IgnoreQueryFilters()` turns off filters EF
itself added and knows nothing about a security policy, so no query can check whether a project
key is taken by a project the caller cannot see. The unique index is the only thing that knows,
which is why key clashes are caught from the database error rather than pre-checked.

### Phase 4 — Power BI, and the gate · *me + your capacity* — **code built, gate not passed**

**This is the phase that decides whether the design is real.** Resume the capacity (D5).

- Provision a client: create SP profile → create workspace → assign to capacity → profile as
  admin
- Import a `.pbix` as that profile, poll to completion
- `GenerateToken` V2 as the profile: one report, one model, role `Client`, `username` = the
  company codes joined by `|`, 30 minutes
- `powerbi-client-react` renders it
- Token renewal 5 minutes before expiry

**Done when — this is Gate 1:**

- Client A's user opens Client A's report and sees **only Client A's company rows**
- Client B's user opens Client B's report and sees only Client B's
- Client A's user requesting Client B's report gets **404**
- A token issued for report A, replayed against report B, is **rejected by Power BI**
- The `username` field holds the real codes for your widest client without truncation ← *the
  open question from `open-questions.md` A3, answered here for real*

If the last one fails, we switch to `CUSTOMDATA()` and the template's filter changes. Better to
find out in Phase 4 than in front of partners.

#### Status: built, and not once run against Power BI

Everything on the portal's side of the line exists and is tested: provisioning, import, release
with the four-eyes rule, the embed endpoint, the identity composition and its limits, the audit
entry, and the player with its renewal. `PowerBi:Mode=Fake` simulates the service so all of it
runs with no capacity and nothing billing.

**Be clear about what that does not prove.** Gate 1 is about the half that lives *inside* Power
BI — that a token naming one report is refused for another, and that the model's row-level
security actually filters to the codes in the identity. No fake can stand in for either. The
simulated player therefore shows the token's scope in writing rather than drawing a report, so
that nobody mistakes a green screen for a passed gate.

#### What is most likely to be wrong the first time it runs

Written against published API shapes with no capacity to check them against. In rough order of
how likely each is to bite:

| | Why it might be wrong | Cost if it is |
| --- | --- | --- |
| `POST /profiles` to create a profile | The only call whose request shape could not be confirmed from the reference | Minutes — the error names the field |
| Service principal + arbitrary `username` | Microsoft's guidance in one place says a service principal must supply **its own object id** as the effective identity. If that applies to this shape, the codes cannot travel in the username at all | Config change to `IdentityMode=CustomData`, plus the template's filter moves to `CUSTOMDATA()` — already wired, see `EffectiveIdentityFactory` |
| `xmlaPermissions: "Off"` alongside a report-scoped token | Sent to stop a viewer's token being usable to query the model directly; the combination is untested | Minutes |
| 256-character `username` ceiling | **Open question A3.** Verified as the documented limit; what is unverified is whether ACP's widest client fits under it | Same fallback as above |
| `embedUrl` from the import response | Taken from the import's report entry rather than fetched separately | Minutes |

`capacityId` on `AssignToCapacity` **was** wrong — written as `capacityObjectId`, which is what
the admin-scoped sibling call uses — and was corrected against the reference before commit. It
is listed here as the reason this table exists.

#### Two deviations

**Release was pulled forward from Phase 5.** Gate 1 needs a client to open a *released* report,
and a client sees nothing until somebody releases something. Shipping Phase 4 without it would
have meant either no gate or a weaker rule in the interim, so the four-eyes rule arrives whole.
What stayed in Phase 5 is the upload *pipeline*: private Blob staging, the queue, the background
worker, and the XMLA template checks. Phase 4 streams the `.pbix` straight to Power BI and
records `direct://` in `BlobPath` so no later migration mistakes those rows for staged ones.

**The Power BI SDK is not used.** The .NET SDK is a generated client over an API surface of
which this uses six calls, it brings its own authentication stack, and its profile support works
by building a differently-configured client per profile — the opposite of what is needed here,
where the profile changes per request. Six typed calls against `HttpClient` are smaller and the
failures are legible. `powerbi-client-react` *is* used, in the browser, where it is the whole job.

### Phase 5 — Upload, checks, release · *me*

SAS issuing, direct browser → Blob upload, queue message, `BackgroundService` consumer with a
processing lock and retry, import as profile, then **XMLA checks over TOM/ADOMD**:

- roles `Client` and `ACP` both exist
- measure `Template Version` ≥ 1.6
- table `Companies` with column `Ges.`
- the codes in the model match the project's companies
- every client-visible table is actually filtered by the `Client` role

Failures produce the wireframe's plain-English message (*"This file has no Client role. Use
template 1.6."*). Then draft → preview-as-client → release by a **different** user.

**Done when:** you drop a good `.pbix` and it reaches Draft unaided; you drop a deliberately
broken one and get a message naming the fix; releasing as the uploader is refused; releasing as
a second person works and the client sees the new version.

### Phase 6 — Client experience · *me*

Sign-in landing, project switcher, Analytics home (project name, data-as-of, released date,
version, company scope, named ACP contact, report list with *updated since your last visit*,
"what changed in this release", access block), report view, and **all six states the wireframe
specifies** — Ready, Loading, Empty, Error, Busy, No access.

**Done when:** a client account signs in cold and reaches a working report without being told
anything, and each of the six states can be forced and looks deliberate.

### Phase 7 — Consultant experience · *me*

Portfolio with the six tiles, clients table, client page, project page (grants, file drop,
version history, preview-as, release), minimal health page showing failed uploads and recent
audit events.

**Done when:** you run the whole lifecycle — new client, project, grant, upload, preview,
release — through the UI, touching no Azure portal, in under five minutes. That number is the
product's core claim.

---

## Part 6 — The test suite that matters

Everything else is ordinary. This is the part worth over-building, because it is the claim the
partners are being asked to believe.

`Acp.Portal.Tests.Isolation` runs the wireframe's own attack table as tests:

| Test | Asserts |
| --- | --- |
| `ClientA_RequestingClientB_Project_Gets404()` | Grant join finds nothing |
| `ClientA_Token_Replayed_On_ClientB_Report_IsRejected()` | Power BI refuses, not just us |
| `Request_With_Tampered_CompanyCodes_IgnoresThem()` | Codes come from the DB only |
| `Sql_Rls_Blocks_When_Application_Filter_Disabled()` | Wall 2 standing alone |
| `Revoked_Grant_Ends_Session_On_Next_Request()` | No stale sessions |
| `Revoked_Grant_Refuses_Token_Renewal()` | ≤ 30 min residual, as designed |
| `Uploader_Cannot_Release_Own_Version()` | Four-eyes holds |
| `Client_Identity_Has_No_PowerBi_Access()` | app.powerbi.com is closed to them |

The last one needs a live tenant; the rest run on stubs in CI.

---

## Part 7 — Keeping the bill near zero

1. **Pause the capacity.** `scripts/capacity.sh pause` at the end of every session. This is the
   whole cost story — everything else is cents.
2. Phases 0–3 touch no Azure. Build them first; that is most of the work.
3. Azure SQL serverless auto-pauses; App Service free tier is enough for the MVP.
4. An Azure budget alert at EUR 50/month, set in Phase 0, so a capacity left running wakes you
   rather than the invoice.

Realistic MVP spend: **under EUR 30** if you pause reliably. Demo day on F2 is about EUR 4.

---

## Part 8 — What I need from you to start

Phase 1 needs nothing — I can start the moment you say go, and it is several days of work.

Running in parallel, when you get to them:

1. **Phase 0 checklist** (D1–D3) before Phase 2 lands.
2. **Two throwaway Microsoft accounts** (D4) before Phase 2 ends.
3. **The F2 capacity** (D5) before Phase 4. Not before — it bills.
4. **A sample `.pbix`** (D6) before Phase 5. Ask Sejal first; tell me early if that is slow so I
   can write the TOM generator instead.

And two documents that would sharpen this, if they exist: the *Databook Portal: Architecture
Review* (28 Oct), and the paused ACP requirements draft with its data model.

---

## Sources

Facts verified rather than recalled, because the plan leans on them:

- [Semantic model connectivity with the XMLA endpoint](https://learn.microsoft.com/en-us/fabric/enterprise/powerbi/service-premium-connect-tools)
- [XMLA read/write default change, June 2025](https://mc.merill.net/message/MC1075915)
- [Capacity and SKUs in Power BI embedded analytics](https://learn.microsoft.com/en-us/power-bi/developer/embedded/embedded-capacity)
- [Pause and resume your capacity](https://learn.microsoft.com/fabric/enterprise/pause-resume)
- [Use Microsoft Accounts with Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/microsoft-account)
- [B2B collaboration invitation redemption](https://learn.microsoft.com/en-us/entra/external-id/redemption-experience)
- [Embedded analytics accelerator solutions from Microsoft partners](https://learn.microsoft.com/en-us/power-bi/developer/embedded/embedded-analytics-accelerators)
- [Embed with a service principal](https://github.com/MicrosoftDocs/powerbi-docs/blob/main/powerbi-docs/developer/embedded/embed-service-principal.md)

Pricing figures are third-party and move; confirm on the Azure pricing page for Germany West
Central before quoting them to anyone.
