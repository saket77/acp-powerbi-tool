# Management briefing script

The spoken version of the proposal, as supplied. Useful as the plain-language test: if the
implementation plan cannot be explained in these terms, it has drifted.

This script is **consistent with the 29 Oct architecture** (ASP.NET Core backend, per-client
workspace *and* service principal profile, Blob staging, Functions for processing). It does not
mention the separate Entra External ID tenant, Queue Storage, or that release needs a second
approver — those are detail below the altitude of the pitch, not disagreements.

---

## The objective

> Today, our consultants create Interactive Databooks in Power BI, but distributing them
> securely to clients requires manual involvement from the BI team.
>
> With approximately 200 client projects a year, that approach won't scale.
>
> What we're proposing is an **ACP-branded Databook Portal** where consultants can independently
> publish their completed reports and clients can securely access them.

## How it would work

> The consultant would continue preparing the Databook in Power BI Desktop using our existing
> standard template.
>
> Once it's ready, they would:
>
> 1. Log into the ACP portal.
> 2. Create or select a client and project.
> 3. Assign the project team and client users.
> 4. Upload the Power BI file.
> 5. Preview the report and submit it for approval.
>
> The platform would automatically create the necessary Power BI resources, validate the
> uploaded report, and publish the approved version.
>
> Clients would log in securely with two-factor authentication and see only the projects,
> companies, and reports they're authorized to access.

## The technical architecture

> We're proposing to use Microsoft Azure and Power BI Embedded.
>
> The frontend would be built in React, with an ASP.NET Core backend managing authentication,
> project permissions, and access to reports.
>
> Azure Blob Storage would temporarily hold uploaded Power BI files. Azure Functions would
> automatically process imports, validate templates, and handle notifications and scheduled
> cleanup.
>
> Azure SQL would store project details, user access rights, report versions, and audit logs.
>
> Power BI Embedded would display the reports directly inside the ACP website, without requiring
> external clients to have individual Power BI licences under the app-owns-data model.
>
> For security, each client would have its own Power BI workspace and service principal profile.
> Row-level security would further control which companies inside a Databook each user can see.

## The business benefit

> The main benefit is removing manual administration from the publishing process.
>
> Consultants would manage their own projects, clients would receive a professional self-service
> reporting experience, and the BI team would focus on maintaining the template, monitoring the
> platform, and handling exceptions.
>
> We already have a clickable wireframe showing the proposed consultant and client experiences,
> along with an architecture proposal showing how the Microsoft services fit together.
>
> The next step would be a working MVP using fictional client data, proving automatic upload,
> validation, secure embedding, and client-specific access before we commit to production.

## The approval we're seeking

> We'd like approval to validate this approach in a separate test environment, confirm the
> required Microsoft permissions and capacity, and develop a working demonstration for the
> partners.
