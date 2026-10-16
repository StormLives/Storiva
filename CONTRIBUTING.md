# Contributing to Storiva

Thanks for your interest! Bug reports, ideas and pull requests are welcome.

## Before you start

- Open an issue first for larger changes, so the approach can be agreed on.
- Never paste real credentials, bucket names or endpoints from private systems in issues, logs or tests.

## Building and testing

You need Windows 10/11 and the .NET 8 SDK (see [README.md](README.md#build-from-source)).

```powershell
dotnet build Storiva.sln -c Debug
dotnet test Storiva.sln -c Debug
```

Full local validation (clean rebuilds, tests, publish and the UI smoke test against a fake S3 server on 127.0.0.1):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate.ps1
```

## Rules for changes

- Warnings are errors; keep the build clean.
- Unit tests must not call external services. Use the in-memory fakes or the loopback fake S3 server in `tests/Storiva.FakeS3`.
- Use the official AWS SDK for anything that talks to S3. Do not add custom request signing, certificate bypasses or options that disable TLS validation.
- Secrets never go to settings files, logs, error messages or command lines.
- Destructive actions must ask for confirmation.
- New third-party code or assets need an OSI-approved license compatible with MIT; record them in `THIRD_PARTY_NOTICES.md` and `docs/ASSET_LICENSES.md`.

## Reviews

Every pull request is reviewed by a maintainer before it is merged, and releases are built only by the GitHub Actions workflow in this repository (see "Code signing policy" in the README).

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
