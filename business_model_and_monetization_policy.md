# Knovara — Business Model & Monetization Policy

| | |
|---|---|
| **Version** | 0.1 (draft) |
| **Date** | 2026-11-25 |
| **Status** | Decisions recorded; policies marked *Proposed* await confirmation |
| **Related** | `business_architecture.md` (summary), `system_architecture.md` (ADR-001), `README.md` ("SaaS control plane") |

**How to read this document.** Every statement carries one of three tags.

- **[Decided]** — chosen by the project owner. Changing it requires an entry in the decision log (section 9).
- **[Proposed]** — recommended here, not yet confirmed. Do not publish or promise it externally until it is confirmed.
- **[Open]** — not decided; section 10 says what it blocks.

No prices, service-level numbers, legal terms or revenue figures appear in this document, because none has been decided and there is no customer data to base them on.

---

## 1. Summary

Knovara is context infrastructure for AI agents: it understands documents once, keeps them as searchable, cited knowledge, and hands an agent the smallest sufficient evidence for a task. It never writes answers; the agent brings its own model.

**[Decided]** The business is **open core**:

- The engine, the clients and the local runtime are free and open source under **Apache-2.0**.
- One customer's authority domain (accounts, roles, enrollment, device tokens, installer hosting) is open too.
- Revenue comes from what costs us operations or what enterprises require: **hosting, governance modules (SSO/SCIM, audit, policy, federation), the multi-customer operator layer, managed compute, and support.**
- The first paying customer is **engineering teams and small companies** already using AI coding assistants.

| Tier | For | Paid? |
|---|---|---|
| Community | Individuals, and teams that self-host | Free, open source |
| Team (hosted) | Teams that want us to run it | Subscription |
| Enterprise | Organizations needing governance | Annual contract |
| Managed compute | Anyone needing heavy processing run for them | Usage |

---

## 2. Who it is for and what value we sell

- **[Decided]** First paying customer: engineering teams and small companies. Adoption starts bottom-up with individual developers using the free tier.
- **[Decided]** First consumers are AI agents, reached through MCP, the CLI and a local API (ADR-001). The desktop app is a management interface, not the foundation.
- **[Decided]** Value proposition: read once, understand once, reuse many times; retrieve progressively; deliver the smallest sufficient, cited context; keep customer data under customer control; work offline.

### 2.1 The claim that pricing depends on

The central promise is **fewer tokens per completed task** than an agent reading raw sources or using standard retrieval. Our benchmarks currently measure retrieval quality (recall, MRR, latency), **not** end-to-end token cost.

- **[Decided]** This is the first business milestone, ahead of any pricing.
- **[Proposed]** Method: run the same set of real tasks three ways (agent reads raw sources; standard RAG; Knovara), record input tokens, LLM calls, latency and answer/citation accuracy, publish the method with the numbers.
- **[Proposed]** Until it is measured, we make no quantified savings claims in sales or marketing material.

---

## 3. Product structure and licensing

### 3.1 Open and proprietary components — **[Decided]**

The rule: **open what a customer must control to stay independent; charge for what costs us operations or what enterprises require.**

| Component | Licence |
|---|---|
| Engine, document workers, MCP server, CLI, desktop app, local runtime and server mode | Open (Apache-2.0) |
| Token verification, device client library, enrollment and token API specification | Open |
| One customer's authority domain: accounts, roles, tenants and device limits, enrollment codes, runtime registrations, token issuing, installer hosting, basic dashboard | Open |
| SSO / SCIM, audit retention and export, policy engine (classification, residency, retention), federation manager | Proprietary (Enterprise) |
| Multi-customer operator layer: provisioning, usage metering, billing, managed operations | Proprietary |
| Hosting, support, managed compute | Paid services |

Why: the open part is the plumbing a customer must be able to run, inspect and replace, which is what "customer authority independent of Knovara's commercial infrastructure" (target architecture) requires. The proprietary part is exactly what a competitor would have to rebuild to offer a rival hosted service.

### 3.2 Licence and contributions

- **[Decided]** The open-source licence is **Apache-2.0**.
- **[Decided]** Contributions use **DCO sign-off**. There is no CLA, so the open code cannot be relicensed later without contributors' agreement.
- **[Decided]** `LICENSE` (Apache-2.0), `NOTICE` and `CONTRIBUTING.md` (with the DCO text) were added to the repository on 2026-11-25. The copyright line reads "The Knovara Authors", a neutral default to replace with the legal owner's name if that differs. Everything in the repository is open, so no split and no history rewrite were required.

### 3.3 Proprietary modules

- **[Decided]** They live in a separate private repository and are written by us.
- **[Proposed]** They attach to the open control plane through extension points: an audit sink, an identity provider (SSO/SCIM), a policy evaluator and an operator/provisioning interface. These do not exist yet; designing them is the first control-plane work.
- **[Proposed]** Rule: no proprietary module may be required for any capability listed as open in 3.1.

### 3.4 Name and trademark

- **[Open]** Apache-2.0 grants no rights to the Knovara name or logo, which is what a hosting competitor cannot copy. A trademark policy (forks may not imply endorsement) and registration are to be decided.

### 3.5 Third-party services

- **[Decided]** The product must never depend on a paid third-party service: the runtime and its models are self-hosted and open.
- **[Decided]** Ordinary business tooling (payment processing, hosting, domain, email) is allowed to run the company and the managed offering.

---

## 4. Offering

All tiers are **[Decided]** in shape and **[Open]** in price.

### 4.1 Community — free, open source

- **For:** individuals, and any team willing to run its own server.
- **Includes:** local runtime, CLI, MCP server, desktop app, working with no account and no network; plus the open server mode and authority domain a team can self-host.
- **Does not include:** a hosted service, SSO/SCIM, audit retention, policy engine, federation manager, support commitments.
- **Charged by:** nothing.

### 4.2 Team (hosted)

- **For:** engineering teams that do not want to operate infrastructure.
- **Includes:** we host and operate your team server and authority domain: backups, upgrades, monitoring, support. The same software self-hosted stays free.
- **Charged by:** per seat or per registered runtime. **[Open]** which unit.

### 4.3 Enterprise

- **For:** organizations with identity, audit and compliance requirements.
- **Includes:** the proprietary governance modules (SSO/SCIM, audit retention and export, policy engine, federation manager), run by the customer or hosted by us, with support and an SLA.
- **Charged by:** annual contract.

### 4.4 Managed compute

- **For:** anyone needing heavy processing without the hardware: OCR (measured at about 30 seconds per page on CPU), bulk embedding.
- **Includes:** our workers, used by hosted or self-run installations.
- **Charged by:** usage, priced from our real compute cost plus margin.

### 4.5 Pricing process

- **[Open]** Prices are not set. **[Proposed]** order of work: (1) measure the token-savings claim (2.1); (2) run paid design-partner pilots with a few teams; (3) set Team pricing from the value those teams report and from comparable developer tools; (4) price Enterprise by quote.

---

## 5. Monetization policy

### 5.1 What we charge for — **[Decided]**

Operations we perform, governance that enterprises require, and the support that comes with both. We do not charge for the ability to run Knovara on your own machine or your own server.

### 5.2 Community commitments — **[Proposed]**

Confirm these before publishing them; they are promises customers will rely on.

1. The local runtime stays free, works offline and needs no account. It has no usage caps, no expiry, and no required phone-home.
2. **No feature moves from open to paid.** New capabilities may be paid; a paid capability may later be opened. This protects the trust that the free tier builds.
3. **Portability.** A customer can export its documents, passages and metadata in documented formats at any time, including from the hosted service.
4. **Same software.** The hosted service runs the open code; the only hosted-only parts are the operator layer and the governance modules.

### 5.3 What we will not do — **[Proposed]**

- Sell or share customer data, or use customer content to train models.
- Resell third-party model tokens or generated answers (Knovara supplies evidence only).
- Show advertising.
- Degrade the free tier to push upgrades.
- Hold data hostage when payment lapses: there is a grace period and an export window (periods to be set).

### 5.4 Pricing principles — **[Proposed]**

- **One clear unit per tier:** seat or registered runtime (Team), contract (Enterprise), cost-plus usage (Managed compute). **No per-question or per-token charging.**
- **Published prices** for Community and Team; Enterprise by quote.
- **No surprise charges.** Compute jobs show their expected cost, or ask for confirmation, before they run (the design already includes admission control before heavy jobs).
- **Notice before changes.** Existing subscribers get advance notice of price changes (period to be set).
- **Evaluation.** Design partners and trials are explicitly time-boxed and priced as pilots, not free forever.

### 5.5 Metering and telemetry

- **[Decided]** The local runtime cannot be metered without telemetry, so Community is not metered.
- **[Proposed]** Community has **no telemetry by default**; an opt-in, anonymous option may be offered. The exact data collected must be published before it ships.
- **[Proposed]** The hosted service measures only what billing and operations need: seats or runtimes, compute minutes, storage. Customers can see their own usage.
- **[Open]** How a self-run Enterprise installation proves its licence while staying offline-capable.

### 5.6 Enforcing limits — **[Proposed]**

- Seat and device limits are enforced where they already exist: at enrollment in the control plane. Revoking a device frees its slot.
- Exceeding a limit blocks **new** enrollments, never an installation that already holds data. Read and export always keep working.
- A short, visible grace period precedes any restriction.

### 5.7 Data ownership and privacy in paid services — **[Proposed]**

- The customer owns its data; Knovara processes it only to provide the service.
- Every read is scoped to the customer's tenant; Enterprise customers can ask for physical isolation (dedicated database or storage), not only logical separation.
- Deletion on request and at termination, after an export window (period to be set).
- **[Open]** Published list of hosting providers and regions.

### 5.8 Support and service levels — **[Open]**

| Tier | Intended support |
|---|---|
| Community | Public issue tracker, best effort |
| Team | Email support; response targets to be set |
| Enterprise | Contractual SLA; terms to be set |

No numeric targets are promised until we can staff them.

### 5.9 Billing, tax and contracts — **[Open]**

Payment processing through an ordinary processor is allowed (3.5). Invoicing, tax handling, refunds and contract terms need legal and accounting input and are not defined here.

---

## 6. Revenue path — **[Proposed]**

Each stage has a gate that must be true before the next begins.

| Stage | What | Gate to start |
|---|---|---|
| 0 | Prove the token-savings claim | None |
| 1 | Paid pilots with 3-5 design-partner teams (support and setup) | Stage 0 shows a real saving |
| 2 | Hosted Team beta | Stage 1 teams use it weekly and would pay for hosting |
| 3 | Enterprise modules (SSO/SCIM, then audit) | A named customer requires them |
| 4 | Managed compute | Customers hit processing limits they cannot meet locally |

Out of scope for now: a marketplace, commercial federation, a web workspace.

---

## 7. Cost drivers (qualitative)

| Driver | Who bears it | Note |
|---|---|---|
| Running the local runtime | The user's machine | Our marginal cost per free user is near zero |
| Hosted team servers | Us, recovered by the Team subscription | Storage grows with passages (about 1.5 KB of vector per passage, measured) |
| OCR and bulk embedding | Us, recovered by usage pricing | The expensive step, hence cost-plus pricing |
| Support | Us | Likely the largest cost for a small team; the reason Community support is best effort |
| Engineering of proprietary modules | Us | Funded by Enterprise revenue |

Unit-economics figures are **[Open]** until pilots give real usage.

---

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| The token-savings claim does not hold end to end | Make it the first milestone; no pricing before it is measured |
| Coding-assistant vendors ship their own context or memory features | Stay agent-independent and local-first; compete on provenance, progressive retrieval and ownership |
| Teams are content with the free self-hosted tier | Charge for what they would rather not do: operating it, SSO, audit, support |
| A large cloud rehosts the Apache-2.0 code | Proprietary governance and operator layers, the trademark, and being the best-run hosting |
| Open-core trust damage if features move to paid | The "no open feature moves to paid" commitment (5.2) |
| Enterprise sales are slow and need compliance work | Do not start them before a named customer requires them |
| Solo-operator support load | Keep Community best effort; limit pilots; price support |

---

## 9. Decision log

| Date | Decision | Notes |
|---|---|---|
| 2026-11-25 | Front door is AI agents via MCP, CLI and a local API | ADR-001 |
| 2026-11-25 | Engine runs locally by default; team and managed forms are opt-in | ADR-001 |
| 2026-11-25 | Evidence only; no answer generation | ADR-001 |
| 2026-11-25 | Control plane becomes one customer's authority domain | ADR-001 |
| 2026-11-25 | First paying customer: engineering teams and small companies | |
| 2026-11-25 | Revenue model: open core | |
| 2026-11-25 | Open-source licence: Apache-2.0 | |
| 2026-11-25 | `LICENSE`, `NOTICE`, `CONTRIBUTING.md` added | Copyright holder is "The Knovara Authors" by default |
| 2026-11-25 | "No paid third-party services" applies to the product, not to business tooling | |
| 2026-11-25 | Control plane boundary: open authority, paid governance | Section 3.1 |
| 2026-11-25 | Contributor terms: DCO sign-off, no CLA | |

---

## 10. Open items

| Item | Blocks | Notes |
|---|---|---|
| Confirm the Community commitments (5.2) and "will not do" list (5.3) | Publishing any policy | Promises customers will rely on |
| Confirm the copyright holder named in `NOTICE` ("The Knovara Authors") | Registering the trademark, any sale of the code | Replace with the legal owner if different |
| Ship third-party licence notices with the desktop installer | Distributing the installer to others | The installer bundles the .NET runtime and WPF libraries (MIT), whose notices must travel with them |
| Automated DCO check on pull requests | Accepting outside contributions at volume | Sign-off is checked by hand until then |
| Token-savings benchmark | All pricing and savings claims | First milestone |
| Seat or registered runtime as the Team unit | Team price list | Decide with pilot input |
| Trademark policy and registration | Public launch | |
| Telemetry policy | Any usage reporting | |
| Pricing for Team, Enterprise and compute | Selling | After stages 0-1 |
| Support and SLA terms | Team and Enterprise contracts | |
| Offline licence verification for self-run Enterprise | Enterprise self-hosted sales | |
| Billing, tax, refunds, contract terms | First invoice | Needs legal and accounting input |
| Hosting providers and regions | Hosted service | |
| Extension-point design for proprietary modules | Any Enterprise module | First control-plane work |

---

## 11. Amending this document

- Change a **[Decided]** statement only by adding a row to the decision log with the date and reason.
- Promote a **[Proposed]** statement to **[Decided]** by recording who confirmed it and when.
- Review the whole document when the first design partner signs, and again before the first public price is published.
