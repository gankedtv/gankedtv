# Legal pages + recorded terms acceptance — design

**Date:** 2026-07-20
**Status:** Approved (implemented in the same PR)

## Problem

GankedTV hosts user-uploaded content but has no Terms of Service or Privacy Policy. The
login screen even claims "By signing in you agree to our Terms of Service" — a document
that does not exist. Without terms, the platform has no contractual basis to remove
content, ban users, or disclaim liability for what users upload. Without a privacy
policy, the platform is plainly non-compliant with the GDPR (accounts, e-mail addresses,
IP-based presence tracking, error monitoring).

As an EU (Netherlands) operator, the hosting-liability safe harbour (DSA, carrying
forward the e-Commerce Directive) expects a notice-and-takedown route and terms that
prohibit illegal content. The in-app report system (`POST /clips/{id}/report` etc.)
already exists; the terms formalise it.

## Goals

- Publish a Terms of Service and a Privacy Policy as first-class pages.
- Make account creation *clickwrap*: an explicit, required "I agree" checkbox, enforced
  server-side, with the acceptance moment recorded on the user row (evidence).
- Cover OAuth signups via *sign-in-wrap*: the login screen's agreement notice becomes
  real links, and OAuth account creation stamps the same acceptance timestamp.

## Non-goals (YAGNI)

- Versioned terms with forced re-acceptance on change. One nullable timestamp is enough
  now; a `terms_version` column can be added when the terms first materially change.
- Localisation (site copy is English; documents are English).
- A separate DMCA agent registration or standalone community-guidelines page — the ToS
  carries an acceptable-use section and a takedown section.

## Design

### Web

- `TermsView.vue` (`/terms`) and `PrivacyView.vue` (`/privacy`): public routes,
  lazy-loaded like every other route. Shared prose layout via a small
  `LegalPage.vue` wrapper component (condensed page title, "last updated" line,
  numbered sections, `max-w` prose column). Arena tokens only; no new CSS.
- `AppFooter.vue`: "Terms" and "Privacy" links added to the bottom colophon bar.
- `RegisterView.vue`: required checkbox — "I agree to the Terms of Service and Privacy
  Policy" (links open in new tab so the half-filled form isn't lost). Payload gains
  `acceptedTerms: true`.
- `LoginView.vue`: footer notice becomes links and now names both documents; it sits
  above the OAuth buttons' screen so OAuth signups are on notice (sign-in-wrap).
- `api/auth.ts`: `RegisterPayload.acceptedTerms: boolean`.

### Server

- `RegisterRequest` gains `bool AcceptedTerms` validated required-true via
  `[AllowedValues(true)]` — the existing `ValidationEndpointFilter` turns that into a
  400 `ValidationProblemDetails` keyed on `AcceptedTerms`. No default value, so API
  clients must send it explicitly.
- `User.TermsAcceptedAt` (`timestamptz`, nullable — existing rows stay null).
  Migration `AddUserTermsAcceptedAt`.
- `CredentialAuthService.TryRegisterAsync` stamps `TermsAcceptedAt = now` on the new
  row (the endpoint gate guarantees consent was given).
- `UserUpsertService.CreateNewUserWithRetryAsync` stamps `TermsAcceptedAt = now` for
  first-time OAuth users (they signed in through a screen that states the agreement).
  Existing users logging in are not stamped retroactively.

### Documents

English, governed by Dutch law, Dutch courts. Operator identity and contact addresses
are bracketed placeholders (`[LEGAL ENTITY]`, e-mail addresses) until the real details
are confirmed. Both pages carry a "last updated" date. Content highlights:

- **ToS:** acceptance/eligibility (16+), account terms, user-content licence
  (non-exclusive, for operating the service — user keeps ownership), acceptable-use list
  mirroring the report reasons (illegal content, IP infringement, harassment, hate,
  NSFW, violence), moderation and enforcement rights, notice-and-takedown (in-app
  reports + e-mail), repeat-infringer termination, as-is disclaimer, liability
  limitation, indemnification, changes, governing law.
- **Privacy:** controller identity, data inventory (account, OAuth profile data,
  content, IP/derived presence + rate-limit keys, error monitoring via self-hosted
  GlitchTip, optional analytics), purposes with GDPR legal bases, retention,
  processors/recipients, GDPR rights + Autoriteit Persoonsgegevens complaint route,
  children (<16), changes, contact.

**These documents are templates written by a non-lawyer; they must be reviewed by
counsel before the site can rely on them.** That caveat lives here and in the PR — not
on the public pages.

## Testing

- Router spec: `/terms` and `/privacy` resolve (keeps the `src/router/**` coverage gate).
- Web `auth.spec.ts`: register payload carries `acceptedTerms`.
- Server: `AcceptedTerms=false` → 400 keyed `AcceptedTerms`; successful register stamps
  `terms_accepted_at`; first-time OAuth upsert stamps it; linked/existing OAuth users
  are untouched.
