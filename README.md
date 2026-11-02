# ACP Databook Client Portal

An ACP-branded portal where consultants publish a finished Interactive Databook (`.pbix`)
themselves and external clients view it after a secure sign-in with MFA — built on Power BI
Embedded under the **app-owns-data** model.

**Status: proposed architecture, not approved.** No code yet. The decision being sought is
approval to build a partner-facing MVP on fictional data in a controlled Microsoft test
environment, plus technical discovery with ACP IT before production.

## Why

ACP builds an Interactive Databook (a Power BI report on one ACP template) for roughly **200
client projects a year**. Today every workspace, permission and upload goes through one person
(Sejal, the data-engineering & platform owner). That does not scale, and it is the bottleneck
the portal exists to remove. Consultants self-serve; the platform owner moves to template,
validation and exceptions.

## Shape of the system

```
Consultant / Client viewer
        │
React portal + ASP.NET Core API        ← authorizes access, issues embed tokens
        │
   ┌────┼─────────────────┬──────────────────────┐
Azure SQL      Blob + Queue + Functions      Entra ID (staff)
grants,        private .pbix staging,        Entra External ID (clients, MFA)
report IDs     import + validation
        │
Power BI Service on a Fabric capacity
  one workspace + service-principal profile per CLIENT
  one report + semantic model per PROJECT
  RLS restricts legal entities within a project
```

**Key principle:** the API authorizes; Power BI hosts and renders. Functions do slow background
work, never the user's live report request.

## Docs

| File | What it holds |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | **Authoritative design** (29 Oct 2026 proposal) |
| [docs/product-brief.md](docs/product-brief.md) | Origin brief (26 Oct 2026): problem, users, screens, template contract |
| [docs/decisions-and-deltas.md](docs/decisions-and-deltas.md) | What the architecture review changed, and why it matters |
| [docs/open-questions.md](docs/open-questions.md) | What must be proven before build; unresolved choices |
| [docs/pitch.md](docs/pitch.md) | The management briefing script |
| [docs/references.md](docs/references.md) | Microsoft docs, starter kits, comparable products |
| [docs/source/](docs/source/) | Original PDFs, extracted text, rendered diagrams |

## Hard constraints

- No real client data and no real Databook outside ACP's tenant.
- EU region only (Germany West Central or West Europe).
- Everything as infrastructure as code (Bicep or `azd`) so ACP IT can rebuild it unchanged.
- Secrets in Key Vault / managed identity. Never in the browser, never in code.
- Code and documents belong to ACP, agreed in writing before production.
