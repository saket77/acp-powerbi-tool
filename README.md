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
Consultant (Entra ID SSO)          Client viewer (Entra External ID, email code)
            │                                   │
    React + ASP.NET Core backend-for-frontend, .NET 10 on App Service
    HttpOnly session cookie · checks every grant · asks Power BI for tokens
    managed identity — no secrets anywhere
            │
   ┌────────┼──────────────┬─────────────────┐
Azure SQL          Blob + Functions       Key Vault / App Insights
grants, audit,     .pbix staging,         config, logs, alerts
KPIs, SQL RLS      import + XMLA checks
            │
Power BI Embedded on Fabric F2
  one workspace + service-principal profile per CLIENT
  one report + semantic model per PROJECT
  30-minute token: one report, one model, the user's company codes
```

**Key principle:** the API authorizes; Power BI hosts and renders. Functions do slow background
work, never the user's live report request.

**Five walls** stand between any two clients — server-side grants, SQL row-level security, the
per-client profile, the token's item scope, and RLS inside the model. Each stops the common
attacks alone; together they cover a bug in any one of them.

| | |
| --- | --- |
| Viewers need | **No licence** — app owns data |
| Running cost | **EUR 350–450/month** on F2, flat regardless of viewer count |
| Automation | **13 of 22** lifecycle steps run by themselves |
| Steps for the platform owner per project | **0** — support only |

**Status of the code:** Phases 1–4 built; Gate 1 not yet passed. See
[Running it locally](#running-it-locally) — it runs today with no Azure account, no Microsoft
tenant and no Fabric capacity.

## Docs

| File | What it holds |
| --- | --- |
| [docs/implementation-plan.md](docs/implementation-plan.md) | **The MVP plan.** Decisions, what you need to buy, phases with definitions of done |
| [docs/wireframe.md](docs/wireframe.md) | **Source of truth — the plan is built against this.** 22-step lifecycle, five walls, API surface, costs |
| [docs/decisions-and-deltas.md](docs/decisions-and-deltas.md) | **Read second.** Three artifacts disagree; this is which one wins on each point |
| [docs/architecture.md](docs/architecture.md) | 29 Oct architecture proposal — superseded in detail, sound in structure |
| [docs/product-brief.md](docs/product-brief.md) | 26 Oct origin brief: problem, users, the template contract |
| [docs/open-questions.md](docs/open-questions.md) | What is still open, what is answered, who owes a decision |
| [docs/pitch.md](docs/pitch.md) | The management briefing script |
| [docs/references.md](docs/references.md) | Microsoft docs, starter kits, comparable products |
| [docs/source/](docs/source/) | The wireframe itself, original PDFs, extracted text, rendered diagrams |

Open the wireframe — it is the best single explanation of the product:

```bash
open docs/source/wireframe.html
```

## Running it locally

Currently at **Phase 4** — Power BI. Consultants provision a client, upload a Databook, release
it (somebody else has to), preview it as any of their clients, and clients open their own. Power
BI itself is simulated unless you give it a capacity.

```bash
scripts/dev-up.sh                 # colima + SQL Server + Azurite, waits until SQL is ready
dotnet ef database update -p src/Acp.Portal.Infrastructure -s src/Acp.Portal.Api
dotnet run --project src/Acp.Portal.Api     # http://localhost:5280
npm --prefix web run dev                    # http://localhost:5173
```

Then open <http://localhost:5173> and pick somebody from the account picker.

```bash
dotnet build && dotnet test       # 92 tests; 28 need SQL and skip without it
scripts/dev-down.sh               # --clean also drops the data volumes
```

### Signing in without a tenant

**Nothing in Phases 1–3 needs Azure, a tenant or a Microsoft account.** The portal ships with a
second way to sign in: a roster of fictional people in `appsettings.Development.json` and a
picker that signs you in as any of them.

It is not a bypass. The same `IIdentityResolver` runs, the same classification decides staff
from client, the same session cookie comes out — only the source of the claims differs. That
means inviting a client contact works too: the invitee turns up in the picker instead of getting
an email, so the whole invite → sign in → *client* loop is testable today.

The development sign-in **refuses to start outside the Development environment**, and its
endpoints are not mapped at all in any other mode — they are not routes that return 403, they
are routes that do not exist.

### Switching to real Microsoft sign-in

Work through [scripts/tenant-setup.md](scripts/tenant-setup.md), then:

```bash
cp config/entra.local.example.json config/entra.local.json   # fill in four values
```

That file is git-ignored and read only in development. Setting `Auth.Mode` to `Entra` in it is
the switch. A deployed environment has no such file: it gets these values from app settings and
authenticates to Graph as its managed identity.

### The isolation suite

Thirteen of the tests are the point of Phase 3, and they run against the real SQL Server from
`docker-compose` — a database of their own, built from the real migrations. Row-level security
is a database feature, and an in-memory provider would have agreed with every assertion while
enforcing none of them. Without the containers running they skip with a reason rather than
passing quietly.

They build their world through the API, so the setup is itself a test of the consultant screens,
and then check the wall from both sides: a client asking for another client's project gets 404
over HTTP, *and* a bare `SELECT [Key] FROM Projects` — no `WHERE`, no EF, connected as `sa` —
returns only their own row.

```bash
dotnet test --filter IsolationTests
```

### Power BI without Power BI

`PowerBi:Mode` is `Fake` in development, which simulates provisioning, imports and tokens so the
whole portal runs with no Fabric capacity and nothing billing. Like the development sign-in, it
refuses to start outside the Development environment.

**What that proves and what it does not.** Everything on the portal's side of the line runs
exactly as in production: the grant lookup, the company codes read from the database, the
identity composition and its limits, the audit entry, the 404s. What it cannot touch is the half
inside Power BI — that a token naming one report is refused for another, and that the model's
row-level security really filters to the codes in the identity. That is **Gate 1**, it needs an
F2 capacity, and no fake stands in for it.

So the simulated player does not draw a grey rectangle saying "preview unavailable". It prints
the token's scope — report, model, workspace, role, identity, companies, expiry — because that
is the part being exercised, and a screen that looked like a working report would be the most
expensive kind of wrong.

### How the database decides what you can see

Every connection is stamped with who is asking, and SQL Server filters `Projects` and
`ReportVersions` to the rows that person holds a live grant on. The application does not
filter — it does not have to, and that is the point: a query that forgets a `Where` returns
nothing rather than everything.

The predicate is uniform. A row is visible if and only if you hold a grant on it, with no
exception for ACP staff — consultants see projects because they are on them, which is why
creating a project writes the creator's own Lead grant in the same transaction. A rule with no
exceptions is one nobody can widen later.

Two facts make the stamping safe, both checked against SQL Server 2022 rather than recalled:
a pooled connection handed back out has its session context **cleared**, so one request's
identity cannot leak into the next; and the value is written **read-only**, so nothing inside a
request — injected SQL included — can re-stamp the connection as somebody else.

### How the session works

Sign-in happens on the server, not in the browser. What reaches the browser is an opaque
`HttpOnly`, `SameSite=Strict` cookie carrying five values — user id, kind, security stamp,
object id, name — and no token of any kind, because the portal calls Graph and Power BI as
itself rather than as the person. The front end cannot read it and has nothing to leak.

Every request re-checks the session's security stamp against the database, so revoking
somebody's access ends their session on their next request rather than whenever the cookie
expires. Removing a grant bumps the stamp; adding one deliberately does not, because that only
ever widens what somebody can see and signing a client out at the moment they are given
something to look at would be confusing for no gain.

### Two things about this machine

The .NET SDK was installed to `~/.dotnet` rather than system-wide, so tools need to find it.
Add this to your shell profile:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
```

Containers run on **colima**, not Docker Desktop. `scripts/dev-up.sh` starts it with Rosetta,
which SQL Server needs because there is no ARM build of it. The script also works around a
stale `"credsStore": "desktop"` left in `~/.docker/config.json` by a previous Docker Desktop
install — deleting that one line makes the workaround unnecessary.

## Known rough edges

- **The browser bundle is 428 kB** (102 kB gzipped), most of it `powerbi-client`. It is loaded
  on every screen including the ones with no report on them. A dynamic import in `ReportView`
  would fix that; Phase 6 rebuilds this UI anyway.
- **Phase 4 uploads stream straight to Power BI.** Phase 5 replaces that with private Blob
  staging, a queue and the XMLA template checks. Rows created the Phase 4 way record
  `direct://` in `BlobPath` so they are easy to tell apart.
- **Nothing in the Power BI client has ever run against Power BI.** The shapes most likely to be
  wrong are listed in [docs/implementation-plan.md](docs/implementation-plan.md) under Phase 4.

## Hard constraints

- No real client data and no real Databook outside ACP's tenant.
- EU region only (Germany West Central or West Europe).
- Everything as infrastructure as code (Bicep or `azd`) so ACP IT can rebuild it unchanged.
- Secrets in Key Vault / managed identity. Never in the browser, never in code.
- Code and documents belong to ACP, agreed in writing before production.
