# airdrop-win

Windows için sıfırdan AirDrop (iPhone ↔ PC), dahili Intel Wi-Fi 6 AX203 (PCI 8086:51F1, subsys 0274) üzerinde.

## Katmanlar

| Katman | Ne | Durum |
|---|---|---|
| BLE | Apple 0x004C / tip 0x05 AirDrop duyurusu + Apple duyurularını tarama | **DOĞRULANDI** (2026-10-28, `tools/ble_test.py`): yayın `Started`, 9 Apple cihaz görüldü |
| Sürücü | Intel kart için kendi KMDF sürücümüz (OpenBSD if_iwx.c portu, ISC), IOCTL ile IBSS + monitor RX + inject, sabit kanal 6 | Araştırma bitti (`docs/driver-research.md`); M1 (firmware ALIVE) yazılıyor. Donanım testi EV sertifika + attestation imzası bekliyor |
| AWDL | Owly (t4t5/owly, Rust, MIT) Windows portu, sürücünün IOCTL'ı üzerine | Bekliyor |
| AirDrop | mDNS/DNS-SD (`_airdrop._tcp`) + HTTPS (/Discover, /Ask, /Upload) | Bekliyor |
| Arayüz | Sağ tık "AirDrop ile gönder", tepsi simgesi, alma bildirimi | Bekliyor |

## Kararlar
- Normal internet AirDrop açıkken Tenda USB (AIC8800DC) üzerinden; Intel kart AWDL'e ayrılır (exclusive mode).
- "Herkes (10 dk)" modu hedef; "Yalnızca Kişiler" Apple imzalı kayıt ister, kapsam dışı.

- Secure Boot asla kapatılmaz, test modu yok: sürücü her yerde Microsoft attestation imzalı yüklenir, HVCI uyumlu yazılır.
- Lisans hedefi izin verici (MIT/BSD): iwx ISC, Linux iwlwifi BSD-3 seçeneği, Owly MIT. itlwm (GPL) ve OWL (GPL-3) kod kopyalanmaz.

## Bekleyenler
- Nitrobyte adına EV kod imzalama sertifikası + Microsoft Partner Center hesabı (Emre).

## Derleme
- Sürücü: `driver\build.ps1 -Name <ad> -Sources <dosyalar>` (VS 2022 Build Tools MSVC 14.44 + WDK 10.0.26100, KMDF 1.33).

## Araçlar
- `.venv` (Python 3.14 + winrt-*): `.\.venv\Scripts\python.exe -I tools\ble_test.py`
