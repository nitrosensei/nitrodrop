# NitroDrop — iPhone Continuity planı (bildirim, mesaj, arama)

Tarih: 2026-10-28. Hedef: Phone Link for iPhone benzeri özellikler, NitroDrop içinde, **user-mode**, sürücüsüz,
Secure Boot açık, ücretsiz dağıtım (GitHub → ileride Microsoft Store). Donanım: Intel AX203 + Intel Wireless Bluetooth.

İşaretler: **[DOĞRULANDI]** = birincil kaynak ya da bu makinede ölçüldü; **UNVERIFIED** = mantıklı ama gerçek iPhone ile
denenmedi / kaynak dolaylı. Gerçek iPhone ile henüz hiçbir şey denenmedi (bu makinede eşleşmiş iPhone yok, bkz. §8).

---

## 0. Özet — ne olur, ne olmaz

| Özellik | Yol | Fizibilite | Not |
|---|---|---|---|
| Uygulama bildirimleri (WhatsApp, Instagram, SMS, takvim…) | ANCS (BLE GATT) | **Yüksek** | Phone Link de böyle yapıyor; LE bond şart |
| Bildirime aksiyon (sil / "Yanıtla"yı değil, sadece pozitif/negatif) | ANCS Perform Notification Action | **Yüksek** | Metin yanıtı ANCS'de **yok** [DOĞRULANDI, spec] |
| Gelen arama çalması + arayan adı/numarası | ANCS (IncomingCall kategorisi) ve/veya HFP (+CLIP) | **Yüksek** (ANCS), Orta (HFP) | ANCS Title = arayan adı |
| Aramayı PC'den açma / reddetme | ANCS pozitif/negatif aksiyon; alternatif HFP `ATA` / `AT+CHUP` | **Yüksek** (ANCS) | Ses telefonda kalır (UNVERIFIED, beklenen) |
| Arama sesi PC'de (mikrofon/hoparlör) | HFP + SCO/eSCO | **Düşük / bugün yok** | SCO'yu user-mode'dan açacak public API yok; Windows inbox HF sürücüsü + kısıtlı `PhoneLine*` API'leri |
| SMS/iMessage okuma (yeni gelen) | MAP MNS (event report) + GetMessage | **Orta** | Sadece bağlıyken gelenler; geçmiş yok (Phone Link ile aynı sınır) |
| SMS/iMessage yanıtlama / yeni mesaj | MAP PushMessage | **Orta** | iOS hangi kanaldan (SMS/iMessage) gideceğine kendisi karar verir (UNVERIFIED) |
| Grup mesajı, fotoğraf/video (MMS) | MAP | **Yok** | Phone Link de yapamıyor [DOĞRULANDI, MS destek] |
| Rehber / arayan adı | PBAP 1.2 | **Orta-Yüksek** | "Kişileri Eşitle" açık olmalı |
| Medya (şu an çalan + oynat/duraklat) | AMS (BLE GATT) | **Yüksek** (bonus) | ANCS ile aynı bond |
| WhatsApp/Instagram'a PC'den yanıt | ANCS'den değil → PC uygulaması/web'e "devam et" | **Yüksek** (yönlendirme) | §1.7, Nitro.Sync tablosu |
| PC → iPhone fare/klavye (HID) | HID-over-GATT, `GattServiceProvider` | **Deneysel** | §6.1 |
| iPhone ekranını PC'de göster | AirPlay alıcı (UxPlay) | **Teknik olarak var, lisans/hukuk riski** | §6.2 |

---

## 1. Bildirimler — ANCS (Apple Notification Center Service)

Kaynak: Apple, *ANCS Specification* —
<https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleNotificationCenterServiceSpecification/Specification/Specification.html>

### 1.1 Roller
- iPhone = **GATT server** (ANCS hizmeti onun üstünde), "Notification Provider".
- PC = **GATT client**, "Notification Consumer". Bağlantı katmanında kimin central olduğu önemli değil: GATT client rolü
  bağlantı yönünden bağımsızdır. iPhone PC'ye bağlansa bile PC aynı link üzerinden iPhone'un GATT tablosunu keşfedebilir.
- iOS ANCS'i **yalnızca şifreli (bond'lu) bağlantıda** gösterir. Bond yoksa hizmet ya listelenmez ya da
  karakteristiklere erişim "Insufficient Authentication" verir ve bu da eşleşmeyi tetikler [DOĞRULANDI: Apple spec
  "the NP must be paired/bonded"; NXP örneği "Accessing ANCS requires LE security" —
  <https://mcuxpresso.nxp.com/mcuxsdk/latest/html/examples/wireless_examples/bluetooth/ancs_c/readme.html>].

### 1.2 UUID'ler [DOĞRULANDI, spec]
| Öğe | UUID | Özellik |
|---|---|---|
| ANCS service | `7905F431-B5CE-4E99-A40F-4B1E122D00D0` | |
| Notification Source | `9FBF120D-6301-42D9-8C58-25E699A21DBD` | notify, 8 bayt olay |
| Control Point | `69D1D8F3-45E1-49A8-9821-9BBDFDAAD9D9` | write with response |
| Data Source | `22EAC6E9-24D6-4BB5-BE44-B36ACE7C7BFB` | notify, cevaplar (parçalı) |

Kodda: `app/Nitro.Continuity/Ancs/AncsProtocol.cs`.

### 1.3 Akış
1. Önce **Data Source**'a, sonra **Notification Source**'a abone ol (CCCD = Notify). Spec bu sırayı öneriyor; aksi halde
   ilk cevaplar kaçar.
2. Abone olunca iOS mevcut tüm bildirimleri `PreExisting` bayrağıyla yollar (yeniden bağlanışta da).
3. Notification Source olayı: `EventID(1) EventFlags(1) CategoryID(1) CategoryCount(1) NotificationUID(4 LE)`.
4. Detay için Control Point'e `GetNotificationAttributes`: `00 | UID(4) | {AttrID [MaxLen(2)]}*` —
   Title(1)/Subtitle(2)/Message(3) max-uzunluk ister, diğerleri istemez.
5. Cevap Data Source'tan **MTU boyunda parçalar** halinde gelir, uzunluk öneki yoktur: client hangi öznitelikleri istediğini
   bilerek cevabın bittiğini anlar → `AncsDataSourceAssembler`.
6. Uygulama adı için `GetAppAttributes(appId, DisplayName)` (uygulama başına bir kez önbelleklenir).
7. Aksiyon: `PerformNotificationAction`: `02 | UID(4) | ActionID(1)`; 0 = Positive, 1 = Negative.
   Bayraklarda `PositiveAction`/`NegativeAction` (iOS 8+) ve etiketler `PositiveActionLabel`/`NegativeActionLabel`.
   - **Gelen arama (Category=IncomingCall): Positive = Cevapla, Negative = Reddet** (etiketler iOS'tan "Answer"/"Decline"
     gibi gelir; UNVERIFIED Türkçe iOS'ta yerelleştirilmiş gelir).
   - Diğer bildirimlerde Negative genelde "Clear/Sil".

### 1.4 Metin yanıtı yok
ANCS'de komutlar yalnızca `GetNotificationAttributes`, `GetAppAttributes`, `PerformNotificationAction`; ActionID yalnızca
Positive/Negative [DOĞRULANDI, spec §"Perform Notification Action"]. "Yanıtla" + metin gönderme **ANCS ile imkânsız**.
Yanıtlar: SMS/iMessage → MAP (§2); WhatsApp/Instagram/Telegram → PC'deki uygulamaya/web'e "devam et" (§1.7).

### 1.5 Windows tarafı (unpackaged .NET 10, WinRT)
- TFM `net10.0-windows10.0.22621.0` ile WinRT projeksiyonu gelir; masaüstü (paketsiz) uygulamada Bluetooth API'leri
  capability bildirimi olmadan çalışır (probe bunu kanıtlıyor, §8).
- Bağlantı: `BluetoothLEDevice.FromIdAsync` / `FromBluetoothAddressAsync` → `GetGattServicesForUuidAsync(AncsUuids.Service, Uncached)`
  → karakteristikler → `WriteClientCharacteristicConfigurationDescriptorAsync(Notify)` → `ValueChanged`.
- Bond: `device.DeviceInformation.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | ConfirmPinMatch | DisplayPin,
  DevicePairingProtectionLevel.Encryption)` (`PairingRequested` olayında `args.Accept()` + kullanıcıya PIN göster).
  Alternatif: korumalı karakteristiğe okuma/yazma (`GattSession.MaintainConnection = true`) işletim sisteminin
  eşleşmeyi kendisi başlatmasına yol açar.
- `GattSession.FromDeviceIdAsync(...).MaintainConnection = true` — Windows aksi halde boştaki LE linkini düşürür.

### 1.6 Kritik sorun: PC iPhone'a LE üstünden nasıl ulaşır? (bağlantı prosedürü)

Gerçekler:
- iPhone'u iOS Ayarlar > Bluetooth listesinde bir LE cihazın görünmesi için o cihazın reklamında **ANCS'i "solicit"**
  etmesi (AD tipi 0x15) ya da HID gibi bir hizmet sunması gerekir — ancs4linux bu yolu kullanıyor (BlueZ ile
  `SolicitUUIDs`), kullanıcı iPhone'da PC'ye dokunur, iPhone bağlanır ve eşleşir
  [<https://github.com/pzmarzly/ancs4linux>, TI notu: "soliciting a service will cause the peripheral to appear in iOS settings"].
- **Windows `BluetoothLEAdvertisementPublisher` solicitation AD tiplerini (0x14, 0x15, 0x1F) yasaklıyor**; Flags, servis
  UUID listeleri, Local Name, Service Data da yasak; yalnızca 0xFF (manufacturer) ve rezerve olmayan tipler serbest
  [DOĞRULANDI: <https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher>].
  Yani ancs4linux yöntemi Windows'ta birebir **yapılamaz**.
- `GattServiceProvider.StartAdvertising(IsDiscoverable, IsConnectable)` yalnızca *barındırılan* hizmetin UUID'sini reklama
  koyar (solicitation değil) [DOĞRULANDI: <https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server>].
- Phone Link bu sorunu iPhone'da QR + "Link to Windows" iOS uygulamasıyla çözüyor (iOS uygulaması CoreBluetooth ile
  bootstrap eder) [UNVERIFIED iç işleyiş; QR ile eşleme kesin]. Bizim iOS uygulamamız yok (App Store ücretli/kapsam dışı).

Önerilen prosedür (sırayla denenecek; biri tutması yeterli):

**A — Klasik eşleştirme + CTKD (birincil yol, sıfır ek kod):**
1. iPhone'da Ayarlar > Bluetooth ekranı açık (iPhone keşfedilebilir olur).
2. PC'de NitroDrop "iPhone'u bağla" → Windows klasik eşleşme (`BluetoothDevice` + `DeviceInformationPairing.Custom.PairAsync`,
   ya da Windows Ayarlar > Cihaz ekle). Her iki tarafta 6 haneli kod onayı.
3. iOS dual-mode cihazlarda Cross-Transport Key Derivation ile LE anahtarlarını (LTK + IRK) da türetir (UNVERIFIED iOS
   tarafının CTKD'yi PC'ye uyguladığı). IRK sayesinde Windows iPhone'un dönen LE rastgele adresini çözer ve
   `BluetoothLEDevice` aynı cihaz kimliğiyle görünür.
4. `BluetoothLEDevice.FromBluetoothAddressAsync(publicAddress)` / LE seçicisiyle bul → GATT → ANCS.
5. iPhone'da PC'nin (i) ekranında **"Sistem Bildirimlerini Paylaş" (Share System Notifications)**, **"Bildirimleri Göster"
   (Show Notifications, MAP)** ve **"Kişileri Eşitle" (Sync Contacts, PBAP)** anahtarları açılır
   [DOĞRULANDI: Phone Link kılavuzları bu üç anahtarı istiyor — <https://learn.microsoft.com/en-us/answers/questions/5835931/phone-link-iphone-text-notifications-no-longer-wor>].
   "Share System Notifications" = ANCS izni; kapalıysa ANCS boş/erişilemez.

**B — Windows central olarak LE'den doğrudan bağlanıp LE eşleşmesi:**
iPhone sürekli Apple manufacturer-data (Nearby/Continuity) reklamı yapar ve bunların bir kısmı bağlanabilir (UNVERIFIED).
`BluetoothLEAdvertisementWatcher` ile 0x004C üreticili ve RSSI'si en yüksek cihazı yakala → `FromBluetoothAddressAsync`
→ `GetGattServicesAsync` → ANCS karakteristiğine eriş → iOS "Bluetooth Eşleme İsteği" penceresi açar → onay. Zorluk: bond
öncesi adres ~15 dk'da bir döner ve hangi reklamın "bu iPhone" olduğu belirsizdir (AirDrop BLE kodumuzdaki 0x004C ayrıştırma
yardımcı olur). Stack Overflow tarifi bu yönü öneriyor: "Windows device needs to discover the ANCS service … reading the
protected characteristic will trigger pairing" (UNVERIFIED, forum).

**C — HID-over-GATT yemi (deneysel):** PC `GattServiceProvider` ile 0x1812 HID hizmeti yayınlar (HID Microsoft'un
yasak listesinde değil, §6.1); iOS HID cihazlarını Ayarlar'da listeler → kullanıcı PC'ye dokunur → iPhone central olarak
bağlanıp LE bond kurar → aynı link üstünden PC GATT client olarak ANCS'e abone olur. Bir geliştirici Windows'tan sanal BLE
klavyesinin iPad'de yok sayıldığını bildirmiş (UNVERIFIED) — o yüzden C yedek.

Bağlantı kalıcılığı: bond sonrası iPhone PC'yi hatırlar; NitroDrop açılışta paired LE cihazı `FromIdAsync` ile açıp
`MaintainConnection` ile tutar; menzil dışından dönünce `ConnectionStatusChanged` ile yeniden abone olur (iOS tekrar
PreExisting yollar → `AncsNotificationCenter.Reset()`).

### 1.7 "PC'de devam et" (Nitro.Sync ile)
`PhoneNotification.AppIdentifier` (bundle id: `net.whatsapp.WhatsApp`, `com.burbn.instagram`, `com.apple.MobileSMS`,
`com.apple.mobilesafari`, `ph.telegra.Telegraph`…) ve `PhoneNotification.Sender` (= Title; mesajlaşma uygulamalarında
gönderen, aramada arayan) temiz biçimde sunuluyor. Nitro.Sync'in bundle-id → PC hedefi tablosu tıklamayı yönlendirir:
WhatsApp → WhatsApp Desktop (`whatsapp://`) / web.whatsapp.com; Instagram → instagram.com/direct/inbox;
`com.apple.MobileSMS` → bizim MAP mesaj paneli (Sender ile konuşma eşleşir); Safari → Handoff yok, sadece başlık/URL metni.
ANCS bildirimin derin bağlantısını (thread id, URL) **vermez** — yalnızca metin; bu yüzden hedef "uygulamayı aç + gönderen
adıyla arama" seviyesinde kalır.

### 1.8 AMS (bonus)
`89D3502B-0F36-433A-8EF4-C502AD55F8DC`; Remote Command `9B3C81D8-…`, Entity Update `2F7CABCE-…`, Entity Attribute `C6B2F38C-…`
(<https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleMediaService_Reference/Specification/Specification.html>).
Entity Update'e `[Track, Artist, Album, Title, Duration]` yazıp abone olunur; Remote Command'a tek bayt komut (Play=0, Pause=1,
Toggle=2, Next=3…). Windows'ta GSMTC (medya kontrol kutusu) entegrasyonu güzel bir ek. Kod: `Ancs/AmsProtocol.cs`.

---

## 2. Mesajlar — MAP (Message Access Profile)

- iOS **MAP 1.4** destekler; Apple'ın listesi: gelen mesaj bildirimi, yanıtlama, yeni mesaj yazma, gelen kutusunu gezme,
  okundu işaretleme [DOĞRULANDI: <https://support.apple.com/HT204387>]. Apple bunu "connected vehicles" için listeliyor;
  PC'yi de kabul ettiği Phone Link'ten biliniyor (Phone Link'in MAP kullandığı UNVERIFIED ama gözlenen sınırlar MAP'e
  birebir uyuyor).
- iPhone = **MSE**, MAS hizmeti SDP `0x1132` (RFCOMM; MAP 1.2+ L2CAP/GOEP 2.0 de olabilir). PC = **MCE**.
- Windows: `BluetoothDevice.GetRfcommServicesForIdAsync(RfcommServiceId.FromShortId(0x1132), Uncached)` →
  `StreamSocket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication)`.
  Probe bu makinede klasik cihazın RFCOMM hizmetlerini (HFP HF dahil) uygulamaya gösterdiğini doğruladı (§8) — Windows
  profil UUID'lerini gizlemiyor. iPhone ile aynı test yapılmalı.
- OBEX: `Connect(Target = BB582B40-420C-11DB-B0DE-0800200C9A66)` → ConnectionId → `SetPath` "telecom" / "msg" / "inbox"
  → `GET x-bt/MAP-msg-listing` → `GET x-bt/message` (bMessage) → `PUT x-bt/message` "outbox" (gönderme).
  Kod: `Obex/Obex.cs`, `Obex/ObexAppParameters.cs`, `Map/MapProtocol.cs`.
- **Anlık bildirim (MNS):** `PUT x-bt/MAP-NotificationRegistration` (NotificationStatus=1) → iPhone PC'deki **MNS sunucusuna**
  geri bağlanır. PC'nin `RfcommServiceProvider.CreateAsync(RfcommServiceId.FromShortId(0x1133))` ile SDP kaydı yayınlaması
  (ServiceClassIdList 0x1133, ProtocolDescriptorList L2CAP/RFCOMM kanal/OBEX, BluetoothProfileDescriptorList 0x1134 v1.4,
  ServiceName "MAP MNS", MAP 1.2+ için `MapSupportedFeatures` 0x0317) ve `StartAdvertising(StreamSocketListener)` gerekir.
  `RfcommServiceProvider` paketsiz masaüstü uygulamada çalışır (UNVERIFIED, denenecek). Gelen olay:
  `PUT x-bt/MAP-event-report` → `<event type="NewMessage" handle=… folder=… msg_type="SMS_GSM"/>` → `GetMessage(handle)`.
- **iOS sınırları** (Phone Link'te gözlenen, MS destek sayfası
  <https://support.microsoft.com/en-us/help/4539634/troubleshooting-for-messages-in-the-your-phone-app>):
  - Yalnızca PC bağlıyken gönderilen/alınan mesajlar görünür → iOS MAP geçmişi listelemiyor (yeni mesaj olayları + o
    mesajlar). Telefondan gönderilenler de çoğu zaman gelmiyor.
  - Grup sohbeti yok; grup mesajları bireysel sohbette görünür, yanıt bireye gider.
  - Fotoğraf/video/MMS yok; iMessage ile SMS ayırt edilemez (hepsi gri balon).
  - "Bildirimleri Göster" kapalıysa MAP kapalı.
  - 2013 tarihli Apple listesi: iOS 6 outbox'a gönderimde "Service Unavailable" (eski) — bugün Phone Link yanıtlayabildiği
    için gönderim çalışıyor; hangi `TYPE` (SMS_GSM) ile kabul ettiği UNVERIFIED.
- PBAP (`0x112F`, Target `796135F0-F0C5-11D8-0966-0800200C9A66`, `telecom/pb.vcf`) ile rehber → arayan/gönderen adı.
  Mevcut `AirDrop.Core/Contacts/VCard.cs` ayrıştırıcısı yeniden kullanılabilir.

---

## 3. Aramalar — HFP (Hands-Free Profile)

- iOS = **AG** (`0x111F`), HFP 1.8 [DOĞRULANDI: HT204387]. PC = **HF** (`0x111E`).
- AT kanalı: RFCOMM üstünde SLC — `AT+BRSF` → `AT+CIND=?` → `AT+CIND?` → `AT+CMER=3,0,0,1` → `AT+CLIP=1`; sonra `RING`,
  `+CLIP: "num",145,,,"ad"`, `+CIEV: callsetup/call`, `ATA` (cevapla), `AT+CHUP` (kapat/reddet), `ATD<no>;` (ara).
  Kod: `Hfp/HfpAt.cs` (`AtResultParser`, `HfpSession`).
- **Çakışma:** Windows'un inbox Bluetooth yığını telefonlar için HF rolünü kendisi üstlenir — Windows 10 1903 ile gelen
  `Windows.ApplicationModel.Calls.PhoneLineTransportDevice` ("currently only supported for use with bluetooth devices")
  ve Intel Unison kılavuzunun aramalar için "Microsoft Handsfree driver" kullandığını söylemesi bunu gösteriyor
  [<https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice>,
  <https://community.intel.com/cipcp26785/attachments/cipcp26785/unison-app/461/1/Intel_Unison_UserGuide.pdf>].
  iPhone cihaz başına tek HFP SLC kabul eder; Windows'un HF sürücüsü bağlıysa bizim RFCOMM bağlantımız ya reddedilir ya da
  Windows'unkini düşürür (UNVERIFIED). Ayrıca Windows'un AG hizmetini uygulamalara RFCOMM olarak gösterip göstermediği
  iPhone ile probe'la kontrol edilecek.
- **Public API:** `PhoneLineTransportDevice.RegisterApp/Connect`, `PhoneCallManager`, `PhoneLine` — `phoneCallHistory`/
  `phoneCallHistorySystem` capability'leri istiyor; bunlar **kısıtlı (restricted)** capability'ler, paket kimliği (MSIX) ve
  Store onayı gerektirir (UNVERIFIED: tam liste; `phoneLineTransportManagement` adı kaynakta bulunamadı). Paketsiz
  uygulama bunları kullanamaz. → Faz 3'te sparse package + Store başvurusu ile yeniden değerlendirilir.
  **Düzeltme (§9):** doğru capability `rescap:phoneLineTransportManagement` (+ `uap:phoneCall`); sideload'da onay
  gerekmez; Microsoft'a özel değil. Ayrıntı ve probe: §9.
- **Ses (SCO/eSCO):** User-mode'dan SCO linki açacak public Win32/WinRT API **yok** (UNVERIFIED ama bilinen bir API yok;
  Windows SCO'yu yalnızca kendi HF/AG ses sürücüleri — BthHFAud — için açar). Phone Link sesi Windows'un inbox HF
  sürücüsüyle PC'ye alıyor (UNVERIFIED). → **NitroDrop'ta arama sesi PC'de olmayacak** (Faz 1-2).
- **Önerilen arama yolu:** ANCS IncomingCall bildirimi → PC'de çalan tost (Cevapla/Reddet) → Positive/Negative aksiyon.
  SCO açılmadığı için iOS sesi telefonda (alıcı hoparlör) tutar (UNVERIFIED — iPhone, kendi seçimi olarak bağlı HF varsa
  sesi oraya da yönlendirebilir; Windows'un inbox HF'si bağlıysa ses PC'ye gidebilir, bu "bedava" bir kazanç olur).
  HFP AT kanalımız yalnızca Windows HF'si bağlı değilse ve kullanıcı isterse: arayan numarası (+CLIP), PC'den numara çevirme
  (`ATD`), `AT+CLCC` ile çağrı listesi.

---

## 4. Birlikte çalışma

- **Phone Link bu makinede kurulu** (`Microsoft.YourPhone` 0.26082.121.0). Aynı iPhone hem Phone Link hem NitroDrop ile:
  - ANCS: iki uygulama aynı LE link ve aynı GATT oturumunu paylaşır. Windows CCCD aboneliğini çoğullar (UNVERIFIED), ama
    **Data Source cevapları bağlantıya özeldir**: Phone Link'in `GetNotificationAttributes` cevapları bizim
    assembler'ımıza da düşer ve uzunluk öneki olmadığı için akış kayar. → Kural: NitroDrop iPhone için **tek tüketici**
    olmalı; Phone Link'te iPhone bağlıysa kullanıcıya "Phone Link'te bu iPhone'un bağlantısını kaldırın" uyarısı.
    `AncsDataSourceAssembler` beklenmedik komut kimliğinde `FormatException` atar → üst katman `Reset()` + uyarı.
  - MAP/PBAP: iOS MAS örneği başına tek MCE kabul eder (UNVERIFIED) → Phone Link bağlıysa bizim Connect `ServiceUnavailable`
    alır.
  - HFP: tek SLC (yukarıda).
- **AirDrop BLE reklamı:** `BluetoothLEAdvertisementPublisher` (0x004C manufacturer data) ayrı bir reklam seti; GATT client
  bağlantısı ve (C yolunda) `GattServiceProvider` reklamı ile birlikte yaşar. AX203 genişletilmiş reklam destekliyor
  (probe: `ExtendedAdv=True`, `MaxAdvData=160`), çoklu reklam seti donanımca mümkün; Windows reklam kaynağını "best effort"
  paylaştırır [DOĞRULANDI, publisher remarks]. Wi-Fi/BT birlikte çalışma (aynı AX203 kombo kart) AWDL ile birlikte ileride
  ölçülmeli.

---

## 5. Mimari (NitroDrop içinde)

```
app/
  Nitro.Continuity/            (net10.0, saf protokol, WinRT yok — testlenebilir)  ← BU TURDA YAZILDI
    Ancs/AncsProtocol.cs       UUID'ler, enum'lar
    Ancs/AncsCodec.cs          olay ayrıştırma, komut kurucular, Data Source birleştirici
    Ancs/AncsNotificationCenter.cs  durum makinesi (Added/Modified/Removed, aksiyon), PhoneNotification modeli
    Ancs/AmsProtocol.cs        AMS sabitleri + Entity Update
    Obex/Obex.cs, ObexAppParameters.cs  OBEX paket/başlık codec'i
    Map/MapProtocol.cs         MAS/MNS/PBAP UUID'leri, istek kurucular, listing/event XML, bMessage
    Hfp/HfpAt.cs               AT sonuç ayrıştırıcı, SLC + çağrı durumu
    Bluetooth/BluetoothUuids.cs
  Nitro.Continuity.Windows/    (sonraki faz; net10.0-windows) WinRT taşıma katmanı:
    AncsGattClient            BluetoothLEDevice + GattSession + abonelik → AncsNotificationCenter
    IPhonePairing             klasik/LE eşleşme sihirbazı (PairAsync, PIN gösterimi)
    MapClient / MnsServer     StreamSocket (RFCOMM) + RfcommServiceProvider (0x1133)
    PbapClient, HfpClient
  Nitro.Continuity.Probe/      salt-okunur teşhis aracı  ← BU TURDA YAZILDI
  AirDrop.App (UI, ileride):
    Arama tostu (tam ekran üstü küçük pencere): arayan adı/numarası, Cevapla / Reddet → ANCS aksiyon
    Bildirim merkezi paneli: uygulama simgesi (bundle id → PC ikon tablosu), Sender, metin, Sil; tıklayınca Nitro.Sync yönlendirmesi
    Mesajlar paneli: MAP konuşmaları (Sender/numara ile gruplanmış), yazma kutusu → PushMessage
    Ayarlar > iPhone: eşleşme sihirbazı, iPhone'daki 3 anahtarın açıklaması, Phone Link çakışma uyarısı
    Windows toast'ları: Microsoft.Windows.AppNotifications (paketsiz: AUMID kaydı ile) — aksiyon butonlu
```

### Fazlar
1. **Faz 1 — Bildirimler + arama çalması/cevap/ret (ANCS):** eşleşme prosedürü A → B → C denemesi, AncsGattClient,
   tostlar. En değerli, en düşük risk. (Gerçek iPhone ile ilk iş: probe + prosedür A.)
2. **Faz 2 — Mesajlar (MAP + MNS) + rehber (PBAP):** okuma/yanıt, Sender eşleştirme, "PC'de devam et" tablosu (Nitro.Sync).
3. **Faz 3 — HFP AT (Windows HF'si boşsa) + medya (AMS)**; Store/MSIX ile `PhoneLine*` API'lerinin başvurusu.
4. **Faz 4 (araştırma):** HID (§6.1), AirPlay alıcı (§6.2, ayrı süreç/lisans kararıyla).

### Riskler
- iOS'un CTKD'yi PC'ye uygulamaması → LE bond için B/C yolları; C'yi iOS yok sayabilir. **En büyük teknik risk.**
- Windows'un LE linkini/GATT oturumunu uyku-uyanmada düşürmesi; yeniden abonelik mantığı şart.
- Phone Link ile ANCS Data Source çakışması (tek tüketici kuralı).
- Apple'ın ANCS/MAP davranışını değiştirmesi (Nordic: "Apple can change the server implementations at any time" —
  <https://nrfconnectdocs.nordicsemi.com/ncs/latest/nrf/libraries/bluetooth/services/ancs_client.html>).
- Gizlilik: 2FA SMS kodları PC'ye düşer — ayarlarda uyarı, varsayılan olarak bildirim önizlemesi kilit ekranında gizli.

### Dürüst liste — OLMAYACAK
- Arama sesi PC'de (mikrofon/hoparlör) — Faz 1-2'de yok; ancak Windows inbox HF bağlıysa Windows'un kendisi yapabilir.
- Mesaj geçmişi (PC bağlı değilken gelenler), grup sohbetleri, fotoğraf/video/MMS, iMessage tapback/efektleri, okundu/yazıyor.
- WhatsApp/Instagram vb. bildirimlerine PC'den metinle yanıt (ANCS'de yok) — ancak PC uygulamasına yönlendirme.
- Bildirimin derin bağlantısı (belirli sohbeti açma), bildirim resmi/ekleri.
- FaceTime, Handoff, Universal Clipboard (Apple'ın iCloud/AWDL tabanlı sistemleri; ortak pano zaten kendi yöntemimizle var).
- iPhone'da bizim uygulamamız olmadan "tek tık QR eşleşme" (Phone Link'in QR akışı iOS uygulamasına dayanıyor).

---

## 6. Ek araştırma

### 6.1 PC → iPhone HID (fare/klavye; iOS AssistiveTouch işaretçisi)
- Microsoft'un GATT server yasak listesi yalnızca DIS, GATT, GAP, Scan Parameters; **HID (0x1812) listede yok**
  [DOĞRULANDI: <https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server>]. Ancak HID-over-GATT
  profili HID hizmetiyle birlikte **Device Information (0x180A, PnP ID)** ister ve DIS yasak — Windows kendi DIS'ini sunar
  (sistem değerleriyle); iOS'un buna takılıp takılmadığı UNVERIFIED.
- Bir geliştirici Windows tabanlı sanal BLE klavyesinin iPad'de yok sayıldığını (CCCD'ye abone olunmadığını) bildirmiş
  [UNVERIFIED, <https://origin-devforums.apple.com/forums/profile/JackBachey>]. Raporlar: Report Map, Report (input,
  notify, şifreli okuma), HID Information, Control Point, (opsiyonel) Boot Keyboard/Mouse, Protocol Mode.
- iOS'un HID listesine girmesi için reklamda HID UUID + Appearance (0x19, yasak!) gerekebilir; `GattServiceProvider`
  UUID'yi ekler ama Appearance koyamayız. → **Fizibilite: deneysel, %30-50.** Kernel gerekmez; denemesi ucuz
  (1-2 gün prototip). Yan fayda: §1.6-C bağlantı yemi.
- Klasik BT HID (L2CAP PSM 0x11/0x13) user-mode'dan sunulamaz (Windows L2CAP sunucu API'si yok) → yalnızca LE.
- Lisans: kendi kodumuz, sorun yok.

### 6.2 AirPlay ekran yansıtma alıcısı
- **UxPlay** (FDH2/UxPlay): AirPlay Mirror + AirPlay ses (ALAC) alıcısı, Windows'ta MSYS2/MinGW-64 ile derleniyor, Windows
  10/11'de test edilmiş; **GPL-3.0-or-later**; RPiPlay → AirplayServer, shairplay, playfair kökenli; AirPlay 2 video
  akışı (YouTube vb. "cast") yok [<https://snapcraft.io/uxplay>, <https://github.com/FDH2/UxPlay>].
  RPiPlay da GPL-3.0. Medya için GStreamer (LGPL) gerekir. mDNS: Bonjour/`dnssd` (bizde kendi mDNS'imiz var).
- **FairPlay el sıkışması:** `playfair` modülü Apple'ın FairPlay anahtar kurulumunu tersine mühendislikle taklit eder
  (sabit tablolar). Hukuki durum: telif/anti-circumvention (ABD DMCA §1201; AB/TR'de birlikte çalışabilirlik istisnaları
  daha geniş) açısından gri alan; Apple bugüne kadar shairport/UxPlay'e karşı dava açmadı (UNVERIFIED — dava kaydı
  bulunmadı). Ticari muadiller (AirServer, Reflector) Apple lisansı olmadan satılıyor (UNVERIFIED lisans durumu).
  **"AirPlay" Apple ticari markası** — arayüzde "AirPlay uyumlu" gibi tanımlayıcı kullanım, ürün adında asla.
- **Lisans uyumu:** GPL-3 kodu NitroDrop'a (izin verici lisans hedefi) **gömülemez**. Çözüm: UxPlay'i ayrı süreç
  (`uxplay.exe`, kaynağıyla birlikte, GPL-3 metni) olarak dağıtıp NitroDrop'tan IPC/komut satırıyla başlatmak ("aggregate"
  — GPL izin verir). Microsoft Store: GPL-3 Store şartlarıyla uyumlu kabul ediliyor ama FairPlay taklidi + Apple markası
  nedeniyle Store reddi riski yüksek (UNVERIFIED) → yalnızca GitHub sürümünde "Ek bileşen" olarak.
- Fizibilite: **teknik olarak hazır (UxPlay Windows'ta çalışıyor)**; aynı LAN (Wi-Fi) gerekir — AWDL sürücümüz gelince
  peer-to-peer da düşünülebilir. Öneri: Faz 4, opsiyonel indirilebilir modül.

---

## 7. Bu turda yazılan iskelet ve testler

- `app/Nitro.Continuity` (net10.0, `AirDrop.slnx`'e eklendi) — §5'teki dosyalar.
- `app/Nitro.Continuity.Probe` (net10.0-windows10.0.22621.0) — salt okunur: eşleşmiş klasik + LE cihazları listeler,
  seçilen cihaz için RFCOMM hizmetlerini (MAP/PBAP/HFP işaretli) ve GATT hizmetlerini (ANCS/AMS işaretli,
  karakteristikleri ve koruma seviyeleri) gösterir. Eşleşme başlatmaz, yazmaz, abone olmaz.
  `nitro-continuity-probe [n|ad|--all] [--cached]`.
- Testler (`app/AirDrop.Tests/Continuity/`): ANCS (olay, komut baytları, parçalı/çoklu Data Source cevabı, Türkçe UTF-8,
  gelen arama akışı + pozitif aksiyon, AMS), OBEX/MAP (MAS Connect tam baytları, Connect cevabı, UTF-16BE Name, SetPath,
  app-params, kesik paket reddi, akıştan çerçeveleme, msg-listing/event-report XML, bMessage LENGTH = UTF-8 bayt), HFP
  (OK/ERROR/+CME/RING/+CIEV/+CLIP/+BRSF/+CIND/+CLCC, parçalı CRLF akışı, SLC sırası + gelen→aktif→boşta).
  ANCS vektörleri Apple spec'teki alan düzenlerinden alan alan kuruldu (spec hex dökümü vermiyor).
- `dotnet test app/AirDrop.slnx`: tümü yeşil (bkz. §8).

## 8. Probe çıktısı (2026-10-28, bu dizüstü)

```
Adapter: C4:3D:1A:CD:8E:6B  Classic=True LE=True Central=True Peripheral=True
         AdvOffload=True ExtendedAdv=True MaxAdvData=160

Paired devices: 1 classic, 0 LE
  [ 0] Classic 联想thinkplus-LP1S                   41:42:01:ac:42:b6 connected=False

Classic  联想thinkplus-LP1S          (--cached)
  Address 41:42:01:AC:42:B6  Status=Disconnected  CoD major=AudioVideo minor=ComputerDesktop raw=0x240404
  Pairing: IsPaired=True ProtectionLevel=Encryption
  RFCOMM services (Cached): Success, 1 found
    HFP Hands-Free (HF)              Bluetooth#Bluetoothc4:3d:1a:cd:8e:6b-41:42:01:ac:42:b6#RFCOMM:00000000:{0000111e-…}
  LE: no LE device object for this address (iPhone LE normally uses a resolvable private address).
(Uncached: cihaz kapalı olduğu için 0 hizmet.)
```

Sonuç: eşleşmiş iPhone yok (yalnızca bir kulaklık). Çıkarımlar: adaptör central + peripheral + genişletilmiş reklam
destekliyor (§1.6-C ve AirDrop reklamı aynı anda mümkün); Windows HFP UUID'li RFCOMM hizmetlerini paketsiz uygulamaya
listeliyor. **Sıradaki adım:** iPhone'u prosedür A ile eşleştirip `nitro-continuity-probe <iPhone>` çalıştırmak — ANCS'in
listelenip listelenmediği ve MAP/PBAP/HFP AG RFCOMM hizmetlerinin görünürlüğü bütün planın ilk doğrulama noktası.

---

## 9. PhoneLine / Phone Link yolu (arama sesi PC'de, inbox HFP ile)

Tarih: 2026-10-28. Soru: SCO'yu kendimiz açamıyoruz (§3), ama Phone Link sesi PC'ye alıyor. Phone Link'in kullandığı
`Windows.ApplicationModel.Calls` API'lerini kendi (Store'suz, sideload) paketimizden kullanırsak Windows'un inbox
Hands-Free (HF) yığını bizim için de çalma/cevaplama/kapatma **ve SCO sesini** yapar mı?

### 9.1 Kısa cevap

**Muhtemelen evet — "Microsoft'a özel" bir kapı bulunamadı; gereken paket kimliği + kısıtlı capability, bunlar da sideload'da
onaysız çalışıyor.** Ama bu makinede henüz denenemedi: (a) kurulum yönetici onayı istiyor (§9.5), (b) eşleşmiş iPhone yok —
`PhoneLineTransportDevice` yalnızca HFP **AG** (`0x111F`, yani telefon) için oluşuyor, kulaklık (HF) için değil.
Hüküm: **untested-needs-approval** (+ iPhone eşleştirmesi).

### 9.2 API'ler ve capability'ler [DOĞRULANDI, birincil kaynak]

| API | Ne işe yarar | Capability |
|---|---|---|
| `PhoneLineTransportDevice.GetDeviceSelector()` + `DeviceInformation.FindAllAsync` + `FromId` | Eşleşmiş telefonun HFP transport cihazı (AQS: InterfaceClassGuid `{BD41DF2D-ADDD-4FC9-A194-B9881D2A2EFA}` + `0000111f`) | — |
| `RequestAccessAsync()` → `RegisterApp()` / `IsRegistered()` → `ConnectAsync()` | Uygulamayı o telefonun HFP "sahibi" yapar, SLC'yi Windows kurar | `rescap:phoneLineTransportManagement` (kısıtlı) |
| `PhoneCallManager.RequestStoreAsync()` → `RequestLineWatcher()` → `PhoneLine.FromIdAsync` | Hatları listeler (BT hattı `Transport=Bluetooth`) | `uap:phoneCall` (genel kullanım) |
| `PhoneLine.Dial/DialWithResultAsync`, `PhoneLine.GetAllActivePhoneCallsAsync`, `PhoneCall.AcceptIncomingAsync/RejectIncomingAsync/EndAsync/MuteAsync/ChangeAudioDeviceAsync(LocalDevice)` | Ara / cevapla / reddet / kapat / sesi PC'ye al (CallsPhoneContract v6, Win11) | `uap:phoneCall` |
| `uap13:Extension Category="windows.phoneCallActivation"` | Sistem çağrı tostundan uygulamanın aktive edilmesi (`PhoneCallActivatedEventArgs`) | manifest uzantısı |

- `phoneLineTransportManagement`: "allows apps to manage system devices responsible for phone line connectivity. This
  capability is required to use PhoneLineTransportDevice APIs" — kısıtlı listede, "won't be approved" notu **yok**
  (phoneCallHistory / backgroundVoIP'de var). <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations>
- Aynı sayfa: "you can sideload apps that declare restricted capabilities without needing to receive any approval.
  Approval is only required when submitting these apps to the Store."
- `RegisterApp` remarks: kayıt PhoneLine'ı görmek için şart, kullanıcı başına, kayıt silinene ya da cihaz kaldırılana kadar
  kalıcı. <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice.registerapp>
- `FindAllAsync` / `RequestCreateAsync` adlı üyeler **yok**; doğrusu `GetDeviceSelector` + `FromId` + `RequestAccessAsync`.
  <https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.phonelinetransportdevice>
- `phoneCallHistory*`, `lpacCom`, `voipCall`, `Calls.Background` tetikleyicileri bu senaryo için **gerekmiyor** (aşağıdaki
  çalışan uygulamalar bildirmiyor). Arka plan tetikleyicileri yalnızca uygulama kapalıyken uyanmak için — NitroDrop tepside.

### 9.3 Phone Link ve üçüncü taraflar ne yapıyor

- **Bu makinedeki Phone Link manifestinde bu capability'ler yok** — `Microsoft.YourPhone 0.26082.121.0` bir stub
  (`runFullTrust`, `unvirtualizedResources`, `Microsoft.coreAppActivation_8wekyb3d8bbwe`); arama kodu
  (`YourPhone.Calling.Managed.dll`) sonradan inen bileşende [ölçüldü: `C:\Program Files\WindowsApps\Microsoft.YourPhone_…\AppxManifest.xml`].
  Phone Link'e özel SCCD/CustomCapability kanıtı bulunmadı.
- Phone Link'te `TryUnlockPhoneLineTransportDeviceAPIs()` ve `ShouldSkipLAFCheck()` var: build **≥ 22000'de LAF kontrolü
  atlanıyor**; eski Windows'ta LAF `com.microsoft.windows.applicationmodel.phonelinetransportdevice_v1` açılıyor. Microsoft
  destek (2024-10): "PhoneLineTransportDevice API was removed from the LAF list and does not require a token".
  <https://github.com/BestOwl/MyPhone/issues/26> (yorumlar 2023-03-17, 2024-10-11, 2026-04-26). UNVERIFIED (topluluk
  tersine mühendisliği). Bu makine 28000 → LAF gerekmez.
- **Sefirah** (shrimqy, açık kaynak, Store'da): paketli WinUI, `runFullTrust` + `rescap:phoneLineTransportManagement` +
  `uap:phoneCall` + `windows.phoneCallActivation`; akış `RequestAccessAsync → RegisterApp → IsRegistered → ConnectAsync →
  PhoneLineWatcher → DialWithResultAsync → ChangeAudioDeviceAsync(LocalDevice)`. README: "View and answer phone calls …
  Remove your Bluetooth device if you have already registered with another app". Yazar (2026-04): "somewhat implement what
  Phone Link does, albeit a bit buggy". <https://github.com/shrimqy/Sefirah> (`src/Sefirah/Package.appxmanifest`,
  `Platforms/Windows/Calling/PhoneLineService.cs`, `Platforms/Windows/Utilities/CallingFeatureUtils.cs`). Android ile;
  iPhone UNVERIFIED.
- **MyPhone** (BestOwl): aynı akış, manifest yalnızca `runFullTrust` + `phoneLineTransportManagement`. Win10 21H2'de çalıştı;
  22H2'de `RequestAccessAsync=DeniedBySystem` / `RegisterApp` → `UnauthorizedAccessException` (LAF dönemi). O dönem çalışan
  diğerleri: Thy Phone (Store), Intel Unison, CallCenter, Grid 3. <https://github.com/BestOwl/MyPhone/issues/26>
- **Olumsuz rapor (2026-10, build 26300):** sparse package + `mediumIL` + `win32App`, Developer Mode ile `-ExternalLocation`;
  `RequestAccessAsync=Allowed` ama `RegisterApp()` sessiz, `IsRegistered()=false`, `RegisterAppForUser` → E_ACCESSDENIED,
  `ConnectAsync=false`; kimliksiz `DeniedBySystem`. Çözümsüz (moderatör: "Microsoft'a özel olduğu gösterilmedi").
  <https://learn.microsoft.com/en-us/answers/questions/6041632/phonelinetransportdevice-registerapp-has-no-effect>
  → **En büyük risk:** sparse/`win32App` kimliği yetmeyebilir; çalışan Sefirah/MyPhone tam MSIX. Telefon başka uygulamaya
  (Phone Link) kayıtlıysa `DeniedBySystem` — cihazı kaldırıp yeniden eşleştirmek gerekiyor (Sefirah README, MyPhone #26).
- Ses: MyPhone kendi RFCOMM SLC'siyle SCO açamadı (#27); inbox yolda sesi Windows'un HF ses sürücüsü taşıyor,
  `ChangeAudioDeviceAsync(LocalDevice)` ile PC'ye çekiliyor (Sefirah kodu). iPhone + Phone Link'te "ses PC'ye geçmiyor, HFP
  endpoint'leri aktif olmuyor" şikayeti de var: <https://learn.microsoft.com/en-ca/answers/questions/5956795/phone-link-initiates-iphone-calls-but-transfer-to>.
- Başka yol (bilgi): IBRHUB/BluetoothHFP USB dongle'ı WinUSB'ye alıp kendi yığınını çalıştırıyor, CVSD/mSBC ses doğrulanmış —
  ama sürücü değişimi + ayrı dongle; NitroDrop hedefi dışı. <https://github.com/IBRHUB/BluetoothHFP>

### 9.4 Bu makinede ölçülenler (2026-10-28) [DOĞRULANDI]

- `PhoneLineTransportDevice`, `PhoneLine`, `PhoneCallManager`, `PhoneCall` → `C:\Windows\System32\PhoneOm.dll` (in-proc,
  TrustLevel=0, her süreç aktive edebilir); yetki kontrolü PhoneSvc'de (`PhoneService.dll`: `phoneCall`,
  `PhoneCallCapabilityAccessCheck`). `PhoneProviders.dll` içinde `BluetoothHandsFreeLine` — BT hattını inbox HF sağlıyor.
- Inbox HF açık: `HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\Hfp\HandsFree` Enabled=1, ProfileVersion=0x107
  (HFP 1.7), BRSF=0xB7 (codec negotiation → mSBC). Topluluğun bulduğu `BypassRegistration` değeri yok (dokunulmadı).
- Developer Mode **kapalı** (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock` var, değeri yok).
- Probe, paketsiz:
  ```
  [Package identity]  NO package identity — running unpackaged
  [Capability access] phoneLineTransportManagement/phoneCall/phoneCallHistory/... Allowed  (paketsizde kontrol yok, anlamsız)
  [Paired classic]    联想thinkplus-LP1S  rfcomm=[111e]  (kulaklık = HF, telefon değil)
  [PhoneLineTransportDevice enumeration]  0 transport device(s)
  [PhoneCallStore]    RequestStoreAsync OK; GetDefaultLineAsync 0x8007139F; watcher: 1 hat — 'WhatsApp' Transport=VoipApp
  ```
  Hat listesi paketsiz de okunuyor; transport yok çünkü eşleşmiş telefon yok.
- Kurulum denemeleri (yönetici değil, ayar değiştirmeden):
  - `Add-AppxPackage -Path NitroPhoneLineProbe.msix -ExternalLocation … -AllowUnsigned` → **0x80073D2B** "an unsigned package
    cannot include Executable activations" (exe payload'da olmasa da Application girdisi sayılıyor; imzasız+exe = tüm
    kullanıcılar = yönetici).
  - `Add-AppxPackage -Register layout\AppxManifest.xml -ExternalLocation …` → **0x80073D2D** (Developer Mode kapalı).
  - Hiçbir şey kurulu kalmadı (`Get-AppxPackage Nitrobyte.NitroDrop.PhoneLineProbe` boş).
  - Exe'ye `<msix>` app.manifest öğesi konunca SxS "msix yayımcı geçerli olmalı" diye exe'yi hiç başlatmıyor (imzasız OID'li
    publisher kabul edilmiyor) → kaldırıldı; paketli çalıştırma AUMID üzerinden.

### 9.5 Probe: `app/Nitro.PhoneLineProbe`

- `nitro-phoneline-probe.exe` (net10.0-windows10.0.22621.0, `AirDrop.slnx`'te, uyarısız). Salt okunur: paket kimliği,
  `AppCapability.CheckAccess`, eşleşmiş klasik cihazlar (+ önbellek RFCOMM UUID'leri), transport cihazları (`FromId`,
  `Transport`, `IsRegistered`, `AudioRoutingStatus`, `InBandRingingEnabled`), `RequestAccessAsync`, `PhoneCallStore` +
  `PhoneLineWatcher` + hat özellikleri. Arama yapmaz. `RegisterApp`+`ConnectAsync` yalnızca `--register` ile (Phone Link'in
  kaydını alabilir). AUMID argüman geçemediği için exe yanındaki `probe-args.txt` de okunur. Log: exe yanında
  `phoneline-probe.log` (ya da `--log <yol>`).
- `pack.ps1`: Release build + `Package/AppxManifest.xml`'den **imzasız sparse paket** (`bin\pkg\NitroPhoneLineProbe.msix`;
  publisher `CN=Nitrobyte Dev, OID.2.25.311729368913984317654407730594956997722=1`, `mediumIL`/`win32App`; capability:
  `runFullTrust`, `phoneLineTransportManagement`, `phoneCall`, `bluetooth`). PFN `Nitrobyte.NitroDrop.PhoneLineProbe_pwgv8gmbnhssc`.

**Kurmak için gereken tek onay** (biri yeter; ikisi de yönetici ister):

- **A (önerilen — kalıcı ayar yok, sertifika yok):** yönetici PowerShell:
  `Add-AppxPackage -Path "C:\Users\Emre Can Temur\airdrop-win\app\Nitro.PhoneLineProbe\bin\pkg\NitroPhoneLineProbe.msix" -ExternalLocation "C:\Users\Emre Can Temur\airdrop-win\app\Nitro.PhoneLineProbe\bin\Release\net10.0-windows10.0.22621.0\win-x64" -AllowUnsigned`
  (imzasız + çalıştırılabilir → tüm kullanıcılara kurulur, o yüzden yönetici: <https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package>;
  sparse pakette kabul edileceği UNVERIFIED). Kaldırma (yönetici): `Get-AppxPackage -AllUsers Nitrobyte.NitroDrop.PhoneLineProbe | Remove-AppxPackage -AllUsers`.
- **B:** Developer Mode'u aç (Ayarlar → Sistem → Geliştiriciler için; kalıcı ayar), sonra yöneticisiz
  `Add-AppxPackage -Register "…\bin\pkg\layout\AppxManifest.xml" -ExternalLocation "…\win-x64"`.
- (C: kendinden imzalı sertifikayı `LocalMachine\TrustedPeople`'a almak — en kalıcı değişiklik, gerek yok.)

Çalıştırma: `explorer.exe shell:AppsFolder\Nitrobyte.NitroDrop.PhoneLineProbe_pwgv8gmbnhssc!Probe` → `…\win-x64\phoneline-probe.log`.

### 9.6 Sıradaki adım (sırayla)

1. iPhone'u Windows Ayarlar'dan eşleştir (Phone Link'e **kayıtlı değilken**; kayıtlıysa önce cihazı kaldır). Paketsiz
   `nitro-phoneline-probe` → transport görünmeli (`0000111f`) ve `RequestAccessAsync=DeniedBySystem` beklenir (taban çizgisi).
2. Seçenek A ile kur, AUMID ile çalıştır → `RequestAccessAsync=Allowed` mı? Sonra bilerek `probe-args.txt` = `--register` →
   `IsRegistered()` true, `ConnectAsync()` true mu; Windows Ses ayarlarında iPhone "Hands-Free" uç noktası beliriyor mu.
3. `IsRegistered=false` (26300 raporu gibi) çıkarsa: sparse/`win32App` yerine **tam MSIX** (exe paketin içinde,
   `packagedClassicApp`, Sefirah gibi) — imzasız+exe yine yönetici; imzalı için B ya da C.
4. Çalışırsa: NitroDrop'a `CallService` (Sefirah akışı) + `windows.phoneCallActivation`; ses `ChangeAudioDeviceAsync(LocalDevice)`.
   ANCS çalma/aksiyon yolu (§3) yedek kalır. Dağıtım: Store (kısıtlı capability gerekçesi "Bluetooth HFP telefon aramaları";
   emsal Sefirah, Thy Phone) ya da imzalı MSIX.
