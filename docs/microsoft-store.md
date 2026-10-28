# Microsoft Store başvurusu

SignPath Foundation başvurusu 28 Eki 2026'da reddedildi (proje yeni, görünürlük az). Store'da paketi Microsoft
imzalar: kullanıcı sertifika kurmaz, SmartScreen uyarısı çıkmaz. Bireysel geliştirici hesabı ücretsizdir.

## 1. Hesap (Emre yapar)
1. https://storedeveloper.microsoft.com → "Get started" → **Individual** (bireysel) hesap, Microsoft hesabıyla.
   Kimlik doğrulama istenebilir. Ödeme bilgisi istenirse dur: bireysel hesap ücretsiz olmalı.
2. Partner Center → Apps and games → **New product** → MSIX or PWA app → ad: **NitroDrop** (ayır/reserve).
3. Product identity sayfasındaki üç değeri not et:
   - `Package/Identity/Name` (ör. `12345Nitrobyte.NitroDrop`)
   - `Package/Identity/Publisher` (ör. `CN=XXXXXXXX-XXXX-...`)
   - `Package/Properties/PublisherDisplayName`

## 2. Paket (Claude/geliştirici yapar)
```powershell
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 -NoSign -SkipZip -DriverOptional `
    -Version 0.3.0.0 -IdentityName '<Name>' -Publisher '<Publisher>' -PublisherDisplayName '<DisplayName>'
```
- Store sürümünün 4. hanesi **0** olmalı (`a.b.c.0`).
- İmzalamaya gerek yok; Store imzalar. Sürücü zip'i Store'a gitmez (çekirdek sürücü Store'da dağıtılamaz).
- Yükleme öncesi Windows App Certification Kit (`appcert.exe`, Windows SDK) ile test edilebilir.

## 3. Mağaza sayfası metinleri

**Ad:** NitroDrop · **Kategori:** Productivity · **Fiyat:** Ücretsiz · **Gizlilik:**
https://github.com/nitrosensei/nitrodrop/blob/main/PRIVACY.md · **Destek:** https://github.com/nitrosensei/nitrodrop/issues

**Kısa açıklama (EN):** Your iPhone on your Windows PC: notifications, messages and calls over Bluetooth, plus file
sharing between PCs. Free and open source.

**Açıklama (EN):**
NitroDrop brings your iPhone and your Windows PC closer together, for free and without accounts or cloud services.
- See iPhone notifications on your PC, read and reply to messages, take calls with the PC's microphone and speakers (Bluetooth).
- Share files and links between PCs on the same network.
- Continue where you left off: open tabs and apps from your other PCs.
- "Send with NitroDrop" in File Explorer, shared clipboard, 6 languages.
Open source (MIT): https://github.com/nitrosensei/nitrodrop. NitroDrop is not affiliated with Apple.

**Kısa açıklama (TR):** iPhone'unuz Windows PC'nizde: Bluetooth ile bildirimler, mesajlar ve aramalar; PC'ler arası
dosya paylaşımı. Ücretsiz ve açık kaynak.

**Ekran görüntüleri:** `docs/images/` (phone-messages, phone-notifications, phone-calls, call-ringing-*, nearby-*,
devices-*). Store en az 1366×768 ister; gerekirse `--snapshot` ile o boyutta yeniden alınır.

**Yaş derecelendirmesi (IARC):** iletişim özelliği var (mesajlaşma) → anket buna göre doldurulur; reklam/satın alma yok.

## 4. Kısıtlı yetenek (restricted capability) gerekçeleri
Partner Center "Submission options → Restricted capabilities" kutusuna:

- **runFullTrust:** WPF desktop app (packaged Win32); Bluetooth, networking and File Explorer integration need the full desktop API.
- **phoneLineTransportManagement:** Lets the user answer iPhone calls on the PC via the Windows PhoneLine (HFP) API, the same mechanism Phone Link uses.
- **unvirtualizedResources:** Settings and received files are stored in normal user folders so the portable build and the packaged app share them and the Explorer menu can find them.
- **allowElevation:** Only the optional "Advanced Setup" wizard asks for administrator rights, after explaining each step, to install the separately downloaded Wi-Fi driver. The app itself runs as the normal user.

## 5. Bilinen riskler
- **allowElevation** ve sürücü kurulum sihirbazı denetimde reddedilebilir. Reddedilirse Store sürümünde sihirbaz
  ve Wi-Fi bölümü kapatılır (bu kısım zaten yalnızca Secure Boot kapalıyken işe yarıyor); GitHub sürümü aynen kalır.
- **Wintun indirmesi** (wintun.net, kullanıcı onayıyla) "uygulama sonradan kod indirmesin" kuralına takılabilir.
  Aynı çözüm: Store sürümünde Wi-Fi/AWDL bölümü yok.
- "AirDrop", "iPhone" adları yalnızca uyumluluğu anlatmak için kullanılır; ad ve logo Apple'ı çağrıştırmaz.
