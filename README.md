# NitroDrop (airdrop-win)

Windows için ücretsiz, açık kaynak, AirDrop uyumlu dosya paylaşımı (iPhone / iPad / Mac ↔ PC). Nitrobyte.
Nitro: NB-0011.

## Katmanlar

| Katman | Ne | Durum |
|---|---|---|
| Arayüz | WPF, Apple tarzı radar, geçmiş, ayarlar, ortak pano (Ctrl+Alt+A), rehber fotoğrafları, güvenilir cihaz (`app/AirDrop.App`, ad: NitroDrop) | Çalışıyor (LAN modu; `--demo` ile sahte veri) |
| AirDrop protokolü | mDNS `_airdrop._tcp` + HTTPS /Discover /Ask /Upload, link paylaşımı, medya yetenekleri, rehber hash eşleştirme (`app/AirDrop.Core`) | Bitti, 54/54 test; LAN'da PC↔PC uçtan uca çalıştı |
| BLE | Apple 0x004C / tip 0x05 duyuru + tarama (`tools/ble_test.py`) | Windows'ta doğrulandı (2026-10-28) |
| Wi-Fi sürücüsü (ana yol) | Dahili Intel AX203 (PCI 8086:51F1) için kendi KMDF sürücümüz — OpenBSD `if_iwx.c` portu, IBSS + monitor + inject, sabit kanal 6 (`driver/`, `docs/driver-research.md`) | M1 bitti (1766 host testi, donanımda denenmedi); M2 monitor RX + M3 IBSS/inject yazılıyor |
| Kurulum sihirbazı | Secure Boot/testsigning/BitLocker rehberi, sürücü kurma/geri alma (`app/AirDrop.App/Setup`, `--setup-dry-run`) | Yazıldı: dry-run + testler tamam, gerçek kurulum denenmedi |
| Sekme/uygulama senkronu | PC↔PC açık sekmeler + uygulamalar (6 haneli eşleştirme, pinli TLS + HMAC), Chrome/Edge MV3 eklentisi (`extension/`), native host (`app/Nitro.Sync.Host`), iPhone "PC'de Aç" Kestirmesi + uygulama→PC eşleme (`app/Nitro.Sync`, `docs/sync-plan.md`, `docs/ios-shortcut.md`) | Kütüphane + eklenti bitti (157/157 test); UI'ya bağlanacak |
| Süreklilik (Bluetooth) | iPhone bildirimleri (ANCS), mesajlar (MAP), aramalar (HFP) (`app/Nitro.Continuity`, `docs/continuity-plan.md`) | Araştırma + iskelet |
| AWDL | Owly (t4t5/owly, Rust, MIT) portu | Bekliyor |

## Sürücü imzası ve dağıtım
- **Faz 1 (ücretsiz):** uygulama içi "Gelişmiş Kurulum" sihirbazı — BitLocker kurtarma anahtarı yedeği, üreticiye göre Secure Boot kapatma talimatı, "BIOS'a yeniden başlat", testsigning, sürücü kurulumu, tek tıkla geri alma. Bedeli kullanıcıya açıkça gösterilir (Test Modu filigranı, anti-cheat oyunlar çalışmaz). Dağıtım: GitHub + nitrobyte.com.tr.
- **Faz 2:** bağış/sponsorla EV sertifika → Microsoft attestation imzası; sihirbaz kalkar, Microsoft Store'a girilir. (Azure Trusted Signing sürücü imzalamayı desteklemiyor — Nisan 2026.)
- **Yedek yol:** USB Wi-Fi adaptör (MT7612U) + Microsoft'un inbox WinUSB'si + user-mode sürücü; kernel kodu/sertifika gerekmez (`docs/usb-plan.md`). Intel kartı olmayan kullanıcılar için.
- Asla: imzalı-zafiyetli sürücü istismarı (BYOVD).

## Lisans
MIT ([LICENSE](LICENSE)). Üçüncü taraf bileşenler ve lisansları: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
(iwx ISC, Linux iwlwifi BSD-3 seçeneği, Owly MIT, QRCoder MIT, AppleDB MIT, yazı tipleri OFL 1.1). itlwm (GPL),
OWL (GPL-3) ve OpenDrop (GPL-3) kodu kopyalanmaz. Wintun (WireGuard LLC, OSI dışı lisans) pakete konmaz; kullanıcı
onaylarsa ilk kullanımda indirilip doğrulanır.

## İmzalama / Code signing
Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Code signing policy:** [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) — kim imzalar/onaylar, ne imzalanır
  (NitroDrop.exe, NitroDrop.Radio.exe, NitroDrop.SyncHost.exe, kendi DLL'lerimiz, MSIX, Explorer menüsü DLL'i) ve ne
  imzalanmaz (çekirdek sürücüsü: ayrı `NitroDrop-driver-<sürüm>.zip`, yalnızca test imzalama; Wintun: WireGuard LLC imzalı, ayrıca indirilir).
- **Gizlilik / Privacy:** [PRIVACY.md](PRIVACY.md) — telemetri yok; ağ trafiği yalnızca yakındaki cihazlarla ve
  kullanıcının istediği indirmeler için.
- **Kaldırma / Uninstall:** MSIX → Windows Ayarlar > Uygulamalar > NitroDrop > Kaldır. Portable → önce Ayarlar'dan
  Explorer menüsünü ve "Windows ile başlat"ı kapatın, sonra klasörü silin (veriler: `%LOCALAPPDATA%\airdrop-win`).
  Wi-Fi sürücüsü → Gelişmiş Kurulum > "Geri al" (sürücü, sertifika ve test imzalama modu kaldırılır).
- Sürümler GitHub Actions'ta (`.github/workflows/release.yml`) imzasız derlenir, SignPath'te imzalanır, taslak
  sürüm olarak yayımlanır. Başvuru adımları: [docs/signpath-apply.md](docs/signpath-apply.md).

## Derleme
- Uygulama: `cd app; dotnet build AirDrop.slnx; dotnet test AirDrop.slnx` (.NET 10)
- Sürücü: `driver\build.ps1 -Name <ad> -Sources <dosyalar>` (VS 2022 Build Tools MSVC 14.44 + WDK 10.0.26100, KMDF 1.33)
- Sürüm paketleri: `powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 [-Sign | -NoSign] [-Publisher "CN=..."] [-DriverOptional]`
  → `packaging\out\NitroDrop-<sürüm>-x64.msix`, `...-win-x64-portable.zip`, `NitroDrop-driver-<sürüm>.zip` (ayrı, imzasız)
- UI görüntüsü (Debug, odak çalmaz): `NitroDrop.exe --snapshot <klasör>`

## Araçlar
- `.venv` (Python + winrt): `.\.venv\Scripts\python.exe -I tools\ble_test.py`
- `tools\usb-list.ps1`: USB Wi-Fi yongası / Secure Boot / Memory Integrity durumu (salt okunur)
