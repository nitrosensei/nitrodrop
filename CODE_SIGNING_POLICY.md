# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

This policy applies to the NitroDrop releases published on this repository's
[Releases](https://github.com/z3x0fly/nitrodrop/releases) page.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [Emre Can Temur](https://github.com/z3x0fly) (Nitrobyte) |
| Approvers | [Emre Can Temur](https://github.com/z3x0fly) (Nitrobyte) |

- **Committers** may change the source code directly.
- **Reviewers**: every change proposed by someone who is not a committer (for example a pull request from a fork) is
  reviewed by a committer before it is merged.
- **Approvers** approve each signing request in SignPath before anything is signed.
- All team members use multi-factor authentication for GitHub and SignPath.

## How releases are built and signed

1. A maintainer pushes a version tag (`v*`). GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml))
   builds the release on a GitHub-hosted Windows runner from that tag, runs the tests and uploads the **unsigned**
   artifacts with `actions/upload-artifact`.
2. The workflow submits a signing request to SignPath (`signpath/github-action-submit-signing-request`). SignPath verifies
   that the artifact comes from this repository's GitHub workflow (origin verification).
3. An approver checks the request (tag, commit, build log) and approves it in SignPath. Only then is it signed.
4. The signed files are attached to a draft GitHub release, checked and published.

Nothing is signed from a developer machine. Local builds use a self-signed development certificate (`CN=Nitrobyte`,
`packaging/sign-dev.ps1`) that is not trusted outside the developer's PC.

## What is signed

Only binaries built from this repository's source code:

- `NitroDrop.exe`, `NitroDrop.dll` — the app
- `NitroDrop.Radio.exe`, `NitroDrop.Radio.dll` — the elevated Wi-Fi radio helper
- `NitroDrop.SyncHost.exe`, `NitroDrop.SyncHost.dll` — the browser extension's native messaging host
- `nitro-phoneline-probe.exe` / `.dll` — read-only PhoneLine diagnostics
- our own libraries: `AirDrop.Core.dll`, `Nitro.Awdl.dll`, `Nitro.Sync.dll`, `Nitro.Continuity.dll`,
  `Nitro.Continuity.Windows.dll`
- `NitroDrop.ShellExt.dll` — the Explorer "Send with NitroDrop" menu (native C++)
- the package `NitroDrop-<version>-x64.msix` (its Publisher is the SignPath Foundation certificate subject) and the
  files inside `NitroDrop-<version>-win-x64-portable.zip`

Product name and version metadata of every signed file are set by the build (`Product` = NitroDrop, `Version` = the
release version) and enforced by the SignPath signing policy.

Third-party files in the packages (the .NET runtime, WPF, QRCoder, …) keep their original publishers' signatures or
are left unsigned; we do not re-sign other people's binaries.

## What is NOT signed

- **The Wi-Fi kernel driver** (`airdropwifi.sys`, `.inf`, `.cat`) and `adwifi-cli.exe`. Kernel drivers need a
  Microsoft attestation signature, which a SignPath certificate cannot provide. They are published separately as
  `NitroDrop-driver-<version>.zip`, clearly labelled *unsigned / test signing only*, and are never part of the signed
  packages. The setup wizard explains every system change (BitLocker, Secure Boot, test signing) before making it and
  can undo all of them.
- **Wintun** (`wintun.dll`, WireGuard LLC). It is not open source under an OSI licence, so it is not included. The app
  downloads it from wintun.net only after the user agrees, and checks its SHA-256 and its WireGuard LLC signature
  before loading it.
- The Intel Wi-Fi firmware, which the setup wizard downloads from linux-firmware (pinned SHA-256) when the user
  installs the driver.

## Privacy

See [PRIVACY.md](PRIVACY.md). This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it.

## Reporting

Security issues or a suspected misuse of the signing certificate: open a GitHub issue (without exploit details) or
write to the maintainer listed above; we will contact SignPath Foundation and help investigate.
