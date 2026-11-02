# Open questions

Revised after reading the wireframe, which answers several of these. Three groups: **still
open**, **answered** (kept so the reasoning is not re-litigated), and **decisions owed by
people**.

---

## A. Still open — must be proven before or during build

### A1. Memory per model on F2 for the largest Databook
The wireframe names this as untested, and it is the one that can invalidate the cost story.
EUR 350–450/month rests on F2 holding the heaviest Databook — Cash Flow with all levels
expanded. Fallback F4 (the Maintenance view says F2 → F4 "in minutes"), but that changes the
number the partners are being shown. **Test before Gate 2, not after.**

### A2. Files over 1 GB
Also named as untested. Power BI's Import API has a separate temporary-upload-location path for
large files, and which one applies changes the Function's import code. Measuring the real
Databook file sizes is an hour's work and de-risks phase 5.

### A3. The `username` length limit
The design puts company **codes** joined by `|` into the embed token's `username` field. Codes
rather than display names is the safer choice and already adopted. What remains unverified is
the field's length limit against ACP's widest client — a group with many legal entities.

`customData` / `CUSTOMDATA()` is the field Microsoft designed for carrying this kind of context
and is worth testing alongside in phase 4, if only to know the fallback. *(My note, not from
ACP material — the wireframe commits to `username`.)*

### A4. ACP staff viewing without Pro
Listed in the wireframe's own decisions as a question for ACP's Microsoft partner. It affects
whether consultants need a Pro licence each to use the portal they built, which is a real line
item at ACP's headcount.

### A5. Capacity metrics have no simple API
The wireframe rates this its only "tricky" job. The workaround — alert on slow or failed loads,
read the Capacity Metrics app weekly — is a degraded signal. Fine for MVP; name it as a known
gap at handover rather than discovering it in production.

### A6. CORS on the storage account
The browser uploading straight to Blob with a SAS requires CORS configured on the storage
account. Trivial, but it belongs in the Bicep, not clicked in the portal. *(My note.)*

### A7. Entra External ID is a separate tenant to own
Right call, but not a toggle: separate tenant, app registration, invitation flow, MFA policy,
branding, Conditional Access. Confirm ACP IT will own a second tenant and budget real time for
phase 2. *(My note.)*

---

## B. Answered — do not reopen

| Question | Answer | Source |
| --- | --- | --- |
| One service principal or many? | **Profile per client.** Limits confirmed: 1,000 workspaces per profile, 100,000 profiles per SP | Wireframe, Power BI setup |
| Why XMLA rather than a simpler query API? | **Execute Queries does not support service principals on RLS models.** XMLA is the only path, and it needs a capacity | Wireframe, Power BI setup |
| Workspace per project or per client? | **Per client.** ~200 workspaces for 200 clients; projects add models, not workspaces | Wireframe + 29 Oct architecture |
| How long does a revoked client keep access? | **At most 30 minutes** — token lifetime. Accepted and stated in the residual-risk list | Wireframe, Security |
| Where do client identities live? | **Separate Entra External ID tenant**, invitation only, sign-up off, email code | Wireframe + 29 Oct architecture |
| What stops cross-client access? | **Five independent walls** — grants, SQL RLS, profile, token scope, model RLS — with an attack table per wall | Wireframe, Security |
| How are secrets managed? | **There are none.** User-assigned managed identity; Key Vault holds configuration. Resource lock on the identity because it owns the profiles | Wireframe, Maintenance |
| Company names or codes in RLS? | **Codes** | Wireframe |
| Where are screens 2–4 and the admin page? | **All present in the wireframe** — client sign-in, analytics home, report view, ACP portfolio, client and project pages, administration, health | Wireframe |

---

## C. Decisions owed by people

From the wireframe's own "Decisions needed before phase 1", plus what the PDFs add.

| From | Input / approval needed |
| --- | --- |
| **Patrik** (sponsor) | Approve the architecture and **the rule for restricted projects**. Decision at each gate; notified on break-glass |
| **Microsoft partner** | ACP staff viewing without Pro; current F2 price and reservation; embedding trial behaviour |
| **Sejal** | List the existing reports, their workspace and capacity, and the tenant settings. Sanitized `.pbix` template and fake-data file; company-code field and RLS roles; template-version conventions |
| **Data protection officer** | Activity data, client terms, retention |
| **ACP IT** | Test/production tenant access; app identity and profile settings; workspace-creation API authorization; Azure subscription; EU region; deployment approval |
| **ACP management** | MVP scope, data ownership, information barriers, second-person release, budget envelope |

---

## D. Missing input that would sharpen the plan

1. **The *Databook Portal: Architecture Review* (28 Oct)** — cited by both the architecture PDF
   and the wireframe as the full reasoning. Not in this repo. It is where the justification for
   the reversals lives, and the likeliest place to find anything the wireframe asserts without
   showing its work.
2. **The paused ACP requirements draft** — 24 functional requirements and the portal data model,
   mentioned at the end of the 26 Oct brief. A data model that already exists is directly
   reusable in phase 3.
