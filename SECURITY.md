# Security Policy

## Supported Versions

Telegrab is in active development. Security fixes are applied to the latest version on the
default branch. There is no long-term support for older builds at this time.

| Version | Supported |
|---------|-----------|
| latest (default branch) | ✅ |
| older builds | ❌ |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, report them privately using GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
("Report a vulnerability" under the repository's **Security** tab), or contact the maintainer
directly.

When reporting, please include:

- A description of the vulnerability and its potential impact.
- Steps to reproduce, or a proof of concept.
- The affected version or commit.
- Any suggested mitigation, if known.

You can expect an initial acknowledgment within a reasonable timeframe. We will keep you informed
of progress toward a fix and may ask for additional details. Please give us a reasonable
opportunity to address the issue before any public disclosure.

## Scope and Handling of Sensitive Data

Telegrab handles sensitive material; keep the following in mind when reporting or contributing:

- **Credentials and sessions:** `api_id` / `api_hash`, the encrypted `session.dat`, and
  `appsettings.json` must never be committed or shared. Do not include real credentials in bug
  reports.
- **Downloaded content:** A populated `telegrab.db` and downloaded media may contain private
  conversations and personal data. Never attach these to a report; redact any screenshots.
- **Read-only design:** Telegrab only reads from Telegram. Reports describing the app sending or
  modifying data in a Telegram account are especially important.

## Out of Scope

- Vulnerabilities in third-party dependencies should be reported to their respective projects
  (see [`NOTICE`](./NOTICE)). We will, however, update dependencies once fixes are available.
- Issues requiring physical access to an already-unlocked machine where the app data folder is
  readable are generally considered out of scope.

Thank you for helping keep Telegrab and its users safe.
