# What changed between the two documents

Two source documents exist and **they do not agree**. The 29 Oct architecture proposal closes
with an explicit supersession note:

> "The revised architecture review supersedes earlier suggestions of a single shared service
> principal, B2B guest client identities in ACP's tenant, and Azure Functions serving the
> primary interactive API."

Those three are the named reversals. Comparing the documents line by line turns up several more.
When writing the implementation plan, **follow the 29 Oct column.**

## Named reversals

| # | 26 Oct brief said | 29 Oct architecture says | Why it matters |
| --- | --- | --- | --- |
| 1 | One shared robot service principal for everything; "one robot or many" listed as an open spike | **A service-principal profile per client**, each limited to its own workspace | Decided, not open. Changes provisioning, Key Vault layout, and every Power BI REST call (profile header on each request) |
| 2 | Clients as **B2B guests in ACP's Entra tenant** | **A separate Entra External ID tenant** for clients | Different product, different SDK configuration, different invitation and MFA setup. Keeps external identities out of ACP's corporate directory |
| 3 | **Azure Functions** as the backend API | **ASP.NET Core on Azure App Service** is the interactive API; Functions do background work only | Cold starts and execution limits are wrong for a live embed-token endpoint. Functions keep import/validation/timers |

## Further differences found on comparison

| Topic | 26 Oct brief | 29 Oct architecture | Note |
| --- | --- | --- | --- |
| Workspace granularity | One workspace **per project** | One workspace **per client**, reused; one report + model **per project** | Material. ~200 projects/year against a far smaller number of clients — changes capacity maths and the workspace-count ceiling |
| Portal hosting | React on Azure **Static Web Apps** | React + ASP.NET Core on **App Service** | Follows from reversal 3 |
| Upload path | Implied through the backend | Browser uploads **direct to private Blob** via short-lived **write-only SAS**; API only records `UploadId` and queues | Avoids pushing large `.pbix` through the web tier |
| Job triggering | Not specified | **Azure Queue Storage** + queue trigger, with processing lock, retries, duplicate prevention, review queue for hard failures | New component; an entire reliability story the brief did not have |
| Release control | Consultant previews and releases | Draft requires a **separate authorized approver** — release is two-person | New governance requirement; needs a role and a UI state the brief's screens do not show |
| Embed token API | `GenerateToken` | **`GenerateToken` V2** | V2 is the multi-resource form; required for the profile model |
| Capacity | **F2**, named and costed (~$0.36/hr) | "a supported Fabric capacity", chosen **only after a representative load test**; F-SKU fit and XMLA availability to be verified | Do not assume F2 survives. See open questions |
| Template validation | Portal checks four things on upload | Same, plus deep model inspection proposed via **XMLA/TOM** — *feasibility and required capacity must be proven* | XMLA has capacity-tier implications that interact with the F-SKU choice |
| Operations | Key Vault | Key Vault + **managed identity** + **Application Insights** + IaC | Prefer managed identity over stored secrets wherever Azure supports it |
| Terminology | "2FA" | "MFA" | Same thing; use MFA |

## What both documents agree on

These are stable and safe to build against:

- App-owns-data embedding; viewers never hold a Power BI licence or sign in to Power BI.
- Consultant self-service is the whole point; the platform owner must not be in the publish path.
- Azure SQL holds projects, people, grants, report/workspace IDs and the audit log.
- Template contract: `Companies[Ges.]`, role `Client`, role `ACP`, a `Template Version` measure;
  reject the upload if any is missing.
- RLS restricts legal entities *within* a project; workspace separation handles *between* clients.
- Filter panel hidden, export off — and these are UX, not security.
- Access expires on project end date; timers handle reminders, expiry, cleanup.
- EU region, IaC, no real client data in the MVP, code belongs to ACP.

## Still only in the 26 Oct brief

Not contradicted, just absent from the newer document — carry them forward:

- The **four clickable screens** (consultant projects, consultant project page, client sign-in,
  client report) and the admin page + "Add person" dialog that were noted as not drawn.
- The **EUR 0 demo recipe**: own test tenant, Pro trial for development embed tokens, or an F2
  switched on for demo day only.
- The concrete **error-message style** for failed checks.
- 14-day end-date reminder; workspace deleted 90 days after end (configurable).
- Consultant effort target: **~5 minutes per project**.
