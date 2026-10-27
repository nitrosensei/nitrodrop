# NitroDrop (airdrop-win)

Windows için ücretsiz, açık kaynak, AirDrop uyumlu dosya paylaşımı (iPhone / iPad / Mac ↔ PC). Nitrobyte.
Nitro: NB-0011.

## Katmanlar

| Katman | Ne | Durum |
|---|---|---|
| Arayüz | WPF, Apple tarzı radar, geçmiş, ayarlar, ortak pano (Ctrl+Alt+A), rehber fotoğrafları, güvenilir cihaz (`app/AirDrop.App`, ad: NitroDrop) | Çalışıyor (LAN modu; `--demo` ile sahte veri) |
| AirDrop protokolü | mDNS `_airdrop._tcp` + HTTPS /Discover /Ask /Upload, link paylaşımı, medya yetenekleri, rehber hash eşleştirme (`app/AirDrop.Core`) | Bitti, 54/54 test; LAN'da PC↔PC uçtan uca çalıştı |
| BLE | Apple 0x004C / tip 0x05 duyuru + tarama (`tools/ble_test.py`) | Windows'ta doğrulandı (2026-10-28) |
| Wi-Fi sürücüsü (ana yol) | Dahili Intel AX203 (PCI 8086:51F1) için kendi KMDF sürücümüz — OpenBSD `if_iwx.c` portu, IBSS + monitor + inject, sabit kanal 6 (`driver/`, `docs/driver-research.md`) | M1 (firmware ALIVE) yazılıyor |
| AWDL | Owly (t4t5/owly, Rust, MIT) portu | Bekliyor |

## Sürücü imzası ve dağıtım
- **Faz 1 (ücretsiz):** uygulama içi "Gelişmiş Kurulum" sihirbazı — BitLocker kurtarma anahtarı yedeği, üreticiye göre Secure Boot kapatma talimatı, "BIOS'a yeniden başlat", testsigning, sürücü kurulumu, tek tıkla geri alma. Bedeli kullanıcıya açıkça gösterilir (Test Modu filigranı, anti-cheat oyunlar çalışmaz). Dağıtım: GitHub + nitrobyte.com.tr.
- **Faz 2:** bağış/sponsorla EV sertifika → Microsoft attestation imzası; sihirbaz kalkar, Microsoft Store'a girilir. (Azure Trusted Signing sürücü imzalamayı desteklemiyor — Nisan 2026.)
- **Yedek yol:** USB Wi-Fi adaptör (MT7612U) + Microsoft'un inbox WinUSB'si + user-mode sürücü; kernel kodu/sertifika gerekmez (`docs/usb-plan.md`). Intel kartı olmayan kullanıcılar için.
- Asla: imzalı-zafiyetli sürücü istismarı (BYOVD).

## Lisans hedefi
İzin verici (MIT/BSD): iwx ISC, Linux iwlwifi BSD-3 seçeneği, Owly MIT, mt76 BSD-3-Clause-Clear. itlwm (GPL) ve OWL (GPL-3) kodu kopyalanmaz.

## Derleme
- Uygulama: `cd app; dotnet build AirDrop.slnx; dotnet test AirDrop.slnx` (.NET 10)
- Sürücü: `driver\build.ps1 -Name <ad> -Sources <dosyalar>` (VS 2022 Build Tools MSVC 14.44 + WDK 10.0.26100, KMDF 1.33)
- UI görüntüsü (Debug, odak çalmaz): `NitroDrop.exe --snapshot <klasör>`

## Araçlar
- `.venv` (Python + winrt): `.\.venv\Scripts\python.exe -I tools\ble_test.py`
- `tools\usb-list.ps1`: USB Wi-Fi yongası / Secure Boot / Memory Integrity durumu (salt okunur)
