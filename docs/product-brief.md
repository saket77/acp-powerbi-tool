# Product brief — origin document

Source: *Databook Client Portal: Product Brief*, **26 Oct 2026**. This is where the product
intent, the users, the screens and the template contract come from. It is **superseded on
several technical points** by the 29 Oct architecture — see
[decisions-and-deltas.md](decisions-and-deltas.md).

Rendered pages: [users and screens](source/diagrams/brief-users-and-screens.png),
[project flow](source/diagrams/brief-project-flow.png),
[backend components](source/diagrams/brief-backend-components.png).

## The problem, in the author's framing

ACP builds an Interactive Databook for about 200 client projects a year and wants to sell it to
clients as a product, like loyos bi.

- **Sharing is manual today.** Workspaces, permissions and uploads all go through one person.
  "I am one person and cannot do that for 200 projects."
- **Clients need a simple, secure view.** Sign in with MFA, see only their own companies,
  install nothing.
- **Why now:** the template is stable, and the partners are open to a product if a demo shows
  it works.

First goal: **a working demo at EUR 0 that convinces the ACP partners.**

## Users

| User | What they do | What they must never need |
| --- | --- | --- |
| Consultant (ACP staff) | Create a project, add people, drop the `.pbix`, preview, release | Power BI admin knowledge, help from the platform owner |
| Client viewer (external) | Sign in with MFA, view and filter the report | An install, access to other clients' data |
| Platform admin (Sejal) | Watch the admin page, publish new template versions, help when a check can't be solved | Daily hands-on work per project |
| ACP IT | Owns the Azure subscription, Entra settings, production deployment | Manual setup per project |

## Screens

Four pages carry the product — two for consultants, two for clients:

1. Consultant: projects
2. Consultant: project page
3. Client: sign-in
4. Client: report

Plus, noted as needed but not drawn: **an admin page** (all projects, failed uploads, usage) and
an **"Add person" dialog with a company picker**.

Only screen 1 survives in the PDF print — the other three were tabs in an interactive widget and
were not captured. The one we have shows a project table with Project / Client companies /
Status (Live, Draft, Ended) / Ends columns, a "New project" button, and the caption *"New project
creates its own Power BI workspace automatically; ended projects lock by themselves."*

> The workspace-per-project claim in that caption is one of the things the 29 Oct architecture
> reversed — it is now a workspace per **client**.

## How a project runs

Target: **about 5 minutes of consultant time per project.** Four manual steps; everything else
automatic.

| # | Consultant does | Portal does automatically |
| --- | --- | --- |
| 1 | Create project — name, companies, end date | Workspace created; robot is the only member |
| 2 | Add people — companies per client | Invites sent; MFA set at first sign-in |
| 3 | Drop the `.pbix` — refreshed in Desktop | Checks, then draft — template, roles, companies |
| 4 | Preview, release — incl. view as client | Draft goes live; clients get an email |

Then: one-hour viewing pass (their companies only) → report opens filtered, no export → end date
reached, access off automatically.

A failed check returns a plain message naming the fix — *"This file has no Client role. Use
template 1.6."* — and the consultant drops a corrected file. Reminder 14 days before the end
date; workspace deleted 90 days after the end (configurable).

## The template contract

The portal can only automate what the template makes checkable. **Every Databook from version
1.6 on carries four fixed things**, added to the template by the platform owner and checked by
the portal on every upload:

| Item in the `.pbix` | Purpose | Check on upload |
| --- | --- | --- |
| Table `Companies` with column `Ges.` | List of legal entities in this Databook | Names match the companies entered for the project |
| Role `Client` | RLS: shows only the companies passed in the viewing pass | Role exists, otherwise reject |
| Role `ACP` | No filter; used for consultants | Role exists, otherwise reject |
| Measure `Template Version` | Which template the file is built on | 1.6 or newer, otherwise reject |

The `Client` role filter is:

```dax
PATHCONTAINS ( USERPRINCIPALNAME (), Companies[Ges.] )
```

The backend puts the client's company list, separated by a vertical bar, into the `username` of
the viewing pass. **The brief itself flags this as unproven** — length limit and special
characters in company names — and makes it spike #1.

The consultant refreshes data in Power BI Desktop as today. **The portal never refreshes and
never stores data-source passwords.**

## Demo at EUR 0

| Piece | Free option |
| --- | --- |
| Microsoft tenant | Own test tenant from a free Azure account |
| Users | 1 consultant and 2 fake client users |
| MFA | Entra ID security defaults (Authenticator app) |
| Power BI | Pro trial, 60 days — start about a week before the demo |
| Embedding | Development embed tokens with the Pro trial, no capacity; or an F2 switched on for demo day only (~$0.36/hour) |
| Portal hosting | Laptop, or Azure Static Web Apps + Functions free tiers |
| Data | A Databook copy with fake "Muster" data, or a Microsoft sample report |

## Ground rules

- No real client data and no real Databook outside ACP's tenant. Fake-data file only if the
  partners agree.
- Infrastructure as code (Bicep or `azd`), so ACP IT can rebuild it in ACP's tenant unchanged.
- Secrets in Key Vault, never in the browser or in code.
- Code and documents belong to ACP; agreed in writing before production.
- EU region only (Germany West Central or West Europe).

## Build vs buy

Considered and rejected: loyos bi, atSpark EmbedPortal, DataTako, Star Knowledge. The stated
reason to build: **consultants must publish without help, and the upload checks are specific to
the Databook template.**
