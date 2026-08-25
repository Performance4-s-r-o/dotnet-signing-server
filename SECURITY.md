# Security Policy

## Reporting a vulnerability

**Please do not open a public issue, pull request or discussion for a security
problem.** Public disclosure puts every deployment of this software at risk
before a fix exists.

Report it privately in one of these ways:

1. **GitHub private advisory** — the preferred route. Go to the
   [Security tab](https://github.com/Performance4-s-r-o/dotnet-signing-server/security/advisories/new)
   and open a draft advisory. It stays private until we publish it.
2. **Email** — <support@performance4.cz> with `SECURITY` in the subject.

Please include:

- what the issue is and why it matters,
- the affected file, endpoint or version,
- steps to reproduce, ideally a minimal request or PDF,
- what an attacker gains.

## What to expect

- We aim to acknowledge a report within **5 working days**.
- We will tell you whether we consider it in scope and what we intend to do.
- We will keep you updated while a fix is prepared, and credit you in the
  advisory unless you would rather stay anonymous.

## Scope

In scope: this repository — the signing engine, the HTTP API, authentication
and token handling, billing logic, and the web portal.

Out of scope:

- vulnerabilities in third-party dependencies that are already public — report
  those upstream, though do tell us if this project uses them in an unsafe way,
- findings that require an attacker to already hold valid credentials or
  administrative access, unless they cross a tenant or privilege boundary,
- missing hardening headers or best-practice suggestions with no demonstrable
  impact,
- denial of service through sheer request volume.

## Deployments

This software is AGPL-3.0 and anyone may run their own instance. A report about
a *deployment you do not operate* should go to whoever runs it. Report issues in
the *code* here.
