# Architecture — authoritative

Source: *ACP Databook Portal, Proposed Solution Architecture*, management briefing, **29 Oct
2026**. Status line in the document: "Architecture status: proposed." This supersedes the
26 Oct product brief wherever the two disagree — see [decisions-and-deltas.md](decisions-and-deltas.md).

Rendered diagrams: [architecture at a glance](source/diagrams/arch-at-a-glance.png),
[publishing flow](source/diagrams/arch-publishing-flow.png).

## 1. Layers

| Layer | Services | What happens |
| --- | --- | --- |
| Users / web | React + TypeScript; ASP.NET Core on **Azure App Service** | One branded portal for staff and clients; backend authenticates sessions, enforces grants, returns embed configuration |
| Identity | ACP **Entra ID** (staff); **separate Entra External ID tenant** (clients) | Staff use corporate SSO; clients use invitation-led login with MFA. Session held in a secure HttpOnly cookie |
| Application records | **Azure SQL Database** | Clients, legal entities / company codes, projects, memberships, access grants, uploaded versions, live report IDs, audit events |
| Upload staging | Private **Azure Blob Storage** | Browser uploads `.pbix` with a tightly scoped short-lived SAS; container private; retained only as required |
| Background processing | **Azure Queue Storage + Azure Functions** | Queue-triggered import/validation; timer-triggered expiry, reminders, cleanup. Retries, failure tracking, monitoring |
| Analytics | **Power BI Service** on a supported Fabric capacity | One workspace + service-principal profile per client; separate report/model per project; embed tokens and RLS enforce permitted views |
| Operations | Managed identity, Key Vault, Application Insights, IaC | Secure credentials, monitoring, repeatable deployment |

**Key principle.** The API authorizes access. Power BI hosts and renders the actual report.
Azure Functions handles slow background jobs, not the user's live report request.

## 2. Actors

| Actor | Responsibility |
| --- | --- |
| Consultant / project lead | Creates project; selects or requests client; grants access to approved people; uploads `.pbix`; previews draft; arranges second-person approval and releases |
| Client viewer / client administrator | Signs in with MFA; views only released, permitted projects and legal entities. A designated client admin may invite colleagues within their own scope |
| Sejal — data-engineering & platform owner | Maintains the Databook template, company-code conventions and RLS definitions; oversees failed validations, template versions, platform health. **Explicitly not a manual publishing bottleneck** |
| ACP IT / tenant administrators | Own Azure subscription, identity tenants, licensing/capacity, security approvals, production environment |
| Portal engineering | Builds UX, authorization database and API, Power BI provisioning/embedding, file processing jobs, audit and tests |

## 3. Tenancy and workspace lifecycle

- An authorized user creates a **client record**. For MVP this is administrator-approved, to
  avoid duplicates.
- The backend provisions a **client-scoped service principal profile and workspace** in Power
  BI, and stores their IDs in Azure SQL.
- A second project for the same client **reuses that workspace**; its `.pbix` becomes a
  separate semantic model / report pair.
- A client with multiple legal entities can have different viewing scopes. **Project grants**
  decide which users see which project; **RLS** decides which company-code rows are visible
  inside it.
- Access expires with project/grant dates. A background timer handles notification, expiry and
  retention under ACP-approved policy.

Three nested isolation boundaries, in order: client workspace → project report/model → RLS rows.
The document is explicit that *filters or hidden report pages alone are NOT security controls*.

## 4. Upload flow, step by step

| Step | System action |
| --- | --- |
| 1. Upload request | Consultant chooses project and `.pbix`. API checks project role, file type/size, returns a **short-lived write-only Blob SAS** for one upload |
| 2. Private staging | Browser uploads **directly to Blob**, not through the web server. API records an `UploadId` and queues processing after upload completes |
| 3. Queue-triggered Function | Function reads upload metadata, takes a **processing lock**, streams the staged file, calls the Power BI **Import API** as the client-scoped profile |
| 4. Import status | Polls import status, records report and model IDs, retries transient failures, **prevents duplicate imports**, routes hard failures to a review queue |
| 5. Validate | Checks allowed template version, company-code list, expected RLS roles, checks/measures, report configuration. Deep model inspection proposed via **XMLA/TOM** — *feasibility and required capacity must be proven* |
| 6. Draft and release | Passed version becomes draft; project lead previews **with a client identity**; a **separate authorized approver** releases. Only the released version reaches clients |
| 7. Post-release | KPI snapshot, release notices, scheduled reminders, access expiry, old-upload deletion, retention |

If a check fails, the consultant gets a plain message naming the fix (e.g. *"This file has no
Client role. Use template 1.6."*) and uploads a corrected file.

> Blob Storage is **staging for the `.pbix` only** — it is not where clients query visual data.
> Once imported, Power BI Service hosts the semantic model and report. For very large `.pbix`
> files Microsoft has a separate temporary-upload-location API; evaluate file sizes before
> choosing the transfer method.

## 5. Viewing and data isolation

1. Client signs in. The application resolves user ID, active project grant, report version and
   permitted company codes **server-side**.
2. Backend calls Power BI **GenerateToken V2** for that exact report and semantic model, using
   **that client's profile** and an appropriate effective identity / RLS role.
3. The React Power BI SDK renders the report in a managed iframe. **The browser never receives
   the service principal credential.**
4. Company identifiers are stable codes in the Databook — currently the FIN Tool `Ges.` code.
   The template needs `Client` and `ACP` roles; the client RLS filters rows by permitted codes.

The 26 Oct brief gives the concrete token shape; treat the identity-passing mechanism as
unsettled (see [open-questions.md](open-questions.md#how-company-scope-reaches-rls)):

```jsonc
{
  "datasets":   [ { "id": "<datasetId>" } ],
  "reports":    [ { "id": "<reportId>"  } ],
  "identities": [ {
    "username": "Muster GmbH|Muster Logistik GmbH",
    "roles":    [ "Client" ],
    "datasets": [ "<datasetId>" ]
  } ],
  "lifetimeInMinutes": 60
}
```

Consultants get role `ACP` with their email as username. The filter panel is hidden and export
is switched off, so a client cannot remove the company filter or download the data. Tokens last
one hour; the page fetches a fresh one and calls `report.setAccessToken(newToken)` before expiry.

## 6. Security and governance

- **Explicit project grants** (Lead, Member, Viewer, Client) rather than access inherited from
  seniority. Restricted engagements stay visible only to named members.
- Platform-configuration admin rights **do not** imply silent access to client financials.
  Emergency access must be time-limited and audited.
- Managed identity where supported; every service principal profile limited to its own client
  workspace; no credentials persisted in the browser.
- Private Blob containers, short-lived SAS, malware/content checks as applicable, configurable
  cleanup, encryption in transit and at rest. **Logs must omit financial values, secrets and
  embed tokens.**
- Every authorization route tested across clients and projects. Note the known gap: revoking
  access blocks *new* embed tokens, but **already-issued tokens stay valid until they expire**.
- ACP IT and privacy stakeholders must approve EU data residency, retention, security posture,
  client invitation policy and operational ownership.

## 7. MVP scope and success criteria

Demonstration scope: two fictional clients, two projects for one client, one consultant/project
lead, a second approver, two external viewers, and one authentic template-based Databook with
3–4 report pages. **The demonstration must exercise the real integration, not a simulated iframe.**

| MVP feature | Demonstrable outcome |
| --- | --- |
| Login and grants | Consultant and client accounts authenticate; cross-client requests are refused |
| Client / project creation | First client auto-provisions profile + workspace; second project reuses it |
| PBIX upload & checks | Private Blob upload starts a queue-triggered Function; file imports; errors are visible |
| Preview / release | Draft is checked and previewed; release requires a distinct approver |
| Client reporting | Client A sees only its company-code rows; Client B cannot open Client A's report |
| Operations | Upload status, failed jobs, access log and token expiry are observable |

**Not in the first MVP:** migration of real client files, complex client self-administration,
advanced exports, portfolio reporting, production capacity optimization.

## 8. Proposed sequence

| Gate | Proves |
| --- | --- |
| **1** | Service-principal profile → workspace creation → PBIX import → working client-scoped RLS embed, on fictional data |
| **2** | Uploads through Blob + queue-triggered Function, validation, draft/release |
| **3** | Styled portal, project/user workflows, admin health |
| **4** | Security tests, EU hosting, operational handover, capacity pilot |
