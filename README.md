# NitroDrop

**Free, open-source file sharing between iPhone / iPad / Mac and Windows PCs.**
By [Nitrobyte](https://nitrobyte.com.tr) · Made in Türkiye · MIT licensed

> 🇹🇷 Türkçe açıklama aşağıda.

> **Status: early preview (0.x).** The app, protocol stack and tests are complete; real-hardware testing
> with iPhones is in progress. Expect rough edges.

## What it does

| Feature | How | Needs |
|---|---|---|
| Send / receive files and links with Apple devices | mDNS + HTTPS discovery/transfer, BLE advertising | Wi-Fi driver (see below) |
| PC ↔ PC sharing on the same network | Same protocol over your normal LAN | Nothing extra |
| iPhone notifications, messages and calls on the PC | Bluetooth: ANCS, MAP, PBAP, HFP; PC microphone/speaker via the Windows PhoneLine API | A paired iPhone |
| Open tabs / apps continuity | Paired PCs (6-digit code, pinned TLS), Chrome/Edge extension, iOS Shortcut "Open on PC" | Optional |
| Explorer "Send with NitroDrop" menu, shared clipboard, contact photos, device model recognition | — | — |
| 6 languages | Turkish, English, German, French, Russian, Polish | — |

### About the Wi-Fi driver
iPhones only discover nearby devices over Apple Wireless Direct Link (AWDL). Windows Wi-Fi drivers cannot
speak it, so NitroDrop has an **optional, separate** kernel driver for Intel Wi-Fi 6 cards (AX201/AX203
family), ported from OpenBSD's `iwx` (ISC). Until that driver carries a Microsoft signature it only loads with
Secure Boot off and test-signing on; the in-app *Advanced Setup* wizard explains the trade-offs and can undo
everything. The driver is **not** part of the signed app package. Everything else works without it.

## Download
See [Releases](https://github.com/z3x0fly/nitrodrop/releases):
- `NitroDrop-<version>-x64.msix` — installer (recommended)
- `NitroDrop-<version>-win-x64-portable.zip` — portable
- `NitroDrop-driver-<version>...zip` — optional Wi-Fi driver, unsigned (test-signing only)

Requires Windows 10 (2004 or later) or Windows 11, x64.

## Code signing
Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) for who builds, reviews and approves releases, and what is (and is not) signed.

## Privacy
No telemetry, no accounts, no cloud. Network traffic only goes to nearby devices you share with, PCs you paired,
and downloads you explicitly approve. Details: [PRIVACY.md](PRIVACY.md).

## Uninstall
- **MSIX:** Settings → Apps → NitroDrop → Uninstall.
- **Portable:** turn off the Explorer menu and "Start with Windows" in NitroDrop settings, then delete the folder
  (data lives in `%LOCALAPPDATA%\airdrop-win`).
- **Wi-Fi driver:** Advanced Setup → *Undo* (removes the driver, its certificate and test-signing mode).

## Building
```powershell
cd app
dotnet build AirDrop.slnx
dotnet test AirDrop.slnx                   # .NET 10 SDK
..\packaging\build-release.ps1 -NoSign     # MSIX + portable + driver zip in packaging\out
```
The driver needs Visual Studio Build Tools (MSVC) + WDK 10.0.26100: `driver\build.ps1`.
Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) and signed via SignPath.

## License
MIT — see [LICENSE](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Wintun (WireGuard LLC) is not bundled; it is downloaded and verified only after you approve it.

"AirDrop", "iPhone", "iPad" and "Mac" are trademarks of Apple Inc. NitroDrop is an independent project and is
not affiliated with or endorsed by Apple.

---

## 🇹🇷 Türkçe

**NitroDrop**, iPhone / iPad / Mac ile Windows bilgisayarlar arasında ücretsiz ve açık kaynak dosya paylaşımıdır.

- iPhone'dan PC'ye, PC'den iPhone'a dosya ve link gönderme
- Aynı ağdaki PC'ler arasında paylaşım
- iPhone bildirimleri, mesajları ve aramaları PC'de (Bluetooth; arama sesi PC mikrofonu/hoparlöründen)
- Açık sekme ve uygulama senkronu, Gezgin'de "NitroDrop ile gönder", ortak pano
- 6 dil

iPhone yakındaki cihazları yalnızca Apple'ın özel Wi-Fi bağlantısı (AWDL) ile bulur. Bunun için Intel Wi-Fi 6
kartlarına özel, **isteğe bağlı ve ayrı** bir sürücü var. Microsoft imzası alınana kadar yalnızca Secure Boot
kapalıyken çalışır; uygulama içindeki *Gelişmiş Kurulum* sihirbazı bedelini açıkça anlatır ve her şeyi geri alabilir.
Sürücü dışındaki tüm özellikler sürücüsüz çalışır.

İndirme: [Sürümler](https://github.com/z3x0fly/nitrodrop/releases) · Gizlilik: [PRIVACY.md](PRIVACY.md) ·
İmzalama: [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) · Lisans: MIT
