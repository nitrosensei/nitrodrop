# Code signing policy

**Current status: NitroDrop releases are not signed with a publicly trusted code-signing certificate.**
An application to the SignPath Foundation programme was declined in October 2026 because the project is still new;
we may apply again later. A Microsoft Store listing (where Microsoft signs the package) is planned.

This policy applies to the NitroDrop releases published on this repository's
[Releases](https://github.com/nitrosensei/nitrodrop/releases) page.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [Emre Can Temur](https://github.com/z3x0fly) (Nitrobyte) |
| Approvers | [Emre Can Temur](https://github.com/z3x0fly) (Nitrobyte) |

- **Committers** may change the source code directly.
- **Reviewers**: every change proposed by someone who is not a committer (for example a pull request from a fork) is
  reviewed by a committer before it is merged.
- **Approvers** decide when a version tag is pushed and the draft release is published.
- All team members use multi-factor authentication for GitHub.

## How releases are built

1. A maintainer pushes a version tag (`v*`). GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml))
   builds the release on a GitHub-hosted Windows runner from that tag and runs the tests.
2. The MSIX is signed on that runner with a **self-signed certificate created for that build only** (`CN=Nitrobyte`).
   Its private key never leaves the runner and is gone when the job ends. The public part is published as
   `NitroDrop-dev.cer`; `install-msix.ps1` adds it to the PC's *Trusted People* store (not *Root*, so it cannot vouch
   for websites) and installs the package.
3. The artifacts are attached to a draft GitHub release, checked and published. Every file's SHA-256 is shown on the
   release page.

Nothing published is built on a developer machine, except the separately labelled unsigned driver zip (the runner
has no WDK).

## What a publicly trusted signature would cover

When a public certificate (or the Microsoft Store) is in place, it will cover only binaries built from this repository's source code:

- `NitroDrop.exe`, `NitroDrop.dll` — the app
- `NitroDrop.Radio.exe`, `NitroDrop.Radio.dll` — the elevated Wi-Fi radio helper
- `NitroDrop.SyncHost.exe`, `NitroDrop.SyncHost.dll` — the browser extension's native messaging host
- `nitro-phoneline-probe.exe` / `.dll` — read-only PhoneLine diagnostics
- our own libraries: `AirDrop.Core.dll`, `Nitro.Awdl.dll`, `Nitro.Sync.dll`, `Nitro.Continuity.dll`,
  `Nitro.Continuity.Windows.dll`
- `NitroDrop.ShellExt.dll` — the Explorer "Send with NitroDrop" menu (native C++)
- the package `NitroDrop-<version>-x64.msix` and the files inside `NitroDrop-<version>-win-x64-portable.zip`

Product name and version metadata of every signed file are set by the build (`Product` = NitroDrop, `Version` = the
release version).

Third-party files in the packages (the .NET runtime, WPF, QRCoder, …) keep their original publishers' signatures or
are left unsigned; we do not re-sign other people's binaries.

## What is NOT signed

- **The Wi-Fi kernel driver** (`airdropwifi.sys`, `.inf`, `.cat`) and `adwifi-cli.exe`. Kernel drivers need a
  Microsoft attestation signature, which an ordinary code-signing certificate cannot provide. They are published separately as
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
write to the maintainer listed above.
