# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report them privately through GitHub: **Security → Report a vulnerability** on this repository (GitHub private vulnerability reporting). Include the Storiva version (Help › About), the steps to reproduce and what you expected. Never include real access keys, secret keys or session tokens: use test credentials or redact them.

You should get a first answer within 7 days. Fixes are released as a new version, and the advisory is published once users can update.

## Supported versions

Only the latest release receives security fixes.

## Design notes

How Storiva protects credentials (Windows DPAPI, no secrets in settings or logs, TLS always validated) is described in [docs/CREDENTIAL_SECURITY.md](docs/CREDENTIAL_SECURITY.md).
