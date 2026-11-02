# Phase 0 — tenant and subscription setup

Clicks only you can do. Roughly two hours. Nothing here costs money except step 6, which you
should not reach until Phase 4.

Work top to bottom; later steps depend on earlier ones. Tick as you go.

---

## 1. A tenant of your own

- [ ] Create a **Microsoft 365 Developer tenant**, or any tenant you are Global Administrator of.
- [ ] **Do not use Lutron's tenant.** You will be changing tenant-wide Power BI settings and
      inviting external guests. That is not something to do in an employer's directory.
- [ ] Note the **tenant id** (Entra ID → Overview).

## 2. An Azure subscription

- [ ] Create a pay-as-you-go subscription on that tenant.
- [ ] Note the **subscription id**.
- [ ] **Set a budget alert now, before anything else.** Cost Management → Budgets → EUR 50/month,
      alert at 50% and 90%, to your email. The one way this project costs real money is a
      capacity left running, and this is what tells you.

## 3. Power BI Pro trial

- [ ] Sign in at app.powerbi.com with your tenant account and start the **60-day Pro trial**.
- [ ] You need this to see the service and support it. Clients never need any licence.
- [ ] It expires. If you start it now and Phase 4 is six weeks out, that is fine; if the project
      pauses, expect to re-trial or buy one Pro seat.

## 4. App registration

Entra ID → App registrations → New registration.

- [ ] Name: `ACP Databook Portal (dev)`
- [ ] Supported account types: **Accounts in this organizational directory only**
      (guests sign in through this same app; they do not need a multi-tenant registration)
- [ ] Redirect URI: **Single-page application** → `http://localhost:5173`
- [ ] Note the **application (client) id**.

Then:

- [ ] **Certificates & secrets** → new client secret. Note it; it goes in user-secrets, never in
      a file. Local development only — the deployed app uses a managed identity and has no secret.
- [ ] **API permissions** → Microsoft Graph → Delegated: `User.Read`, `User.ReadBasic.All`.
      Application: `User.Invite.All` (for inviting client guests).
- [ ] **API permissions** → Power BI Service → Delegated: `Tenant.Read.All`.
- [ ] Grant admin consent for all of the above.

## 5. Power BI tenant settings

app.powerbi.com → Settings → Admin portal → Tenant settings.

**These are the ones that silently break everything if missed.** Each takes effect within about
15 minutes.

First create the security group:

- [ ] Entra ID → Groups → new security group `Power BI Service Principals`.
- [ ] Add the app registration from step 4 as a **member**.

Then, in Developer settings:

- [ ] **Service principals can use Fabric APIs** → Enabled, *limited to* `Power BI Service Principals`
- [ ] **Service principals can create and use profiles** → Enabled, same group
      *(without this the whole per-client isolation model is unavailable)*
- [ ] **Embed content in apps** → Enabled
- [ ] **Allow service principals to create and use profiles** → confirm it saved; this one is
      easy to miss because it sits apart from the first

In Export and sharing settings:

- [ ] **Publish to web** → **Disabled** for the whole organisation
- [ ] **Export to Excel / .csv / .pbix** → your call for ACP staff, but clients get none of it
      through the portal regardless

In Integration settings:

- [ ] **XMLA endpoint** → **Read Write**
      *(the upload checks run over XMLA; Execute Queries does not support service principals on
      models with row-level security, which is why this is required rather than nice to have)*

## 6. Fabric capacity — **Phase 4 only, not now**

Skip this until you actually reach Phase 4. It bills from the moment it exists.

- [ ] Azure portal → Microsoft Fabric → create capacity
- [ ] Size **F2**, region **Germany West Central**
- [ ] Add yourself as capacity administrator
- [ ] **Pause it immediately after creating it**

Or, once you have the ids from steps 1–2:

```bash
export AZURE_SUBSCRIPTION_ID=...
export FABRIC_RESOURCE_GROUP=...
export FABRIC_CAPACITY_NAME=...
scripts/capacity.sh status
scripts/capacity.sh pause
```

## 7. Two client accounts — Phase 2

- [ ] Create or nominate **two throwaway Microsoft accounts** (`outlook.com` is fine).
- [ ] Do not invite them yet; Phase 2 does that through the portal, which is the point.
- [ ] These are your two fictional clients for the demo.

---

## Done when

This command returns a list of workspaces rather than an error:

```bash
TENANT_ID=...; CLIENT_ID=...; CLIENT_SECRET=...

TOKEN=$(curl -s -X POST \
  "https://login.microsoftonline.com/${TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id=${CLIENT_ID}" \
  -d "client_secret=${CLIENT_SECRET}" \
  -d "scope=https://analysis.windows.net/powerbi/api/.default" \
  -d "grant_type=client_credentials" | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

curl -s -H "Authorization: Bearer ${TOKEN}" \
  https://api.powerbi.com/v1.0/myorg/groups | python3 -m json.tool
```

An empty `value: []` is a pass — it means the service principal authenticated and is allowed to
ask. A 401 means step 5 has not taken effect yet (wait 15 minutes) or the app is not in the
security group.

## Record these

Put them in user-secrets, not in a file in the repo:

```bash
cd src/Acp.Portal.Api
dotnet user-secrets init
dotnet user-secrets set "Entra:TenantId"     "..."
dotnet user-secrets set "Entra:ClientId"     "..."
dotnet user-secrets set "Entra:ClientSecret" "..."
```
