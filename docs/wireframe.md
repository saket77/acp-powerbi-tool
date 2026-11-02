# The wireframe — current source of truth

Source: `docs/source/wireframe.html` (154 KB, self-contained interactive concept, titled
"Databook portal — Concept for review").

**This is the newest and most detailed artifact, and it supersedes both PDFs.** It is not a
sketch: it carries concrete version numbers, confirmed Microsoft API limits, a 22-step
lifecycle, a costed plan, and a per-click "Behind the scenes" panel documenting what the
backend does. **The implementation plan should be built against this.**

To open it:

```bash
open /Users/smundhada/repos/acp-databook-portal/docs/source/wireframe.html
```

It has ten views: Overview, Client, ACP team, Step by step, Security, Power BI setup,
Automation, How it works, Maintenance, The plan. Plus a guided tour.

## Headline claims the concept makes

| Figure | Value |
| --- | --- |
| Viewers need | **No licence** (app owns data) — ACP staff viewing without Pro still to be confirmed with Microsoft |
| Running cost | **EUR 350–450/month** on F2 — flat whether 20 or 800 people view |
| Walls between clients | **5** — grants, SQL RLS, profile, token, model RLS |
| Steps for Sejal per project | **0** — support only |
| Automation | **13 of 22 steps** run by themselves |

Cost comparison it uses to make the case: Pro for every viewer would be ~EUR 10,000/month;
F64 ~EUR 4,600–7,800. F2 is ~EUR 240, the rest of Azure ~EUR 100–180. A load test before the
pilot confirms F2.

## Stack, as named

| Piece | Choice |
| --- | --- |
| Portal pages | React |
| API and session | **ASP.NET Core backend-for-frontend on Azure App Service, .NET 10 (LTS)** |
| Background | Azure Functions — imports, checks, timers |
| Client sign-in | **Entra External ID** (separate tenant) |
| ACP sign-in | Microsoft Entra ID, SSO + Conditional Access |
| Records | Azure SQL — grants, audit, KPIs |
| Uploads | Blob storage |
| Secrets | **User-assigned managed identity — "no secrets"**; Key Vault for configuration |
| Email | Azure Communication Services |
| Monitoring | Application Insights |
| Analytics | Power BI Embedded on **Fabric F2** |
| Delivery | GitHub Actions (federated credentials) + Bicep |

.NET 10 is supported until November 2028.

## The 22-step lifecycle

13 automatic · 7 ACP team · 2 client · **0 Sejal**. Only steps 8–15 repeat for a new Databook
version.

| # | Step | Who |
| --- | --- | --- |
| | **SET UP** | |
| 1 | Create the project — client, companies with their codes, end date. One short form | ACP |
| 2 | Profile and workspace for a new client; later projects reuse them | auto |
| 3 | Add ACP colleagues — rank proposes Lead/Member/Viewer, the lead decides | ACP |
| 4 | Grants saved, rights applied; rank is informational | auto |
| | **GIVE ACCESS** | |
| 5 | Add client contacts (or the client admin does) — email, function, companies, end date, PDF yes/no | ACP |
| 6 | External ID account, single-use invitation link, email code as second factor | auto |
| 7 | Client accepts once; sets a password, passkey optional | Client |
| | **PUBLISH** | |
| 8 | Refresh the Databook in Power BI Desktop — as today, data sources stay on ACP's side | ACP |
| 9 | Drop the `.pbix` in the portal — one file drop on the project page | ACP |
| 10 | Import as the client's profile; **checks through XMLA** — template version, roles, company codes, every client table filtered | auto |
| 11 | KPI snapshot — headline figures per company scope for the Analytics home | auto |
| 12 | Clear message if a check fails; listed in Health | auto |
| 13 | Preview, also as each client — "View as", the same RLS the client gets | ACP |
| 14 | **Another lead releases — four-eyes rule**; release note for clients | ACP |
| 15 | Live switch and email; previous version kept for roll-back; **model pre-warmed** | auto |
| | **USE** | |
| 16 | Client signs in and opens the Databook | Client |
| 17 | Token per view, renewed in the background — one report, one model, the client's codes, 30 min | auto |
| 18 | Activity for the ACP team — last opened, views, most-read page | auto |
| | **END** | |
| 19 | Reminder before the end date, to the leads, with one-click extension | auto |
| 20 | Access ends on the end date; External ID account disabled if on no other project | auto |
| 21 | Quarterly access review — each lead confirms their client contacts | auto |
| 22 | Clean-up after retention; model and report deleted, **audit log kept** | auto |

## Five walls between Client A and Client B

Each wall alone stops the common attacks; together they also cover a bug in any one of them.

1. **Server-side grants** — every request resolves the project through the caller's active
   grant. IDs from the browser are never trusted; *not found* and *not allowed* both return 404.
2. **SQL row-level security** — the database returns only projects the session's user holds,
   even if the application forgets a filter.
3. **One Power BI profile per client** — tokens are generated as the client's own profile,
   which can only reach that client's workspace.
4. **Token scoped to one report and one model** — view only, 30 minutes, with the user's
   company codes. Useless for any other item.
5. **RLS inside the model** — role `Client` filters by company code; ACP-only tables return
   nothing to clients.

### Attack table (from the Security view)

| A Client A user tries | What stops it |
| --- | --- |
| Changes the URL to Client B's project | Grant join finds nothing: 404, logged, rate-limited |
| Edits the API request (IDs, company codes) | Power BI IDs and codes are never read from requests |
| Hopes for a bug that loads the wrong project | SQL RLS, and Client B's workspace refuses Client A's profile |
| Reuses the token on another report | Token names exact item IDs; Power BI rejects others |
| Opens app.powerbi.com | Client identities live in External ID and have no Power BI access |
| Opens a hidden page with developer tools | The `Client` role filters its data to nothing |
| Keeps watching after access was removed | At most until the token expires (30 min); renewal refused |

### Residual risk, stated plainly

- Screenshots cannot be prevented — stated in the client terms.
- Up to 30 minutes of viewing after access is removed.
- Wrong numbers inside a Databook: preview and four-eyes release, not technology.
- Break-glass misuse: reason, time limit, notification, review.

Session: HttpOnly, `SameSite=Strict` cookie; no sign-in token in the browser. **Any change to a
person's access ends their old session on the next request.**

## Power BI layout

One workspace per client, owned by that client's own service principal profile. Each project
adds one semantic model and its report inside it. **About 200 workspaces for 200 clients,
stable — projects add models, not workspaces.** Plus a `Templates` workspace holding golden
reports for later dynamic binding, owned by the managed identity.

Nobody opens app.powerbi.com except Sejal, for support. Workspaces have no human members;
clients have no Power BI identity at all.

### Why this layout (comparison it makes)

| Layout | For | Against |
| --- | --- | --- |
| **Workspace per client + profiles** (chosen) | Power BI refuses cross-client tokens; fits one Databook per engagement; Microsoft's recommended pattern | Onboarding automation needed (one API call per client) |
| One shared model + RLS | One report, one model | One filter mistake exposes all clients; needs a central model ACP doesn't have |
| Workspace per project, one identity (earlier) | Simple | Isolation rests on application code alone |

### Confirmed limits — these answer earlier open questions

- **Execute Queries does not support service principals on RLS models**, so checks use the
  **XMLA endpoint**, which needs a capacity. *This is why XMLA is in the design.*
- Each profile can reach up to **1,000 workspaces**; one service principal can own up to
  **100,000 profiles**.
- **Profiles belong to the identity that created them** — the managed identity is locked
  against deletion.
- Still to test: **memory per model on F2 for the largest Databook**, and **files over 1 GB**.

## The embed token

`GenerateToken` V2 as the client's profile: one report, one model, role `Client`,
`username` = the company **codes** joined by `|`, 30 minutes. Renewal 5 minutes before expiry;
an audit row is written. For ACP staff the role is `ACP` — no filter, ACP-only pages visible.

Preview uses the same path: role `Client` with the client's codes, so "View as" gives exactly
the RLS the client gets.

## API surface (from the "Behind the scenes" panel)

```
GET /api/me
GET /api/projects/{projectKey}/summary
GET /api/projects/{projectKey}/reports/{reportKey}/embed[?draft=true]
GET /api/acp/clients/{clientKey}
GET /api/acp/projects/{projectKey}
```

The panel states the rule for all of them: *every Power BI call runs as the client's own
service principal profile*, Power BI IDs are read from the database, never from the request.

## Automated jobs and their difficulty

The concept rates its own work — **7 easy, 5 medium, 1 tricky**:

| Job | Trigger | How | Difficulty |
| --- | --- | --- | --- |
| Onboard a client | First project of a new client | Power BI REST: create profile, create workspace, assign to capacity, profile as admin | Easy |
| Find colleagues and rank | Add person (ACP) | Microsoft Graph: user search, job title | Easy |
| Invite a client user | Add person / client admin invite | Graph creates the External ID account; single-use link; identity linked at first sign-in | Medium |
| Upload large files safely | File dropped | Browser uploads straight to Blob with a short-lived write-only link | Easy |
| Import as a draft | Upload finished | Power BI Import API as the client profile; poll until done | Medium |
| Checks and KPI snapshot | Import finished | **XMLA endpoint with TOM / ADOMD.NET** under the profile; DAX for template, roles, codes, KPIs | Medium |
| Embed token per view | Report opened | GenerateToken V2 as the client profile | Easy |
| Release, roll back, notify | Click | Database switch; email via Communication Services | Easy |
| Client activity | Report events | Page events from the embedded report sent to the API | Medium |
| End access, clean up | Nightly timer | Grants expire; Graph disables accounts; Power BI deletes after retention | Easy |
| Quarterly access review | Timer | Email each lead; one-click confirm | Easy |
| **Capacity alert** | Hourly | **No simple API for capacity metrics** — alert on slow or failed loads; Capacity Metrics app weekly | **Tricky** |
| Deploy a new version | Merge to main | GitHub Actions with federated credentials; Bicep | Medium |

## The plan it already proposes

Eleven phases, three gates. The demo covers phases 0–7 in reduced form on example data: no
capacity for embedding, a few hours of pay-as-you-go capacity for the XMLA checks.

| Phase | Content | Demo | Production |
| --- | --- | :-: | :-: |
| 0 | Discovery and decisions | ✓ | ✓ |
| 1 | Foundations: repository, environments, deployment | ✓ | ✓ |
| 2 | Identity and sessions (ACP Entra ID, External ID) | ✓ | ✓ |
| 3 | Data model and authorization, SQL row-level security | ✓ | ✓ |
| 4 | Power BI: profiles, workspaces, embedding | ✓ | ✓ |
| 5 | Upload, XMLA checks, release | ✓ | ✓ |
| 6 | Client analytics experience | ✓ | ✓ |
| 7 | ACP analytics, administration, health | ✓ | ✓ |
| 8 | Security hardening, penetration test | – | ✓ |
| 9 | Pilot with two clients, capacity load test | – | ✓ |
| 10 | Go-live, monitoring, handover | – | ✓ |

**Gate 1** (after phase 4): the security path works — two example clients, each sees only its
own data. **Gate 2** (after the demo): partners say go. **Gate 3** (after phase 9): the pilot
works — two clients use it, no isolation findings.

### Decisions needed before phase 1

- **Patrik** — approve the architecture and the rule for restricted projects.
- **Microsoft partner** — ACP staff viewing without Pro; current F2 price and reservation.
- **Sejal** — list the existing reports, their workspace and capacity, and the tenant settings.
- **Data protection officer** — activity data, client terms, retention.

### People

| Person | Role |
| --- | --- |
| Sejal | Builds and owns the portal; Azure, Entra and Power BI setup; template; support |
| Patrik | Sponsor; decision at each gate; notified on break-glass |
| Data protection officer | Activity data, client terms, retention |
| T. Thal | Backup admin — second platform admin account |
| Pilot users | Two consultants, two client contacts |

## Setup Sejal does once

- User-assigned managed identity, in the Power BI security group.
- Tenant settings: service principals may use the APIs **and create profiles** (that group
  only); embedding on; **publish to web off**; **XMLA read on**.
- External ID tenant: branding, **sign-up off**, email code, Conditional Access.
- Fabric capacity in an EU region.
- Azure resources from the Bicep templates.
- `Client` and `ACP` roles and company codes in the Databook template.

Ongoing: new template versions, approved client email domains, break-glass support (rarely).

## Maintenance posture

| Item | How often | How |
| --- | --- | --- |
| Secrets and certificates | **Never** | Managed identity — nothing to rotate. **Resource lock on it, because it owns the profiles** |
| Server patches | Never | App Service, Functions and SQL are managed |
| Backups | Continuous | Azure SQL point-in-time restore; soft delete on files |
| Library updates | Monthly | Dependabot opens, Sejal merges, deployment automatic |
| .NET version | 2–3 years | .NET 10 supported to Nov 2028 |
| Client access review | Quarterly | Each lead confirms their contacts |
| Capacity size | Quarterly | Capacity Metrics app; F2 → F4 in minutes if needed |
| Restore and profile recovery drill | Yearly | Rebuild from GitHub in a test subscription; run the profile recovery script |

Long-run watch items it names: one person (Sejal builds, runs and supports — backup admin,
runbooks and IaC make handover possible); Microsoft changing Fabric prices and SKUs; growth
(heavy clients can move to their own capacity without code changes).

## UI detail worth keeping

The client report view has a **state switcher** built into the concept — Ready, Loading, Empty,
Error, Busy, No access. Those are the states the real UI has to implement, and they are
specified rather than left to chance.

Client Analytics home carries: welcome, project name, data-as-of date, released date, version
number, company scope, named ACP contact with email, a KPI strip (Revenue, EBITDA, Net working
capital, Net debt/EBITDA, Cash — each with prior-year delta), a report list with per-report
"Updated since your last visit", a "What changed in this release" note with releaser and date,
and an access block (companies, until-date, view only, last opened).

ACP Portfolio carries: Active projects, Released this month, Client users active, Median data
age, Failed checks, Ending soon — then a clients table (Projects, Your access, Last release,
Newest data, Issues). Totals include only projects the signed-in person may see. The persona
switcher runs from Partner down to Working student plus two BI admins, which is how the grant
model gets demonstrated.
