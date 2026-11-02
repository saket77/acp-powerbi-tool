# Open questions

Three kinds: what the documents say must be **proven**, what must be **decided by people**, and
technical risks **I flagged while reading** that neither document addresses. The last group is
my analysis, not sourced from ACP material — treat it as a review note.

---

## A. Must be proven before build (from the documents)

Gate 1 in the architecture proposal, and the four spikes in the product brief, are the same work
seen twice. Combined:

### A1. RLS through the viewing pass
Pass two or three company names in the effective identity and confirm the `Client` role filters
correctly — including names with spaces, umlauts, and long lists. **This is the single highest
risk in the design** (see C1).

### A2. Upload by the service principal
Import a `.pbix` of realistic size into a new workspace as the client-scoped profile, then read
back the roles and the `Template Version` measure to run the checks.

### A3. Capacity fit
Open the heaviest Databook page — Cash Flow with all levels expanded — and watch memory and
throttling. The brief assumed F2; the architecture refuses to name a SKU before a representative
load test. Fallback F4 or F8.

### A4. Profiles at scale
The brief asked "one robot or many". The architecture **decided**: a profile per client. What is
left to prove is whether the profile model holds at ACP's client count, and what the per-tenant
limits actually are.

### A5. XMLA/TOM validation feasibility
Deep model inspection is *proposed*, not proven — and it interacts with A3, because XMLA
endpoint behaviour depends on the capacity tier.

---

## B. Decisions needed from people (from the documents)

| From | Input / approval needed |
| --- | --- |
| **Sejal** | Sanitized `.pbix` template and fake-data file; real report/page inventory; company-code field and RLS roles; validation measures and template-version conventions |
| **ACP management** | Approve MVP scope, data ownership, information barriers, second-person release, budget envelope |
| **ACP IT** | Test/production tenant access; app identity and profile settings; workspace-creation API authorization; Azure subscription; EU region; sign-in configuration; deployment approval |
| **Microsoft licensing / capacity owner** | Verify embedding trial behaviour, XMLA feature availability, F-SKU fit against the largest model. Choose production capacity only after a representative load test |

---

## C. Review notes — risks I would raise

### C1. How company scope reaches RLS

The design smuggles a pipe-delimited company list into the `username` field of the effective
identity and reads it back with `PATHCONTAINS(USERPRINCIPALNAME(), ...)`. Both documents flag
the length limit, but the deeper issue is that `username` is **not the field designed for this**.

Power BI's effective identity has a `customData` field intended to carry arbitrary context,
readable in DAX as `CUSTOMDATA()`. It is worth evaluating in spike A1 as the primary mechanism,
with `username` as fallback:

- It has its own length limit — **verify the actual limit against ACP's longest client**, a
  group with many legal entities.
- Check that `customData` behaves correctly **in combination with service-principal profiles**,
  since that pairing is the part of the design that changed most recently.
- A delimiter-based list breaks if a company name contains the delimiter. `|` is unlikely in a
  legal entity name but is not impossible; a code list (the `Ges.` codes themselves rather than
  display names) is safer than names.

**Recommendation:** filter on stable `Ges.` codes, not display names, whichever field carries them.

### C2. Token revocation is not immediate

The architecture states it plainly: revoking access blocks *new* embed tokens, but already-issued
tokens stay valid until expiry. With a 60-minute lifetime, a revoked client keeps access for up
to an hour. Decide whether that is acceptable for financial data, or shorten the lifetime and
refresh more often. This should be a written, approved position, not an accident.

### C3. Capacity and workspace ceilings

Worth confirming before committing to the per-client workspace model:
- Maximum workspaces, semantic models and reports per capacity / per tenant.
- Maximum service-principal profiles per service principal, and any throttling on profile
  creation.
- Whether F2 memory holds one Databook model, let alone several resident at once.

200 projects a year accumulate. The model must answer "what does year three look like."

### C4. Direct-to-Blob upload needs CORS

The browser uploading straight to Blob Storage with a SAS requires CORS configured on the storage
account. Easy, but it is infrastructure that must be in the Bicep, not clicked in the portal.

### C5. Entra External ID is a separate product with its own setup

Moving clients out of ACP's tenant into a dedicated External ID tenant is the right call, but it
is not a config toggle — separate tenant, separate app registration, its own invitation flow,
MFA policy and branding. Budget real time for it, and confirm ACP IT will own a second tenant.

### C6. `.pbix` size drives the transfer method

The architecture notes Microsoft has a separate temporary-upload-location API for large files.
**Measure the real Databook file sizes before choosing**, because it changes the Function's
import path. This is a one-hour task that de-risks step 3.

### C7. The three missing screens

Screens 2–4 (consultant project page, client sign-in, client report) and the admin page exist
only in the original interactive doc. They are needed before UI work in Gate 3 — not before
Gates 1 and 2, which are backend proofs.
