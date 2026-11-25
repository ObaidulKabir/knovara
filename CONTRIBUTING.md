# Contributing to Knovara

Thank you for helping. This repository is the **open core** of Knovara: the retrieval engine, document workers, MCP server, CLI,
desktop app, local runtime, and one customer's authority domain (`src/Knovara.ControlPlane`). It is licensed under the
[Apache License 2.0](LICENSE). What is open and what is not is described in
[`business_model_and_monetization_policy.md`](business_model_and_monetization_policy.md) (section 3.1).

Enterprise governance modules and the hosting/operator layer are proprietary, are developed elsewhere, and are **not accepted here**.
A change that would make an open capability depend on a proprietary one will not be merged.

## Sign off your commits (DCO)

Contributions are accepted under the [Developer Certificate of Origin](https://developercertificate.org/), not a CLA. By signing off,
you certify that you have the right to submit the work under the project's licence. Add a sign-off to every commit:

```powershell
git commit -s -m "Describe the change"
```

That appends a `Signed-off-by: Your Name <you@example.com>` line, which must use your real name and an address you control.
To add it to commits you already made: `git commit --amend -s --no-edit` (last commit) or `git rebase --signoff main` (a branch).

The full text you are agreeing to:

```
Developer Certificate of Origin
Version 1.1

Copyright (C) 2004, 2006 The Linux Foundation and its contributors.

Everyone is permitted to copy and distribute verbatim copies of this
license document, but changing it is not allowed.


Developer's Certificate of Origin 1.1

By making a contribution to this project, I certify that:

(a) The contribution was created in whole or in part by me and I
    have the right to submit it under the open source license
    indicated in the file; or

(b) The contribution is based upon previous work that, to the best
    of my knowledge, is covered under an appropriate open source
    license and I have the right under that license to submit that
    work with modifications, whether created in whole or in part
    by me, under the same open source license (unless I am
    permitted to submit under a different license), as indicated
    in the file; or

(c) The contribution was provided directly to me by some other
    person who certified (a), (b) or (c) and I have not modified
    it.

(d) I understand and agree that this project and the contribution
    are public and that a record of the contribution (including all
    personal information I submit with it, including my sign-off) is
    maintained indefinitely and may be redistributed consistent with
    this project or the open source license(s) involved.
```

Maintainers check the sign-off during review; there is no automated check yet.

## Getting set up

You need the .NET 10 SDK, Docker (for Postgres, RabbitMQ, the model service and the document worker), and Python only if you work on the
worker or model service. [`README.md`](README.md) ("Run locally") has the full steps.

```powershell
dotnet build
dotnet test src/Knovara.Api.Tests
dotnet test src/Knovara.Common.Tests
dotnet test src/Knovara.ControlPlane.Tests
```

Some control-plane store tests run against a real Postgres and are skipped unless `KNOVARA_TEST_POSTGRES` holds a connection string, for example
`Host=localhost;Port=5442;Database=knovara;Username=knovara;Password=...`. They create and remove their own tenants.

On Windows a running API, control plane or desktop app locks its own DLLs, so stop it before building.

## Making a change

- **Keep a change focused** and say *why* in the commit message, not only what. The history is written that way; please match it.
- **Add or update tests** for behaviour you change. Prefer tests that fail without your change.
- **Update the docs** the change touches: `README.md`, `system_architecture.md`, and the diagrams (`scratch_*.html`) when a flow or a component changes.
- **Never commit secrets**, signing keys, passwords or tokens, including in tests or sample config.
- **No paid third-party services in the product.** The runtime and its models must work with self-hosted, open components only.
- **Do not trust a tenant id sent by a caller.** It comes from the authenticated credential, and every read is filtered by it.

## Reporting problems

Open an issue with what you did, what you expected and what happened, plus your OS and .NET version. A private channel for
security reports has not been set up yet; until it is, please do not post exploit details in a public issue.

## Names

The Apache licence does not grant rights to the Knovara name or logo. A fork must not present itself as the official project.
