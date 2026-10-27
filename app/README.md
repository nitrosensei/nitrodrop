# airdrop-win / app — AirDrop uygulama protokolü (C# .NET 10)

IP üstündeki AirDrop katmanı: mDNS/DNS-SD keşif + HTTPS `/Discover`, `/Ask`, `/Upload`.
Protokol detayları seemoo-lab/OpenDrop (GPL-3.0) `server.py`/`client.py`/`config.py` ile birebir eşlendi.
Şu an loopback/LAN üzerinde çalışır; AWDL sanal arayüzü hazır olunca `--interface <ad>` ile ona bağlanacak.

## Yapı

```
app/
  AirDrop.slnx
  AirDrop.Core/            sınıf kütüphanesi (NuGet bağımlılığı yok)
    Plist/BinaryPlist.cs   bplist00 okuyucu/yazıcı (dict, array, ASCII/UTF-16 string, int, real, bool, data, date)
    Archive/Cpio.cs        odc "070707" cpio yazıcı (akışlı) + okuyucu (odc ve newc)
    Archive/SafeExtractor.cs  güvenli çıkarma: "..", mutlak yol, sürücü harfi, ters bölü, symlink, reparse point reddi;
                              Windows'a uygunsuz adlar temizlenir (":" → "_", CON → _CON), çakışmada "ad (1).uzantı"
    Mdns/DnsMessage.cs     DNS wire formatı (isim sıkıştırma dahil)
    Mdns/MdnsService.cs    mDNS responder + browser (224.0.0.251 ve ff02::fb, port 5353), TTL'li önbellek
    Net/NetworkBinding.cs  arayüz soyutlaması: ad/index/GUID/"loopback"/"any"; link-local scope, IP(V6)_UNICAST_IF
    Http/MiniHttpServer.cs TLS üstünde küçük HTTP/1.1 sunucu (keep-alive, chunked, 100-continue)
    Security/CertificateStore.cs  ilk çalıştırmada self-signed RSA-2048 sertifika → %LOCALAPPDATA%\airdrop-win\tls-identity.pfx
    AirDropProtocol.cs     sabitler, flag'ler (varsayılan 136 = 0x08|0x80), plist anahtarları, AskRequest, UTI eşlemesi
    AirDropServer.cs       alıcı: /Discover, /Ask (AskHandler callback ile kabul/ret), /Upload (gzip+cpio → klasör)
    AirDropClient.cs       gönderici: Discover → Ask → Upload (akışlı cpio+gzip, chunked, ilerleme callback'i)
    AirDropBrowser.cs      _airdrop._tcp tarayıcı, AirDropPeer
    AirDropNode.cs         alıcı+gönderici tek uç: eş bulundu/kayboldu takibi (+ her eşe /Discover), alma aç/kapa
                           (kapalı = mDNS goodbye + boş /Discover + Ask reddi), dosya/bağlantı gönderme, yumuşatılmış hız
    MediaCapabilities.cs   ReceiverMediaCapabilities JSON: "uyumlu" ({"Version":1} → iOS HEIC→JPEG, HEVC→H.264 çevirir) / tam
    Transfer/ThroughputMeter.cs  bayt/sn EMA (~1 sn zaman sabiti) + 100 ms rapor kısma
    Contacts/ValidationRecord.cs  Sender/ReceiverRecordData: CMS SignedData (System.Formats.Asn1 ile) → plist →
                           ValidatedEmailHashes/ValidatedPhoneHashes; imza gömülü sertifikaya göre doğrulanır, Apple kökü PIN'lenmedi
    Contacts/VCard.cs      vCard 2.1/3.0/4.0 (iCloud, Google, Android): katlanmış satır, item1.TEL, QP, gömülü PHOTO / data: URI
    Contacts/ContactHashing.cs  SHA-256 aday biçimleri (e-posta küçük harf; telefon E.164 rakam, +'lı/+'sız, yerel "0…" → "90…")
    Contacts/ContactBook.cs  içe aktarılan kişiler, hash eşleme, %LOCALAPPDATA%\airdrop-win\contacts\ (index.json + photos\)
    Plist/XmlPlist.cs      XML plist okuyucu (kayıt XML gelirse)
  AirDrop.App/             WPF arayüz (lead). Services/CoreAirDropService.cs = IAirDropService'in Core üstündeki gerçek uygulaması,
                           Services/ContactBookResolver.cs = IContactResolver → ContactBook
  AirDrop.Cli/             komut satırı
  AirDrop.Tests/           xUnit testleri
```

## Çalıştırma

```powershell
cd app
dotnet build
dotnet test

$cli = "AirDrop.Cli\bin\Debug\net10.0\AirDrop.Cli.exe"
& $cli serve --dir C:\Users\me\Downloads\AirDrop          # her Ask'ta [e/H] sorar
& $cli serve --dir .\gelen --auto-accept --name "Emre PC"  # otomatik kabul
& $cli browse --timeout 10                                 # eşleri listeler + /Discover ile adlarını sorar
& $cli send <peer-id> dosya1.jpg "klasör"                  # mDNS ile bulup gönderir
& $cli send 127.0.0.1:8770 dosya.txt                       # doğrudan host:port
```

Ortak seçenekler: `--interface <ad|index|loopback|any>`, `--name`, `--mdns-port` (varsayılan 5353), `--verbose`.
`serve` ayrıca `--port` (varsayılan 8770), `--no-mdns`.

Windows'un kendi mDNS servisi 5353'ü kullanırken de çalışıyor (SO_REUSEADDR); bu makinede LAN üzerinden
IPv6 link-local ile browse → Discover → Ask → Upload uçtan uca denendi.

## Arayüz adaptörü (CoreAirDropService)

AWDL arayüzü olana kadar **LAN modu**: `IsHardwareReady=false`, varsayılan arayüz `any`; aynı ağdaki iki Windows PC
birbirini görür, dosya ve bağlantı gönderir (UI'yi uçtan uca denemek için). `ReceiveMode.Off` ve `ContactsOnly` = kapalı
(Apple imzalı kaydımız yok). Kendi eşlerimizi `/Discover` ve `/Ask`'taki ek `AirDropWinVersion` anahtarından tanıyıp
`DeviceKind.Windows` gösteriyoruz; model adımız varsayılan `MacBookPro18,3` (`CoreAirDropServiceSettings.ModelName`).
Testler adaptörü WPF olmadan derlemek için `IAirDropService.cs`, `IContactResolver.cs`, `CoreAirDropService.cs`,
`ContactBookResolver.cs` dosyalarını AirDrop.Tests'e link'ler — bu dosyalara WPF tipi girmemeli.

## Gelişmiş Kurulum sihirbazı (`AirDrop.App/Setup`, `Views/SetupWizard.xaml`)

Faz 1'de sürücümüz Microsoft imzalı olmadığı için kullanıcının Secure Boot'u kapatıp test modunu açması gerekiyor.
Sihirbaz bunu teknik olmayan kullanıcı için adım adım yaptırır. Ana pencerede kart destekliyse ve sürücü kurulu değilse
"iPhone'larla doğrudan bağlantı için kurulum gerekli → Kur" bandı çıkar; Ayarlar → **Wi-Fi Sürücüsü** bölümünde durum +
"Kur…" / "Kaldır ve eski haline getir…" var.

Adımlar (her biri açılışta durumu algılar, tamamlanmışsa atlanır; ilerleme `%LOCALAPPDATA%\airdrop-win\setup.json`):

1. **Uyumluluk** — `HKLM\SYSTEM\CurrentControlSet\Enum\PCI` + `CM_Locate_DevNode` (yalnızca takılı kart).
   Tablo: `SupportedDevices.cs` (şimdilik `PCI\VEN_8086&DEV_51F1&SUBSYS_02748086`). Kart yoksa nazikçe durur, USB adaptör yolunu anar.
2. **Onay** — Secure Boot kapanır, "Test Modu" filigranı, Valorant/Vanguard ve FACEIT gibi oyunlar açılmaz, koruma azalır,
   Intel Wi-Fi NitroDrop'a geçer. Anahtar açılmadan ilerlenmez.
3. **BitLocker** — hızlı durum yönetici izni olmadan (Shell `System.Volume.BitLockerProtection`); "Anahtarı göster…" yönetici
   yardımcısıyla WMI `Win32_EncryptableVolume`'dan anahtar kimliğini ve 48 haneli parolayı okur (parola günlüğe yazılmaz),
   dosyaya kaydet / yazdır, aka.ms/myrecoverykey bağlantısı. "Kaydettim" onayı zorunlu. "Yeniden başlatırken bir kez askıya al"
   (varsayılan açık) → BIOS ve test modu yeniden başlatmalarından önce `manage-bde -protectors -disable C: -RebootCount 1`.
4. **Secure Boot** — `...\SecureBoot\State\UEFISecureBootEnabled`. Açıksa SMBIOS'tan (`HKLM\HARDWARE\DESCRIPTION\System\BIOS`)
   üretici/model okunur, `VendorGuides.cs` tablosundan talimat gösterilir: Lenovo (ThinkPad ayrı), HP, Dell/Alienware, ASUS, Acer,
   MSI, Gigabyte, Samsung, Huawei/Honor, Casper, Monster, Clevo/Tongfang, Surface, ASRock + genel. "BIOS'a yeniden başlat" =
   onay → RunOnce kaydı → yönetici yardımcısında `shutdown /r /fw /t 5`.
5. **Test modu** — etkinlik `NtQuerySystemInformation(SystemCodeIntegrityInformation)` `TESTSIGN` bitinden (yönetici gerekmez).
   Yardımcıda `bcdedit /set {current} testsigning on`; Secure Boot açıkken bcdedit'in reddi (İngilizce/Türkçe mesaj ya da algılanan
   durum) anlaşılır bir mesaja çevrilir. Sonra onaylı `shutdown /r /t 5` (yönetici gerekmez).
6. **Kurulum** — firmware C#'ta indirilir (`FirmwareFetcher.cs`, `tools/fetch-firmware.ps1` ile aynı tag/SHA-256, git.kernel.org →
   GitLab aynası), lisans gösterilip kabul ettirilir. Yardımcı: paketi yalnızca Yöneticiler/SYSTEM'in yazabildiği
   `%ProgramData%\NitroDrop\driver`'a kopyalar, firmware hash'ini ve **kataloğu** doğrular, bu kuruluma özel RSA-3072 kod imzalama
   sertifikası üretir, `.sys` ve `.cat`'i imzalar, açık sertifikayı `LocalMachine\Root` + `TrustedPublisher`'a ekler (özel anahtar
   yalnızca bellekte, hiç diske yazılmaz), `pnputil /add-driver … /install`, ardından Intel'in WHQL sürücüsü daha iyi eşleştiği için
   `UpdateDriverForPlugAndPlayDevices(hwid, inf, INSTALLFLAG_FORCE)` ve kartın `airdropwifi` servisine bağlandığını doğrular.
7. **Kaldır ve eski haline getir** — `%windir%\INF\oem*.inf` içinden bizim INF'ler (Provider + CatalogFile, dilden bağımsız) →
   `pnputil /delete-driver oemNN.inf /uninstall /force`, `pnputil /scan-devices` (Intel sürücüsü geri bağlanır), sertifikaları
   parmak izi + konuyla siler, `testsigning off`, gerekirse `manage-bde -protectors -enable`, sonra Secure Boot'u açma talimatı.

**Katalog (WDK olmadan):** son kullanıcıda Inf2Cat/MakeCat yok. Katalog bizim derlememizde `driver\package.ps1` ile **imzasız**
üretilip uygulamayla gelir (`driver\out\airdropwifi.cat` → çıktıda `driver\`). Katalog yalnızca dosya hash'lerini tutar, imzalayandan
bağımsızdır; kullanıcının makinesinde `SignerSignEx` (mssign32, signtool'un da kullandığı API; PE ve katalog SIP'leri var) ile kuruluma
özel sertifikayla imzalanır. `.sys`'e gömülü imza eklemek kataloğu bozmaz çünkü katalogdaki hash Authenticode PE hash'idir
(sertifika tablosu hariç) — testte doğrulanıyor. INF'in `DriverVer`'i UTC'ye göre gelecekte olursa Inf2Cat reddeder.

**Yükseltme:** arayüz hiçbir zaman yönetici olarak çalışmaz. Her adım butonu aynı exe'yi `--setup-helper --pipe <ad> --request
<base64url JSON>` ile `runas` başlatır (tek UAC), yardımcı tek bir `HelperAction` yapar ve sonucu yalnızca mevcut kullanıcı +
Yöneticilere açık named pipe'tan döndürür (`ElevatedHelper.cs`, `HelperActions.cs`). Bütün sistem değişiklikleri `HelperActions`'tadır.
Yeniden başlatmadan önce `HKCU\…\RunOnce` → `NitroDrop.exe --setup-resume` yazılır, sihirbaz açılışta kaldığı yerden sürer.
Her adım ve komut `%LOCALAPPDATA%\airdrop-win\setup.log`'a yazılır (sihirbazdaki "Kurulum günlüğü").

**Komut satırı:** `--setup` (sihirbazı aç), `--setup-resume`, `--setup-dry-run` (hiçbir komut çalışmaz, yükseltme istenmez, komutlar
günlüğe yazılır; ilerleme ayrı `setup.dry-run.json`'da). Debug: `NitroDrop.exe --snapshot <klasör>` sihirbazın tüm sayfalarını açık/koyu
tema `setup-*.png` olarak da çizer; `--snapshot <klasör> --setup-dry-run` ayrıca bütün yardımcı eylemlerini dry-run çalıştırıp
`<klasör>\setup-dry-run.log` yazar.

## Testler

- bplist: tüm tipler gidiş-dönüş, CPython `plistlib` çıktısını okuma, bizim çıktımızı CPython'a okutma (`.venv` varsa)
- cpio: gidiş-dönüş, newc okuma, gzip'li çıkarma, Windows `tar.exe` (libarchive) ile çapraz doğrulama
- güvenlik: path traversal / mutlak yol / sürücü harfi / ters bölü / symlink reddi, ad temizleme, çakışma
- adaptör (iki CoreAirDropService, loopback mDNS 536xx): keşif, ilerlemeli gönderim, ret, bağlantı paylaşımı,
  Off modunda geri çekilme + Ask reddi + geri gelme, medya yetenekleri/isim güncelleme, model→cihaz türü, hız ölçer
- kişiler: iCloud/Google/v4/v2.1 vCard, telefon normalizasyonu, bplist/XML/CMS (RSA+ECDSA, bozuk imza) kayıt, eşleme + kalıcılık
- kurulum sihirbazı (`SetupTests.cs`, `Setup/` içindeki WPF'siz dosyalar link'li): PCI kimliği ayrıştırma/eşleme, adım akışı ve
  atlama, CI/SystemStartOptions/Shell BitLocker ayrıştırma, üretici tablosu (26 SMBIOS örneği), komut kurucular ve tırnaklama
  (`CommandLineToArgvW` ile gidiş-dönüş), bcdedit/pnputil sonuç sınıflandırma (EN/TR), BitLocker JSON, yardımcı protokolü, setup.json,
  INF tanıma, firmware doğrulama (ağsız), sertifika özellikleri, **geçici kopyalarda gerçek `SignerSignEx` imzası** (bellekteki anahtar,
  hiçbir depo değişmez) ve imzadan sonra hazır kataloğun geçerli kalması (sürücü + firmware derlenmişse)
- uçtan uca (loopback, süreç içi): Türkçe karakterli ad (ğüşİöç) dahil 2 dosya + klasör gönderimi, bayt bayt kontrol;
  ret durumunda Upload'un 403 alması; mDNS yayın + tarama (loopback, port 53530)

## Doğrulanmamış / yapılacaklar

Kodda `// UNVERIFIED:` ile işaretli:
- Ret (decline) durumunda Apple'ın döndürdüğü HTTP kodu (PrivateDrop gibi 406 dönüyoruz; OpenDrop hiç reddetmiyor).
- Model adı: Mac model kimliği (`MacBookPro18,3`) iOS'ta dizüstü simgesi çizdirir varsayımı.
- macOS'un tam `ReceiverMediaCapabilities` JSON'u (Vendor/Codecs/ContainerFormats anahtarları hafızadan; yalnızca
  `{"Version":1}` OpenDrop/PrivateDrop'ta kesin). Sender tarafında HEIC/HEVC dönüştürmüyoruz (codec yok), `ConvertMediaFormats=false`.
- Bağlantı paylaşımı: `Items=[url]`, dosya yok, `/Upload` yok; alıcı Ask kabulünde bağlantıyı teslim eder. TXT flags'e
  SupportsUrl (0x01) eklendi (node varsayılanı 137).
- Bilinmeyen `AirDropWinVersion` anahtarını iOS/macOS'un yok sayması.
- Kişi hash normalizasyonu: SHA-256 + küçük harf hex kesin (PrivateDrop); e-postanın küçük harfe çevrildiği ve telefonun
  "+"'sız E.164 rakamlarıyla hashlendiği varsayım — bu yüzden birden çok aday biçim üretip yalnızca kendi kişilerimizle eşliyoruz.
- iOS'un sıkıştırılmamış cpio gönderip göndermediği (ikisi de kabul ediliyor).
- AWDL arayüzünde sunucunun yalnızca link-local adrese bağlanması gerekip gerekmediği.

Yapılacak: gönderirken JPEG2000 `FileIcon` küçük resmi, "Yalnızca Kişiler" (kendi Apple imzalı kaydımız — kapsam dışı),
Apple kök CA'sını PIN'leyip kayıt zincirini doğrulamak, alırken toplam boyut (Ask'ta boyut yok → ilerleme yüzdesi yok), mDNS probing/çakışma çözümü, pipelining (flag 0x04).
Gerçek iPhone ile test AWDL katmanı hazır olunca yapılacak.

Kurulum sihirbazında doğrulanmamış olanlar (bu makinede hiçbir gerçek sistem komutu çalıştırılmadı; yalnızca derleme, testler,
dry-run ve ekran görüntüsü):
- Uçtan uca gerçek kurulum: UAC → bcdedit/shutdown/manage-bde/pnputil, sertifikanın depolara eklenmesi, test modunda PnP'nin bizim
  imzaladığımız kataloğu kabul etmesi ve `UpdateDriverForPlugAndPlayDevices(FORCE)`'un Intel sürücüsünün yerine geçmesi.
- Shell `System.Volume.BitLockerProtection` değer eşlemesi (1 açık, 2 kapalı, 3/4 sürüyor, 5 askıda, 6 kilitli) gözleme dayalı.
- Üretici talimatları genel menü yollarıdır; BIOS sürümüne göre adlar değişebilir (her sayfada "BIOS açılmazsa" tuşu ve genel ipucu var).
- HVCI/Bellek Bütünlüğü açıkken test imzalı sürücünün yüklenmesi; bu sihirbaz şimdilik Bellek Bütünlüğü'ne dokunmuyor.
- BitLocker'ın `-RebootCount 1` ile iki ayrı yeniden başlatmada (BIOS + test modu) ayrı ayrı askıya alınması — her yeniden başlatmadan
  önce tekrar çalıştırılıyor, ama gerçek TPM/PCR davranışı denenmedi.
