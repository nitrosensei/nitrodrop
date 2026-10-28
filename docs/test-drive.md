# NitroDrop test sürüşü: Acer Nitro V 15

Bu rehber NitroDrop'u ilk kez kendi laptopunda denemen için. Üç bölüm var:

- **Bölüm A:** sistemde hiçbir şeyi değiştirmeden. Kurulum, ilk kullanım, PC↔PC, ortak pano, sekmeler, iPhone Kestirmesi.
- **Bölüm B:** Bluetooth. iPhone bildirimleri, mesajlar, aramalar.
- **Bölüm C:** Wi-Fi sürücüsü ve gerçek AirDrop. **Sistem ayarlarını değiştirir** (Secure Boot, test modu, sürücü).
  Sona bırak. Sadece A ve B sorunsuz geçtiyse ve vaktin varsa yap.

Her adımda üç şey yazıyor: **ne yapacaksın**, **ne görmen gerekiyor**, **olmazsa bana ne göndereceksin**. Bir
şey ters giderse dur ve bir sonraki adıma geçme. Gönderilecek dosyaların toplu listesi en sonda
([Bana ne göndereceksin](#bana-ne-göndereceksin)).

> Bu ilk donanım denemesi. Özellikle Bölüm C'nin bir yerde takılması normal. Amaç o noktada doğru logları
> toplamak.

---

## 0. Başlamadan önce

- [ ] **Şarj aleti** takılı olsun. Bölüm C'de BIOS'a girip birkaç kez yeniden başlatacaksın.
- [ ] **iPhone** şarjlı, aynı Wi-Fi ağında, iOS güncel.
- [ ] **Seni arayacak bir arkadaş** (Bölüm B'de gelen arama testi için 5 dakika yeter).
- [ ] **Tenda USB Wi-Fi adaptörü** (Bölüm C için). Sürücü değişince dahili Intel Wi-Fi kapanır, internete bu
      adaptörle çıkacaksın. **Bölüm C'den önce**, internet varken tak ve çalıştığını gör. Sürücü isterse şimdi kur.
- [ ] **BitLocker kurtarma anahtarı** telefonda ya da kâğıtta olsun (bilgisayarın dışında). Secure Boot
      değişince açılışta sorabilir. <https://aka.ms/myrecoverykey> adresinden Microsoft hesabıyla da bakabilirsin.
      Sihirbaz da gösteriyor.
- [ ] (Varsa) **ikinci PC**: masaüstü w0fly. PC↔PC dosya, pano ve sekme testleri için. Aynı ağda olmalı.
- [ ] Bende hazırlanan dosyalar (`packaging\out\` klasörü), USB bellekte ya da bulutta:
  - `NitroDrop-<sürüm>-x64.msix` (asıl paket), `NitroDrop-dev.cer`, `install-msix.ps1`
  - `NitroDrop-<sürüm>-win-x64-portable.zip` (kurulumsuz sürüm)

### Hangi paketi kullanayım?

| | **MSIX (önerilen)** | **Portable zip** |
|---|---|---|
| Kurulum | Bir kez sertifika onayı (UAC), sonra normal kurulum | Klasöre çıkar, `NitroDrop.exe`'yi çalıştır |
| Arama sesi PC'de (HFP) | **Var** (paket kimliği gerekiyor) | **Yok**: arama yalnızca kontrol (cevapla/reddet iPhone'dan çalar) |
| Windows ile başlat | Başlangıç görevi (Görev Yöneticisi → Başlangıç uygulamaları) | HKCU Run kaydı |
| Kaldırma | Ayarlar → Uygulamalar → NitroDrop → Kaldır | Klasörü sil |
| Gezgin "NitroDrop ile gönder" | Yeni sağ tık menüsünde, kurulunca hazır | Ayarlar'dan açılır; Windows 11'de "Diğer seçenekleri göster" altında + Gönder menüsü |

İkisi de ayarları aynı yerde tutar (`%LOCALAPPDATA%\airdrop-win`). **Aynı anda ikisini açma.** Biri açıkken öteki
açılırsa kendini kapatıp açık olanı öne getirir. Karşılaştırmak için birini tepsiden "Çıkış" ile kapat, ötekini aç.

---

## Bölüm A: sistem değişikliği yok

### A1. Kurulum

**MSIX:**
- [ ] Üç dosyayı (`.msix`, `.cer`, `install-msix.ps1`) aynı klasöre koy, örneğin `İndirilenler\NitroDrop`.
- [ ] O klasörde boş bir yere Shift + sağ tık → "PowerShell penceresini burada aç" (ya da Terminal), sonra:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\install-msix.ps1
  ```
- [ ] **Bir kez UAC sorar** ("Windows PowerShell değişiklik yapsın mı?") → Evet. Bu adım geliştirici sertifikamızı
      `Yerel Bilgisayar\Güvenilen Kişiler` deposuna ekliyor. Kök sertifika değil, web sitelerine güven vermez.
      Sonraki güncellemelerde tekrar sormaz.
- [ ] Beklenen: `Installed Nitrobyte.NitroDrop_...` satırı. Başlat menüsünde **NitroDrop** görünür.
- Elle yapmak istersen (aynı şey):
  ```powershell
  # Yönetici PowerShell, bir kez:
  Import-Certificate -FilePath .\NitroDrop-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
  # Normal PowerShell:
  Add-AppxPackage -Path .\NitroDrop-<sürüm>-x64.msix
  ```
- **Olmazsa:** hata metninin ekran görüntüsü + Olay Görüntüleyicisi → Uygulama ve Hizmet Günlükleri → Microsoft →
  Windows → **AppXDeploymentServer → Microsoft-Windows-AppXDeploymentServer/Operational** (son kırmızı kayıtlar),
  ya da `Get-AppxLog | Out-File appx.txt` çıktısı.

**Portable:**
- [ ] Zip'i `C:\NitroDrop` gibi kalıcı bir yere çıkar (İndirilenler'de bırakma, eklenti yolu oraya bağlanıyor).
- [ ] `NitroDrop.exe`'yi çalıştır. İmzasız olduğu için SmartScreen "Windows bilgisayarınızı korudu" diyebilir:
      **Ek bilgi → Yine de çalıştır**.

### A2. İlk açılış ve tanıtım

- [ ] NitroDrop açılınca 6 adımlık **İlk kullanım** ekranı gelir. ←/→ ile gez. Son adımda görünen adı ("Emre-Nitro"),
      "Beni görebilecekler"i ve **"Windows açılınca başlat"**ı ayarla.
- [ ] **Windows Güvenlik Duvarı** izin sorarsa **Özel ağlar**'ı işaretle → İzin ver. Bu izin olmadan diğer cihazlar
      seni göremez.
- [ ] Beklenen: tepside NitroDrop simgesi; pencere geniş açılır, solda Yakındakiler · Cihazlar · Telefon · Geçmiş
      (dar pencerede bunlar üstte sekme olur).
- [ ] (MSIX) "Windows açılınca başlat" açıkken Görev Yöneticisi → **Başlangıç uygulamaları**'nda NitroDrop
      "Etkin" görünmeli. Bir kez oturumu kapatıp aç: NitroDrop pencere açmadan tepsiye gelmeli.
- **Olmazsa:** ekran görüntüsü + Olay Görüntüleyicisi → Windows Günlükleri → **Uygulama** → Kaynak ".NET Runtime" ya
  da "Application Error" olan son kayıtlar (Ayrıntılar sekmesinin metni).

### A2b. Dil ve pencere boyutu

- [ ] Windows dilin Türkçe ise arayüz Türkçe açılır. **Ayarlar → DİL** → "Deutsch" seç: tüm pencere (kenar çubuğu,
      sekmeler, tepsi menüsü dahil) hemen Almancaya döner, yeniden başlatma gerekmez. Sırayla "Русский" ve
      "Polski"yi de dene: Kiril harfler düzgün (kutucuk ya da soru işareti yok), yazılar taşmıyor, kesik değil.
- [ ] Dosya Gezgini'nde bir dosyaya sağ tıkla (Windows 11'de "Daha fazla seçenek göster"): menü öğesi seçili dilde
      ("Mit NitroDrop senden", "Отправить через NitroDrop"…).
- [ ] "Windows ile aynı" seçeneğine dön → Türkçe. NitroDrop'u kapatıp açınca seçim korunur.
- [ ] Pencereyi daralt/genişlet: ~720 px altında üstte sekmeler, üstünde solda kenar çubuğu, ~1100 px üstünde
      Cihazlar/Geçmiş/Ayarlar iki bölmeli; radar alana göre büyüyüp küçülür. Büyüt (□) çalışır; kapatıp açınca
      boyut ve konum hatırlanır.
- **Olmazsa:** hangi dilde ve ekranda olduğu, ekran görüntüsü.

### A3. PC ↔ PC dosya (w0fly ile)

- [ ] Masaüstüne de portable zip'i çıkarıp NitroDrop'u aç (Güvenlik Duvarı → Özel ağlar).
- [ ] Beklenen: iki PC birbirinin **Yakındakiler** radarında birkaç saniye içinde görünür (Windows simgesiyle).
- [ ] Laptoptan bir fotoğrafı ve bir klasörü masaüstündeki cihazın üstüne sürükle → masaüstünde onay penceresi →
      Kabul → dosyalar `İndirilenler`'e iner, Geçmiş'te görünür. Ters yönde de dene.
- [ ] Türkçe karakterli bir dosya adı dene (`çiğdem_şükür.txt`).
- **Olmazsa:** iki PC'nin `ipconfig` çıktısı, hangi yönde çalışmadığı, ekran görüntüsü. Not: misafir ağı ya da
  "Ortak ağ" profili mDNS'i engeller. Ayarlar → Ağ → Wi-Fi → ağ profili **Özel** olmalı.

### A3b. Gezgin sağ tık: "NitroDrop ile gönder"

- [ ] **MSIX:** Gezgin'de bir dosyaya sağ tıkla. Beklenen: yeni (kısa) menüde NitroDrop simgesiyle
      **NitroDrop ile gönder**. Bir klasörde de görünmeli. Ayarlar → Bu bilgisayar → **Gezgin sağ tık menüsü** açık ve
      gri (değiştirilemez, "Windows tarafından yönetiliyor").
- [ ] **Portable:** Ayarlar → Bu bilgisayar → **Gezgin sağ tık menüsü**'nü aç. Sağ tık → **Diğer seçenekleri göster**
      → **NitroDrop ile gönder**. Ayrıca sağ tık → Diğer seçenekleri göster → **Gönder** → **NitroDrop**.
- [ ] Tek dosya: NitroDrop öne gelir, Yakındakiler'de "“dosya” göndermeye hazır — bir cihaz seçin". Masaüstündeki cihaza
      tıkla → gider.
- [ ] Çoklu seçim: 5 dosya + 1 klasör seç → sağ tık → NitroDrop ile gönder. Beklenen: **tek** "6 öge göndermeye hazır"
      (son dosya tek başına değil). NitroDrop kapalıyken de dene (ilk açılışta da hepsi gelmeli).
- [ ] (İsteğe bağlı, MSIX) Çok büyük seçim: bir klasörde ~500 dosyayı seçip gönder → hepsi sayılmalı.
- [ ] Portable: anahtarı kapat → menü ve Gönder → NitroDrop kaybolur.
- **Olmazsa:** sağ tık menüsünün ekran görüntüsü, kaç öge seçtiğin ve kaçının geldiği. Portable'da:
  `reg query "HKCU\Software\Classes\*\shell\NitroDrop" /s`. MSIX'te menü hiç yoksa: Görev Yöneticisi → Ayrıntılar'da
  `explorer.exe`'yi yeniden başlat ve tekrar bak; olmazsa `Get-AppxPackage Nitrobyte.NitroDrop | Format-List` çıktısı.

### A4. Ortak pano

- [ ] Laptopta bir metin kopyala → **Ctrl + Alt + A**. Bu kısayol kopyalananı son kullandığın cihaza gönderir (ilk
      seferde cihaz seçtirir).
- [ ] Beklenen: masaüstünde balon bildirim, metin panoda (Ayarlar'da "Gelenleri panoya kopyala" açık).
- [ ] Bir ekran görüntüsü (Win + Shift + S) ile de dene.
- **Olmazsa:** kısayol çakışıyorsa Ayarlar'da kısayol durumu yazar. Ekran görüntüsünü gönder.

### A5. Bilgisayar eşleştirme ve sekme senkronu (tarayıcı eklentisi)

- [ ] Laptop: **Cihazlar** sekmesi → **Bilgisayar ekle** → masaüstünü seç → iki ekranda aynı 6 haneli kod → iki
      tarafta da **Onayla**.
- [ ] Eklenti (her iki PC'de, Chrome ya da Edge):
  1. NitroDrop → Ayarlar → Senkron → **Tarayıcı eklentisi** → "Eklentiyi kur" yardımı eklenti klasörünü gösterir.
     MSIX'te klasör `%LOCALAPPDATA%\airdrop-win\extension`, portable'da `C:\NitroDrop\extension`. "Kopyala" ile yolu al.
  2. Chrome: `chrome://extensions` (Edge: `edge://extensions`) → sağ üstte **Geliştirici modu** aç →
     **Paketlenmemiş öğe yükle** → kopyaladığın klasör. NitroDrop eklentisi görünür.
  3. NitroDrop'ta **Tarayıcıya bağla**'ya bas → "Chrome ve Edge'e bağlandı". Tarayıcıyı tamamen kapatıp aç.
- [ ] Beklenen: birkaç saniye sonra Ayarlar'da eklenti durumu **Bağlı**. Masaüstünün kartında laptopun açık
      sekmeleri ve uygulamaları görünür. Bir sekmeye tıkla, o PC'nin tarayıcısında açılsın.
- **Olmazsa:** eklenti sayfasındaki hata (Hatalar düğmesi), `%LOCALAPPDATA%\airdrop-win\native-host\` klasöründeki
  `.json` dosyası, şu komutun çıktısı:
  `reg query HKCU\Software\Google\Chrome\NativeMessagingHosts /s` (Edge: `HKCU\Software\Microsoft\Edge\NativeMessagingHosts`).

### A6. iPhone Kestirmesi "PC'de Aç"

- [ ] NitroDrop → Ayarlar → **iPhone (Kestirme)** → **iPhone ekle** → ad ver → QR çıkar.
- [ ] iPhone kamerasıyla QR'ı okut, adresi kopyala. Kestirmeyi uygulamadaki **Kestirme talimatları**na (aynı metin
      `docs\ios-shortcut.md`'de) göre kur: "URL'nin İçeriğini Al", POST, JSON, `url` = Kestirme Girdisi.
- [ ] Safari'de bir sayfa → Paylaş → **PC'de Aç**. Beklenen: PC'de "iPhone'dan bağlantı geldi" balonu, tıklayınca açılır.
- [ ] (İsteğe bağlı) "iPhone'da ne açık" otomasyonu: Instagram açınca PC'de "iPhone'da Instagram açık" kartı.
- **Olmazsa:** Kestirme'nin hata mesajının ekran görüntüsü (iPhone), PC'nin IP'si. Güvenlik Duvarı 8772 portuna izin
  vermiş olmalı (Özel ağ).

---

## Bölüm B: Bluetooth (iPhone bildirimleri, mesajlar, aramalar)

Sistem ayarı değişmez. Yalnızca Bluetooth eşleştirmesi yapılır.

### B1. Phone Link'ten ayır (önemli)

iPhone başka bir uygulamaya (Phone Link / Telefon Bağlantısı) kayıtlıysa Windows aramaları bize vermez.

- [ ] Phone Link'i aç → Ayarlar → Cihazlarım → iPhone → **Kaldır**. Phone Link'i kapat.
- [ ] Windows Ayarlar → Bluetooth ve cihazlar → Cihazlar → iPhone → **Cihazı kaldır**.
- [ ] iPhone'da Ayarlar → Bluetooth → bu PC'nin yanındaki (i) → **Bu Aygıtı Unut**.

### B2. Eşleştirme

- [ ] NitroDrop → **Telefon** sekmesi → **iPhone'u bul**. iPhone'da Ayarlar → Bluetooth ekranı açık kalsın.
- [ ] Listeden iPhone'u **Seç** → iki ekranda aynı 6 haneli kod → iPhone'da **Eşleştir**, PC'de **Kodlar aynı**.
- [ ] Beklenen: "Eşleşti" ve üç anahtar hatırlatması.
- **Olmazsa:** ekran görüntüsü + Olay Görüntüleyicisi → Windows Günlükleri → **Sistem** → Kaynak "BTHUSB" / "BthEnum"
  / "Bluetooth" olan son kayıtlar.

### B3. iPhone'daki üç anahtar

iPhone → Ayarlar → Bluetooth → bu PC'nin yanındaki **(i)**:
- [ ] **Sistem Bildirimlerini Paylaş**: bildirimler ve gelen aramalar (ANCS).
- [ ] **Bildirimleri Göster**: SMS/iMessage okuma ve yanıtlama (MAP).
- [ ] **Kişileri Eşitle**: arayanın adı ve fotoğrafı (PBAP).

Anahtarlar görünmüyorsa NitroDrop'ta **Bildirim bağlantısını kur**'a bas, iPhone'da çıkan izinleri onayla ve (i)
ekranına tekrar bak.

### B4. Bildirimler

- [ ] Kendine bir WhatsApp mesajı attır ya da bir hatırlatıcı kur.
- [ ] Beklenen: Telefon → **Bildirimler**'de görünür, Windows bildirimi gelir. Önizleme ayarı Ayarlar → TELEFON'da.
- **Olmazsa:** Telefon sekmesindeki uyarı metni ("Bildirimler kapalı: …" gibi) ekran görüntüsü.

### B5. Mesajlar (okuma ve yanıtlama)

- [ ] Bir arkadaşın SMS ya da iMessage atsın → Telefon → **Mesajlar**'da görünmeli.
- [ ] Buradan yanıt yaz → karşı tarafa gitmeli.
- **Olmazsa:** "Mesajlar kapalı…" uyarısı varsa (i) ekranında "Bildirimleri Göster"i kontrol et. Ekran görüntüsü.

### B6. Aramalar

- [ ] Arkadaşın seni arasın.
- [ ] Beklenen: PC'de gelen arama penceresi/bildirimi (arayanın adı, Kişileri Eşitle açıksa).
- [ ] **Cevapla** ve **Reddet**'i ayrı aramalarda dene.
- [ ] Telefon → **Aramalar** bölümündeki durum yazısına bak:
  - **MSIX:** "Windows Eller Serbest bağlantısı hazır: aramalar bu bilgisayarın hoparlör ve mikrofonuyla
    konuşulabilir." yazmalı. Cevaplayınca ses laptopun hoparlöründen ve mikrofonundan gitmeli. Windows Ses
    ayarlarında iPhone "Eller Serbest" uç noktası görünür.
    - "…başka bir uygulamaya (büyük olasılıkla Phone Link) kayıtlı" ya da "RegisterApp" yazıyorsa B1'i tekrar yap
      (Phone Link'ten ve Bluetooth'tan kaldır, NitroDrop ile yeniden eşleştir).
    - "Telefon araması erişimine izin verilmedi" yazıyorsa: Windows Ayarlar → Gizlilik ve güvenlik → Telefon
      aramaları → NitroDrop'a izin ver.
  - **Portable:** "NitroDrop paketsiz çalışıyor… konuşma iPhone'da sürer" yazmalı. Cevapla/Reddet çalışır, **ses
    iPhone'da kalır**. Bu normal.
- [ ] (MSIX, ses gelmediyse) PhoneLine teşhisi: normal PowerShell'de:
  ```powershell
  nitro-phoneline-probe.exe --log "$env:LOCALAPPDATA\airdrop-win\phoneline-probe.log"
  ```
  Salt okunur, arama yapmaz. `phoneline-probe.log`'u gönder. (Bu komut yalnızca MSIX kuruluyken bulunur.)
- **Olmazsa:** durum yazısının ekran görüntüsü, `phoneline-probe.log`, arkadaşın aradığı saat (logda bulmak için).

---

## Bölüm C: Wi-Fi sürücüsü ve gerçek AirDrop

> ⚠️ **Bu bölüm sistem ayarlarını değiştirir.** Secure Boot kapanır, Windows **Test Modu**'na geçer (sağ altta
> filigran), Intel Wi-Fi kartı NitroDrop sürücüsüne geçer, bu sırada dahili Wi-Fi çalışmaz. Valorant/Vanguard,
> FACEIT gibi anti-cheat'li oyunlar Test Modu'nda açılmaz. Hepsi geri alınabilir (C7), ama zaman ister.
> Sürücü donanımda **ilk kez** çalışacak. Önemli bir işin varsa yapma.

### C0. Hazırlık

- [ ] Tenda USB adaptörüyle internete bağlan ve çalıştığını doğrula (C3'ten sonra tek internet yolu bu).
- [ ] BitLocker anahtarı elinde (bkz. 0).
- [ ] Bir geri yükleme noktası oluştur: Başlat → "Geri yükleme noktası oluştur" → Oluştur.
- [ ] Portable zip'i de çıkarılmış tut: `C:\NitroDrop\tools\adwifi-cli.exe` lazım olacak (MSIX klasörüne yol
      yazmak zor).

### C1. Gelişmiş Kurulum sihirbazı

- [ ] NitroDrop → Ayarlar → **Wi-Fi Sürücüsü** → **Kur…** (ya da ana penceredeki "Kurulum gerekli → Kur" bandı).
- [ ] **Uyumluluk:** "Uyumlu" demeli. Desteklenen kart Intel AX203 (`PCI\VEN_8086&DEV_51F1&SUBSYS_02748086`).
      **"Desteklenmiyor" derse burada dur.** Acer kartı farklı bir alt sistem kimliğiyle gelmiş olabilir. Bana şunu gönder:
      `pnputil /enum-devices /class Net /connected > net.txt`
- [ ] **Onay** anahtarını aç. **BitLocker:** "Anahtarı göster…" → kaydet. "Yeniden başlatırken bir kez askıya al"
      açık kalsın.

### C2. Secure Boot'u kapat (Acer)

Sihirbaz Acer talimatını gösterir. Acer'da kısaca:
- [ ] Sihirbazda **BIOS'a yeniden başlat** (ya da açılışta **F2**).
- [ ] BIOS → **Security** → **Set Supervisor Password**. Acer bu şifre olmadan Secure Boot'u gri gösterir.
      **Şifreyi bir yere yaz**, unutursan BIOS'a giremezsin.
- [ ] **Boot** → **Secure Boot** → **Disabled** → **F10** (Kaydet ve çık).
- [ ] BitLocker anahtar sorarsa anahtarı gir. Windows açılınca NitroDrop sihirbazı kaldığı yerden açılır
      (açılmazsa NitroDrop → Ayarlar → Wi-Fi Sürücüsü → Kur…).
- **Olmazsa:** BIOS ekranının fotoğrafı.

### C3. Test modu ve sürücü kurulumu

- [ ] **Test modu** adımı → yönetici izni (UAC) → yeniden başlat. Sağ altta "Test Modu" filigranı görünür.
- [ ] **Kurulum** adımı: firmware indirilir (Intel lisansını kabul et) → UAC → sürücü kurulur.
- [ ] Beklenen: "Kart NitroDrop sürücüsüne bağlandı". Aygıt Yöneticisi'nde (Win + X → Aygıt Yöneticisi) kart
      **Sistem aygıtları** altında, sarı ünlemsiz. Intel Wi-Fi ağ listesinden kaybolur. Bu normal.
- **Olmazsa:** `%LOCALAPPDATA%\airdrop-win\setup.log` (sihirbazdaki "Kurulum günlüğü" de açar),
  `C:\Windows\INF\setupapi.dev.log` (son 300 satır yeter), Aygıt Yöneticisi'nde kartın Özellikler → Olaylar ekranı.
  Mavi ekran olursa: `C:\Windows\MEMORY.DMP` ya da `C:\Windows\Minidump\*.dmp`.

### C4. Radyo testi (adwifi-cli)

`driver\README.md`'deki "İlk donanım testi" runbook'unun kısa hali. **Yönetici** PowerShell aç:
```powershell
cd C:\NitroDrop\tools
.\adwifi-cli.exe info
```
- [ ] **info:** state `DOWN`, MAC adresi, firmware `77.f92b5fed.0`, init status 0 olmalı.
- [ ] **Dinleme (M2):**
  ```powershell
  .\adwifi-cli.exe run --listener -c 6 -- read -n 200 -w m2.pcap
  .\adwifi-cli.exe info
  ```
  Yakındaki modemlerin beacon'ları akmalı, `m2.pcap` oluşmalı.
- [ ] **IBSS + iPhone (M3 RX):** iPhone'da Denetim Merkezi → AirDrop → **Herkes (10 dakika)**, sonra:
  ```powershell
  .\adwifi-cli.exe run -c 6 -- read -t 30 -w m3.pcap
  ```
  Bu sırada iPhone'da Paylaş → AirDrop ekranını açık tut. AWDL çerçeveleri görünmeli.
- **Olmazsa** (her adımda): komutun tüm çıktısı (kopyala-yapıştır), `info` çıktısı (`last fw error` satırı),
  `m2.pcap` / `m3.pcap`. Sonraki adıma geçme.

### C5. iPhone → PC AirDrop

- [ ] NitroDrop açık, Yakındakiler'de görünürlük **Herkes**. iPhone'da AirDrop **Herkes (10 dakika)**.
- [ ] iPhone → Fotoğraflar → bir foto → Paylaş → AirDrop. Beklenen: listede "Emre-Nitro" (dizüstü simgesiyle) →
      dokun → PC'de onay penceresi → Kabul → foto `İndirilenler`'de.
- **Olmazsa:** iPhone listesinde PC'nin hiç görünüp görünmediği, `%LOCALAPPDATA%\airdrop-win\logs\radio.log`,
  `%LOCALAPPDATA%\airdrop-win\*.log`, o sırada çalışan bir `adwifi-cli ... read -t 30 -w airdrop.pcap` kaydı.

### C6. PC → iPhone AirDrop

- [ ] Yakındakiler'de iPhone görünmeli. Bir dosyayı üstüne sürükle → iPhone'da "Kabul et" → dosya açılır.
- **Olmazsa:** C5 ile aynı dosyalar.

### C7. Geri alma (her zaman yapılabilir)

- [ ] NitroDrop → Ayarlar → Wi-Fi Sürücüsü → **Kaldır ve eski haline getir…** → UAC. Bu adım sürücüyü siler, Intel
      sürücüsü geri gelir, sertifikalar silinir, test modu kapanır, BitLocker koruması açılır.
- [ ] Yeniden başlat → filigran gider, Intel Wi-Fi ağları geri gelir.
- [ ] BIOS (F2) → Boot → **Secure Boot → Enabled** → F10. İstersen Security → Supervisor Password'ü boş bırakıp
      kaldır.
- [ ] Kontrol: Başlat → "Sistem Bilgisi" → "Güvenli Önyükleme Durumu: Açık".
- **Sihirbaz çalışmazsa elle (yönetici PowerShell):**
  ```powershell
  pnputil /enum-drivers            # "airdropwifi" olan oemNN.inf adını bul
  pnputil /delete-driver oemNN.inf /uninstall /force
  pnputil /scan-devices
  bcdedit /set testsigning off
  manage-bde -protectors -enable C:
  ```
- **Windows açılmazsa:** açılışta 3 kez kapat-aç → Gelişmiş başlangıç → Başlangıç Ayarları → **Güvenli Mod** →
  yukarıdaki `pnputil /delete-driver` satırları. O da olmazsa Sistem Geri Yükleme (C0'daki nokta).

### MSIX'i kaldırma

- Ayarlar → Uygulamalar → NitroDrop → Kaldır, ya da `Get-AppxPackage Nitrobyte.NitroDrop | Remove-AppxPackage`.
- Sertifikayı da silmek istersen (yönetici):
  `Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=Nitrobyte' | Remove-Item`
- Ayarlar ve eşleşmeler `%LOCALAPPDATA%\airdrop-win`'de kalır. Sıfırdan başlamak için o klasörü sil.

---

## Bana ne göndereceksin

Hepsini tek bir zip'e koy (`Win + R` → `%LOCALAPPDATA%\airdrop-win` ile klasör açılır):

| Ne | Nerede | Ne zaman |
|---|---|---|
| Uygulama verisi ve loglar | `%LOCALAPPDATA%\airdrop-win\*.log`, `%LOCALAPPDATA%\airdrop-win\logs\` (radio.log) | her sorun |
| Kurulum sihirbazı günlüğü | `%LOCALAPPDATA%\airdrop-win\setup.log`, `setup.json` | Bölüm C |
| PhoneLine teşhisi | `%LOCALAPPDATA%\airdrop-win\phoneline-probe.log` (B6'daki komutla) | arama sesi |
| Radyo yakalaması | `C:\NitroDrop\tools\*.pcap` + adwifi-cli çıktısı (metin) | C4–C6 |
| Sürücü kurulum logu | `C:\Windows\INF\setupapi.dev.log` | C3 |
| Çökme dökümü | `C:\Windows\MEMORY.DMP`, `C:\Windows\Minidump\` | mavi ekran |
| Uygulama çökmesi | Olay Görüntüleyicisi → Windows Günlükleri → Uygulama (".NET Runtime", "Application Error") | NitroDrop kapanırsa |
| MSIX kurulum hatası | Olay Görüntüleyicisi → Uygulama ve Hizmet Günlükleri → Microsoft → Windows → AppXDeploymentServer → Operational | A1 |
| Bluetooth | Olay Görüntüleyicisi → Windows Günlükleri → Sistem (BTHUSB, BthEnum) | B2 |
| Ekran görüntüleri | ilgili ekranlar, hata mesajları | her sorun |

`%LOCALAPPDATA%\airdrop-win` içinde `tls-identity.pfx` ve `sync-peers.json` da var. Bunları **gönderme**,
kimlik/eşleştirme anahtarları. Sadece `.log` dosyaları ve `setup.json` yeter.

Teşekkürler, iyi sürüşler! 🏁
