<p align="center">
  <img src="src/Storiva.App/Assets/Brand/storiva-256.png" alt="Storiva logo" width="96" height="96">
</p>

<h1 align="center">Storiva</h1>

<p align="center">
  A Windows desktop client for <b>Amazon S3</b> and <b>S3-compatible object storage</b> (MinIO, Ceph, Garage and others).<br>
  Browse buckets, upload and download files and folders, and manage objects in a classic, fast desktop interface.
</p>

<p align="center">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-0f766e">
  <img alt="Platform: Windows 10/11" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-1f6feb">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512bd4">
  <a href="https://github.com/StormLives/Storiva/actions/workflows/build.yml"><img alt="build" src="https://github.com/StormLives/Storiva/actions/workflows/build.yml/badge.svg"></a>
</p>

<p align="center">
  <a href="README.pt-BR.md">Leia em português</a>
</p>

![Storiva main window, light theme: buckets on the left, objects on the right, a text preview in the lower tabs](docs/images/storiva-main-light.png)

<details>
<summary><b>Dark theme</b> (Tools › Options › Interface)</summary>

![Storiva main window, dark theme, showing the properties of a PDF file](docs/images/storiva-main-dark.png)

</details>

> The screenshots use fictitious data served by the project's local test server (`tests/Storiva.FakeS3`). They are regenerated with `tools\capture-readme-screenshots.ps1`.

## Contents

- [Features](#features)
- [Download and install](#download-and-install)
- [Getting started](#getting-started)
- [Privacy](#privacy)
- [Security](#security)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Build from source](#build-from-source)
- [Project structure](#project-structure)
- [Testing status](#testing-status)
- [Code signing policy](#code-signing-policy)
- [Contributing](#contributing)
- [License](#license)

## Features

### Connections

- **Amazon S3** and any **S3-compatible server** with its own endpoint (MinIO, Ceph, Garage, …).
- Several saved connections (**Accounts › Add New Account.. / Manage Accounts..**): add, edit, test, connect, disconnect and remove.
- Authentication:
  - **AWS**: access key, **named AWS profile** (SSO, assume-role) or the AWS SDK default credential chain;
  - **S3-compatible**: endpoint URL (`https://…`, or `http://` only with explicit consent), signing region, path-style addressing and checksum options.
- A real, **read-only connection test** that tells apart: connected, limited permissions, invalid credentials, missing credentials, access denied, network error, TLS error and region/endpoint mismatch.
- Storiva never connects on its own: it always starts disconnected and connects only when you click **Connect**.

![Add New Account dialog for an S3-compatible server](docs/images/storiva-add-account.png)

### Browsing

- The left pane lists **only buckets** by default, so a bucket with thousands of folders does not flood the tree. Folders open in the list on the right. You can show folders in the tree too (Tools › Options › Interface).
- Paginated listings (**Load more**), breadcrumb path, back / forward / up, editable path field.
- Local search, type filter and sorting on every column.
- Bookmarks for buckets and folders.
- Object names with unusual characters are preserved exactly.

### Operations

All operations run in the **Tasks** tab with real progress, cancel and retry:

- upload files and folders (multipart for large files);
- download files, folders and whole buckets (streamed to a partial file, with name-conflict handling);
- create folder, delete, copy, cut/paste (move), rename and change storage class;
- create and delete buckets, add an external bucket by name, edit versioning.

Deletes always ask for confirmation. Deleting a bucket only works when it is empty: Storiva never deletes a bucket recursively.

### Details tabs

| Tab | What it shows |
| --- | --- |
| Permissions | Object/bucket ACL (read-only) |
| Headers | HEAD response and `x-amz-meta-*` metadata (read-only) |
| Tags | Object tags, editable (with confirmation) |
| Properties | Size, dates, storage class, ETag, content type, encryption, version |
| Preview | First 4 KB as text, JSON or hex. Content is never rendered or executed |
| Versions | Versions reported by the server (read-only) |
| Event log | What Storiva did in this session, with task IDs |

### Interface

- **Light, Dark and System** themes. *System* follows the Windows setting live, including the title bar and scroll bars.
- High DPI aware (PerMonitorV2), keyboard shortcuts for every main command, accessible names on controls.
- Icons from [Lucide](https://lucide.dev); original application icon.

![Options › Interface: theme and layout settings](docs/images/storiva-options-interface.png)

## Download and install

Get the latest version from the [Releases](https://github.com/StormLives/Storiva/releases) page (64-bit Windows 10 or 11):

| File | What it is |
| --- | --- |
| `Storiva-vX.Y.Z-win-x64-setup.exe` | **Installer (recommended).** Installs for your user without administrator rights, adds a Start menu shortcut and can be uninstalled from Settings › Apps. Nothing else to install. |
| `Storiva-vX.Y.Z-win-x64-portable.zip` | **Portable.** Extract anywhere (even a USB drive) and run `Storiva.exe`. Nothing else to install. |
| `Storiva-vX.Y.Z-win-x64.zip` | Small build for computers that already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64). |

Storiva does not install services and does not change system settings.

> **Not code-signed yet.** Windows SmartScreen may say "Windows protected your PC" because the publisher is unknown: click **More info › Run anyway**. Release files are built only by the GitHub Actions workflow of this repository from the tagged source (see [Code signing policy](#code-signing-policy)). If you prefer, [build your own Storiva.exe](#build-your-own-storivaexe-buildbat) from the source in one step.

### Uninstall

- **Installer:** Settings › Apps › Installed apps › Storiva › Uninstall.
- **Portable or zip:** delete the folder where you extracted it.
- Your saved connections and settings stay in `%LOCALAPPDATA%\Storiva` in both cases, so a reinstall keeps them. To remove them too, delete that folder (or remove each connection first in **Accounts › Manage Accounts.. › Remove**).

## Getting started

1. Open **Accounts › Add New Account..**
2. Choose **Amazon S3 (AWS)** or **S3-compatible server**.
3. Fill in:
   - **AWS**: region and credentials (a named profile is preferred to long-lived keys);
   - **MinIO / S3-compatible**: endpoint (for example `https://minio.example.internal:9000`), region (`us-east-1` for MinIO) and access key. Under *advanced settings..*, keep *path-style* on.
4. Click **Test connection**, then **Add new account** and **Connect**.
5. To disconnect: **Accounts › Disconnect from "…"**.

Without a connection, the window shows a **Not connected** panel with a link for each saved connection:

![Storiva without a connection: the Not connected panel](docs/images/storiva-not-connected.png)

More detail, including TLS certificates from a private CA: [docs/CONNECTION_SETUP.md](docs/CONNECTION_SETUP.md).

### Where Storiva keeps its data

`%LOCALAPPDATA%\Storiva\`:

| File | Content |
| --- | --- |
| `settings.json` | Theme preference |
| `connections.json` | Saved connections, **without** secrets |
| `secrets\*.bin` | Credentials, encrypted with Windows DPAPI for your Windows user |

## Privacy

Storiva has **no telemetry, no analytics, no crash reporting and no update check**. It does not transfer any information to other networked systems unless you specifically request it:

- it connects only to the storage endpoint of the connection you choose, and only when you click **Connect** or **Test connection**;
- with an AWS named profile or the SDK default credential chain, the AWS SDK may also contact the identity services that profile is configured for (for example AWS SSO or STS), or the local instance metadata service when running on AWS;
- your credentials stay on your computer, encrypted with DPAPI, and are sent only to that endpoint, as AWS Signature Version 4 request signatures computed by the AWS SDK.

## Security

- TLS certificates are **always validated**. There is no option to ignore certificate errors; private CAs are supported by trusting them in Windows.
- Plain `http://` endpoints require an explicit opt-in per connection and show a warning.
- Secrets are stored separately from settings, encrypted with DPAPI, and never written to logs, error messages, JSON files or command lines.
- Destructive actions ask for confirmation; buckets are never deleted recursively.
- S3 requests are made only through the official [AWS SDK for .NET](https://github.com/aws/aws-sdk-net); Storiva has no hand-written request signing.

Details and limits: [docs/CREDENTIAL_SECURITY.md](docs/CREDENTIAL_SECURITY.md). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Keyboard shortcuts

When the same shortcut exists in **Files** and **Buckets**, the focused pane decides: bucket pane → Buckets, object list → Files.

| Action | Shortcut |
| --- | --- |
| Open (folders navigate; files show the Preview tab) | Enter or double-click |
| Download / Upload file(s) / Upload folder(s) | Ctrl+D / Ctrl+U / Ctrl+Shift+U |
| Cut / Copy / Paste | Ctrl+X / Ctrl+C / Ctrl+V |
| Rename / Delete | F2 / Del (in the bucket pane: Delete Bucket) |
| New folder (list) · New bucket (bucket pane) | Ctrl+N |
| Refresh | F5 |
| Permissions / Headers / Tags / Properties tabs | Ctrl+L / Ctrl+H / Ctrl+T / Ctrl+P |
| Select all / Invert selection | Ctrl+A / Ctrl+I |
| Add External Bucket / Edit Versioning | Ctrl+E / Ctrl+Shift+V |
| Options | Alt+O |
| Back / Forward / Up | Alt+← / Alt+→ / Alt+↑ (Backspace in the list goes up) |
| Search / clear · Edit the path · Help | Ctrl+F / Esc · Alt+D · F1 |

## Build your own Storiva.exe (build.bat)

Anyone can build the same portable `Storiva.exe` from the source, without Visual Studio:

1. Install the **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** (8.0.426 or newer, x64).
2. Download the source: **Code › Download ZIP** on GitHub (or `git clone https://github.com/StormLives/Storiva.git`) and extract it.
3. Double-click **`build.bat`** (or run `build.bat test` in a terminal to run the unit tests first).
4. Your executable is in **`dist\Storiva.exe`**: one self-contained file, nothing else to install to run it.

A file you build yourself does not come from the internet, so Windows SmartScreen does not show the unknown-publisher warning for it.

## Build from source

Requirements: Windows 10 or 11 (x64) and the **.NET 8 SDK** 8.0.426 or later in the 8.0 band (see `global.json`).

```powershell
git clone https://github.com/StormLives/Storiva.git
cd Storiva

dotnet restore Storiva.sln
dotnet build Storiva.sln -c Release
dotnet test Storiva.sln -c Release
dotnet run --project src\Storiva.App -c Release

# Folder with Storiva.exe (framework-dependent: needs the .NET 8 Desktop Runtime to run)
dotnet publish src\Storiva.App -c Release -o artifacts\release

# Portable single-file Storiva.exe with the .NET runtime inside (what build.bat does)
dotnet publish src\Storiva.App -c Release -p:PublishProfile=win-x64-portable -p:DebugType=none -o dist
```

The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php) from `installer\Storiva.iss` by the release workflow.

Full validation (clean rebuilds of Debug and Release, all tests, publish, and a UI smoke test of the published exe against a fake S3 server on 127.0.0.1):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate.ps1
```

The log is written to `artifacts\validation-log.txt` and the smoke-test screenshots to `artifacts\smoke-release\`.

## Project structure

```
Storiva.sln
src/
  Storiva.Core/            Models, navigation, commands, details, task queue, connections and the storage
                           client contracts. No UI and no network code.
  Storiva.Infrastructure/  S3/ (AWS SDK client, factory, error mapping), Persistence/ (JSON), Security/ (DPAPI)
  Storiva.App/             Windows Forms UI: main window, dialogs, details tabs, themes, icons
    Assets/                Lucide icons (with LICENSE and SHA-256 list), app icon, third-party license texts
tests/
  Storiva.Tests/           xUnit tests (no external calls)
  Storiva.FakeS3/          Fake S3 server on 127.0.0.1 for tests, the smoke test and the screenshots (not shipped)
tools/                     validate.ps1, capture-readme-screenshots.ps1, capture-window.ps1, generate-brand-icon.ps1
installer/                 Inno Setup script for the Windows installer
build.bat                  One-step build of the portable dist\Storiva.exe
docs/                      Connection setup, credential security, themes, testing, architecture, asset licenses
.github/workflows/         CI: build, test, publish; draft release with installer, portable and zip on version tags
```

To support another storage provider, implement `Storiva.Core.Remote.IObjectStorageClient` and its factory, and register it in `Program.CreateMainForm`.

## Testing status

- **315 unit tests** (313 run, 2 optional MinIO/AWS integration tests skipped unless configured). They run on every push in GitHub Actions and never call external services.
- The real AWS SDK is exercised against the project's **fake S3 server on 127.0.0.1**, including TLS with an untrusted certificate. The fake server checks the access key ID but does not validate signatures.
- A **UI smoke test** drives the real window (layout, menus, dialogs, themes, and connect / list / upload / download / copy / delete / tags / disconnect against the fake server).
- Against a **real MinIO server**, only the read-only connection test has been run so far (result: connected). Write operations have not yet been verified on a real MinIO or on AWS. To run the integration tests on your own test environment, see [docs/TESTING.md](docs/TESTING.md).

## Documentation

The detailed documents are written in Portuguese (pt-BR):

- [docs/CONNECTION_SETUP.md](docs/CONNECTION_SETUP.md): connecting to AWS and MinIO, TLS with a private CA.
- [docs/CREDENTIAL_SECURITY.md](docs/CREDENTIAL_SECURITY.md): how credentials are stored and what DPAPI does and does not protect.
- [docs/THEMES.md](docs/THEMES.md): light/dark/system themes and their limits.
- [docs/TESTING.md](docs/TESTING.md): test suites, smoke test and integration tests.
- [docs/REAL_S3_IMPLEMENTATION.md](docs/REAL_S3_IMPLEMENTATION.md): architecture, design decisions and known risks.
- [docs/ASSET_LICENSES.md](docs/ASSET_LICENSES.md) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): origin and license of every bundled asset and library.

## Code signing policy

<!-- After SignPath Foundation approves the project, add this line here and on the release page:
Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).
-->

Storiva's Windows releases are intended to be signed through the [SignPath Foundation](https://signpath.org) open source program. Until the project is accepted, release binaries are **unsigned**.

- Only binaries built by the [GitHub Actions workflow](.github/workflows/build.yml) of this repository, from a tagged commit, are released or signed. Nothing built on a personal machine is published.
- Every signing request is approved manually by an approver.
- All team members use multi-factor authentication for GitHub (and for SignPath).

Team roles:

| Role | Members |
| --- | --- |
| Committers and reviewers | [@StormLives](https://github.com/StormLives) |
| Approvers | [@StormLives](https://github.com/StormLives) |

Privacy: see [Privacy](#privacy). Storiva does not transfer any information to other networked systems unless specifically requested by the user.

## Contributing

Issues and pull requests are welcome; please read [CONTRIBUTING.md](CONTRIBUTING.md) first.

## License

Storiva is released under the [MIT License](LICENSE).

Third-party components keep their own licenses: [Lucide](https://lucide.dev) icons (ISC; Feather-derived icons also MIT), [AWS SDK for .NET](https://github.com/aws/aws-sdk-net) (Apache-2.0) and `System.Security.Cryptography.ProtectedData` (MIT). Full texts in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); they are also shipped next to `Storiva.exe`.

Amazon S3 and AWS are trademarks of Amazon.com, Inc. or its affiliates; MinIO is a trademark of MinIO, Inc. Storiva is an independent project and is not affiliated with or endorsed by them.
