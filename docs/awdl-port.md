# AWDL portu: Owly → `app/Nitro.Awdl` (C#)

Durum: 2026-10-28. Kaynak: [t4t5/owly](https://github.com/t4t5/owly), commit `9a676e11d011c34ea32798a2541f2b1eeb66e731`,
MIT, "Copyright (c) 2026 T4T5". Lisans metni `app/Nitro.Awdl/LICENSE-OWLY.txt` (çıktıya da kopyalanıyor), port edilen
her dosyanın başında kaynak dosya ve telif satırı var.

**GPL sınırı:** seemoo-lab/OWL (GPL-3) koduna bakılmadı. Owly reposundaki `crates/omdrop-owl/omdrop-awdl/*`
(GPL-2 yama lisansı) de açılmadı. Tek kaynak Owly'nin MIT crate'leri (`owly-awdl`, `owly` sürücü katmanı,
`owly-airdrop/src/proto/ble.rs`) ve Owly'nin anonimleştirilmiş capture'ları (`tests/captures`).

## Mimari

```
            IAwdlRadio                 AwdlNode (sans-IO)               IPacketSink
 AdWifiRadio ──RX/TX──► AwdlHost ──Frame/Timer/Outbound──► ──Deliver/PeerUp──► WintunPacketSink (awdl0 eşi)
 SimulatedRadio          (özel thread,                                        UserModePacketSink (test/in-proc)
                          high-res timer, MMCSS)
```

`AwdlNode` Owly'deki gibi IO'suz: girdiler (çerçeve, zamanlayıcı, giden paket) mikro saniye zamanla verilir, çıktı
`Transmit/ArmTimer/PeerUp/PeerDown/Deliver` eylemleri. Bu sayede tüm protokol sanal zamanda deterministik test ediliyor.

## Modül eşlemesi

| Owly (Rust) | C# dosyası | Not |
|---|---|---|
| `owly-awdl/src/lib.rs` (Time, TU, MacAddr, BSSID, OUI, Error) | `Core/Primitives.cs`, `Core/Wire.cs` | `AwdlFormatException.Truncated` = Owly `Error::Short` |
| `owly-awdl/src/channel.rs` | `Core/Channel.cs` | 3 kodlama (simple/legacy/opclass), intersect, dominant |
| `owly-awdl/src/rate.rs` | `Core/TxRate.cs` | legacy 500 kb/s birimi, HT MCS + SGI, `ForPeer` |
| `owly-awdl/src/sync.rs` | `Core/Sync.cs` | AW 16 TU, slot 64 TU, 16 slot, guard'lar, 14-bit sayaç sarması |
| `owly-awdl/src/election.rs` | `Core/Election.cs` | MetricFirst / CounterFirst, MaxHeight 10, döngü koruması |
| `owly-awdl/src/tlv.rs` | `Core/Tlv.cs` | TLV sırası 4,5,18,24,6,7,[16],12,21; AWDL-5 dolgu kuralı |
| `owly-awdl/src/frame.rs` | `Core/Frame.cs` | PSF/MIF action, LLC/SNAP AWDL veri başlığı, A-MSDU (≤16), ADDBA yanıtı |
| `owly-awdl/src/ip.rs` | `Core/Ipv6Packet.cs` | EUI-64 ↔ MAC, SYN tespiti, ICMPv6 echo, checksum |
| `owly-awdl/src/peer.rs` | `Core/Peer.cs` | AWDL-17 geçerlilik: MIF + version + devclass |
| `owly-awdl/src/node.rs` | `Core/Node.cs` | Tam port: tick 1 s, peer timeout 5 s, echo 3 s (id 0x4f77, "owly"), SYN hold 2 s, kuyruk 128/16, MAX_PEERS 512, RSSI eşiği −65 |
| `owly/src/driver.rs` (thread, retry, backlog) | `Host/AwdlHost.cs` | unicast 2 s / multicast 100 ms retry, 500 µs bekleme; **saat farkı** aşağıda |
| `owly/src/native.rs` (TAP, MTU 1450, NUD_PERMANENT, addr_gen_mode, accept_dad=0) | `Ip/Wintun.cs` | Wintun L3 olduğu için komşu girdisi gerekmiyor; adres/MTU/DAD `ConfigurationCommands` ile |
| `owly/src/setup.rs` (iwlwifi exclusive, kanal 6, IBSS hücresi) | `Radio/AdWifiRadio.cs` + sürücü `START` | sürücü M3 IBSS = Owly'nin ad-hoc hücresi |
| (monitor arayüzü RX/TX) | `Radio/IAwdlRadio.cs`, `Interop/AirdropIoctl.cs`, `Interop/DeviceNative.cs` | ABI v2 |
| `owly-airdrop/src/proto/ble.rs` | `Ble/AirDropBleAdvert.cs`, `Ble/BleAirDropAdvertiser.cs` | v3 yükü; v1 (ble_test.py) de var |
| Owly test simülasyonu | `AirDrop.Tests/Awdl/AwdlSimulationTests.cs` (`VirtualAir`) | sanal zaman, kayıp, kanal |
| — (yeni) | `Radio/SimulatedAir.cs`, `Ip/UserModePacketSink.cs`, `Interop/TimingNative.cs` | gerçek zamanlı sim, in-proc sink, zamanlama |

Sürücü ABI'si (`driver/include/airdrop_ioctl.h`, **v2**): INFO 176 B (v1 144 öneki de okunuyor), START 24 B,
RX_HEADER 64 B (çerçeve `HeaderSize`'dan başlıyor, büyüyen başlığa dayanıklı), TX_HEADER 16 B, TX_RESULT 24 B.
`AdWifiRadio`: GUID ile cihaz bulma (cfgmgr32), overlapped `DeviceIoControl` (ThreadPoolBoundHandle), 8 bekleyen
`READ_FRAME` + veriliş sırasına göre yeniden sıralama, `INJECT_FRAME` TX_RESULT bekleyerek (uçuşta ≤64, dolunca
`TrySend=false` → host retry), kanal değişimi = STOP+START, TSF son RX damgasından ekstrapolasyon (GET_TSF yok).

**NOACK:** firmware non-QoS unicast'ta ACK'siz göndermeyi desteklemiyor (`NOACK_NOT_HONORED`). Owly düz Data
(0x0008) gönderiyor. `AdWifiRadioOptions.UpgradeNoAckDataToQos` (varsayılan açık) NOACK'li düz unicast veriyi QoS Data
(TID 0) yapıyor, sürücü Ack Policy'yi yeniden yazıyor. iPhone'ların kendisi de QoS Data gönderiyor (capture #260),
ama iOS'un bizden gelen QoS'u kabul ettiği **doğrulanmadı**. Sorun olursa kapatılır, o zaman çerçeveler ACK +
retry ile gider. Bu da Fixed modda daha yavaş ama çalışır olmalı.

## Doğrulanan / varsayılan

**Doğrulandı (test, bayt bazında):**
- Owly'nin kendi yayınladığı MIF (capture `discovery-ios26.x-mt7925e-ch44-1.pcap` #1, 287 B) bizim encoder'la
  **birebir** yeniden üretiliyor. Tek fark, capture aracının anonimleştirdiği ülke kodu ("XXX" ↔ "X0\0").
- Gerçek iPhone MIF (959 B, #12), PSF (327 B, #2) ve QoS veri çerçevesindeki TCP SYN (receive pcap #260) eksiksiz
  ayrıştırılıyor. Bilinmeyen TLV 17 ve 6 servis yanıtı atlanıyor, MIF peer'i geçerli yapıyor, PSF yapmıyor.
- Owly'nin altın veri çerçevesi baytları, EUI-64 (`86:08:04:8a:2b:f4` → `fe80::8408:4ff:fe8a:2bf4`), kanal dizisi
  kodlamaları, seçim kuralları, saat/guard hesapları, hız ayrıştırma (Owly unit testlerinin portu).
- Sanal zamanda 2 düğüm birbirini buluyor, tek master'da uzlaşıyor, slot saati 1 TU içinde hizalanıyor. 3 düğüm %20
  kayıpla uzlaşıyor. Farklı kanaldakiler buluşmuyor. Susan peer ~5 s'de düşüyor. 50 paket sırayla gidiyor. Echo
  çalışıyor. Erken gelen SYN tutulup MIF sonrası teslim ediliyor. Peer tablosu ve RSSI eşiği çalışıyor.
- **Uçtan uca:** iki gerçek zamanlı `AwdlHost` (thread + timer + host saati) `SimulatedAir` üzerinde, gerçek
  `AirDropClient` → `AirDropServer` HTTPS (Discover + Ask + Upload, 120 KB) aktarımını AWDL veri çerçeveleriyle
  taşıyor. Dosya bayt bazında eşit. Kendi link-local'ı olmayan kaynaklı paketler gönderilmiyor.
- IOCTL kodları (CTL_CODE) ve v2 yapı ofsetleri header'daki `static_assert`'lerle uyumlu. BLE v3/v1 yük düzeni doğru.

**Varsayım / doğrulanmadı:**
- Gerçek donanımda hiçbir şey koşmadı (sürücü kurulu değil, admin yok). `AdWifiRadio` ve `Wintun*` derleniyor ama
  çalıştırılmadı.
- Windows'un Wintun arayüzünde ND çözümlemesi yapmadığı (L3 tünel) ve kendi rastgele link-local'ını
  `skipassource` ile kaynak seçmediği. Host bu tür paketleri zaten düşürüyor (`OutboundForeignSource`/`NotAwdl`).
- iOS'un bizden gelen QoS Data'yı kabul etmesi (yukarıda).
- RX `HostTimestamp` (KeQueryInterruptTimePrecise) ile user-mode `QueryInterruptTimePrecise`'in aynı taban olduğu
  (dokümana göre öyle). Ölçülmedi.
- `BluetoothLEAdvertisementPublisher` aralığı/adresi seçtirmiyor. BLE-5'in istediği ~100 ms bağlanabilir reklam
  yerine Windows varsayılanı gidiyor. `ble_test.py` bu makinede v1 ile çalıştı.
- HTTPS ve mDNS'in gerçek Wintun arayüzüne bağlanması donanımda denenmedi (testlerde Wintun yerine loopback bağlanıyor,
  `RadioServiceTests`). `NitroDrop.Radio.exe`, UAC akışı ve adaptör oluşturma hiç çalıştırılmadı.

## Zamanlama

Owly Linux'ta host `Instant`'ını düğüm saati olarak kullanıyor ve timerfd ile ~50 µs hassasiyet alıyor. AWDL'nin
kritik pencereleri: AW 16 TU (16,4 ms), slot 64 TU (65,5 ms), unicast guard 3 TU (~3 ms), PSF 110 TU. Fixed modda
(Intel exclusive, hep kanal 6) kanal değiştirmediğimiz için gereken tek şey çerçeveleri slot kenarı guard'larının
dışına ve MIF'i slot ortasına düşürmek. Hata payı ~1 ms.

Bu makinede ölçüm (2026-10-28, rastgele 0,3–5 ms ileri hedefler, 2000 örnek, gecikme = uyanma − hedef):

| Yöntem | p50 | p90 | p99 | max |
|---|---|---|---|---|
| `Thread.Sleep(ms)` | 12,9 ms | 14,9 ms | 16,2 ms | 22,2 ms |
| `WaitOne(ms)` | 13,0 ms | 15,0 ms | 16,3 ms | 21,2 ms |
| High-res waitable timer | 343 µs | 693 µs | 2,6 ms | 6,6 ms |
| High-res + son 300 µs spin | 29 µs | 387 µs | 2,0 ms | 4,2 ms |
| High-res + MMCSS "Pro Audio" + Highest | 279 µs | 538 µs | 735 µs | 1,1 ms |
| Aynısı, 12 meşgul thread altında | 314 µs | 519 µs | 637 µs | 1,1 ms |
| High-res, normal öncelik, 12 meşgul thread | 6,3 ms | 7,6 ms | 10,2 ms | 13,3 ms |

Sonuç: varsayılan Windows zamanlayıcısı (~15,6 ms tick) AWDL için **kullanılamaz**. Normal öncelikli high-res timer
yük altında çöküyor. **High-res timer + MMCSS** yük altında bile p99 < 1 ms veriyor, Fixed mod için yeterli.

Uygulanan önlemler (`AwdlHost` varsayılanları):
1. `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (`PreciseWaiter`). Radyo RX ve sink olaylarıyla aynı `WaitAny`'de.
2. MMCSS "Pro Audio" + `ThreadPriority.Highest` (yönetici gerektirmez).
3. İsteğe bağlı son-µs spin (`SpinUs`, CPU yakar, varsayılan kapalı).
4. Düğüm saati = interrupt-time (µs). RX zamanı sürücünün ISR damgasından alınıyor, bu yüzden user-mode
   gecikmesi senkron hesabına (`ErrorTu`) girmiyor. Sadece TX zamanlaması jitter'dan etkileniyor.

İleride (gerekirse): TX zamanlamasını sürücüye taşımak (INJECT'e "şu TSF'de gönder" alanı ya da firmware'in
`TIME_EVENT`/NoA mekanizması). Share modu (kanal atlama) Windows user-mode'da gerçekçi değil. Ancak sürücü içi
slot zamanlayıcısıyla olur.

## Çalışma zamanı mimarisi (entegrasyon, 2026-10-28)

```
 NitroDrop.exe (kullanıcı, yükseltilmemiş)                      NitroDrop.Radio.exe (yönetici, requireAdministrator)
 ┌──────────────────────────────────────────┐  runas (1 UAC)   ┌──────────────────────────────────────────────┐
 │ CoreAirDropService                       │ ───────────────► │ RadioHostServer  (Nitro.Awdl/Ipc)             │
 │  ├ LAN AirDropNode  (any − NitroDrop AWDL)│                  │  └ AwdlRadioStack                             │
 │  ├ AWDL AirDropNode (NitroDrop AWDL)      │  pipe            │     AdWifiRadio (sürücü, kanal 6, IBSS)       │
 │  ├ BleAirDropAdvertiser                   │ ◄══════════════► │     WintunAdapter "NitroDrop AWDL" + Sink     │
 │  └ RadioHostClient                        │ NitroDrop.Radio. │     AwdlHost (RadioConstraints.IntelExclusive)│
 └──────────────────────────────────────────┘  <kullanıcı SID>   └──────────────────────────────────────────────┘
```

**Neden ayrı süreç:** sürücü cihazı yalnız SYSTEM/Administrators'a açık (INF SDDL) ve Wintun adaptörü oluşturmak admin
istiyor; WPF uygulaması yükseltilmeden çalışıyor. Yükseltilmiş tek parça `NitroDrop.Radio.exe` (`app/Nitro.Radio.Host`,
uygulamayla aynı TFM, çıktıda `NitroDrop.exe`'nin yanında). `wintun.dll` pakette **yok** (OSI dışı lisans): ilk
radyo başlangıcında uygulama "Wi-Fi bileşeni indirilecek (Wintun, WireGuard LLC)" diye sorar, onaylanırsa wintun.net'ten
indirir, zip SHA-256 + DLL SHA-256 + WireGuard LLC Authenticode imzasını doğrular ve `%LOCALAPPDATA%\airdrop-win\wintun\<arch>\`
altına koyar (`Ip/WintunInstall.cs`); host bu klasörü `--wintun-dir` ile alır ve yüklemeden hemen önce aynı denetimleri yapar.

**Boru protokolü** (`Nitro.Awdl/Ipc/RadioPipe.cs`): ad `NitroDrop.Radio.<kullanıcı SID>`, byte modu, satır başına bir JSON
(UTF-8, camelCase, enum'lar camelCase metin, bilinmeyen alan/tip yok sayılır, satır ≤256 KB).
- Host → uygulama: `{"type":"hello","version":1,"pid":…}`, `{"type":"status","status":{"state":"starting|ready|failed|stopped",
  "channel":6,"peers":[{"mac","name","rssi"}],"failure":"…","failureKind":"driverMissing|accessDenied|wintunMissing|rfKill|radio|network|other",
  "interface":"NitroDrop AWDL","interfaceIndex":…,"mac":…,"address":"fe80::…"}}`, `{"type":"log","message":"…"}`.
- Uygulama → host: `start` (`name` = AWDL ARPA adı, `listenerOnly`), `stop`, `getStatus`, `quit`.
- BLE reklamı yükseltme istemediği için uygulamada (`IAirDropBleAdvertiser`); ayrı bir `setAdvertise` komutu yok.

**Güvenlik:** host boruyu `FILE_FLAG_FIRST_PIPE_INSTANCE`, tek örnek ve korumalı DACL ile açar: yalnız `--user-sid` ile
verilen kullanıcı okuma/yazma alır (Administrators dahil başka kimse istemci olamaz; başka hesapla UAC onayında da doğru
kullanıcı). Host istemcinin PID'ini `--parent-pid` ile karşılaştırır, uygulama da sunucunun PID'ini başlattığı süreçle
(`GetNamedPipeServerProcessId`) — ad önceden kapılmışsa ikisi de reddeder.

**Ömür:** host tek bağlantıya hizmet eder. Uygulama 30 s içinde bağlanmazsa, boru kapanırsa (uygulama kapandı/çöktü/ayar
kapatıldı), `quit` gelirse ya da ebeveyn süreç ölürse: `AwdlHost` durur, sink + adaptör kapanır (Wintun kendi oluşturduğu
adaptörü siler), `AdWifiRadio.DisposeAsync` → sürücü `STOP`, süreç çıkar. Host öldürülse bile cihaz handle'ının kapanması
radyoyu durdurur. Intel kartı sahipsiz kalmaz.

**Host başlatma sırası** (`AwdlRadioStack`): `AdWifiRadio.OpenAsync` (kanal 6) → `WintunAdapter.Open()` yoksa `Create()` →
`ApplyConfiguration(radio.Address)` (fe80::EUI-64/64, MTU 1450, DAD 0, RA kapalı, diğer link-local'lar skipassource) →
`WintunPacketSink` → `AwdlHost.StartAsync` → arayüz Up + adresimiz görünene kadar ≤15 s → `ready`. Hatalar
`RadioFailureKind`'e çevrilir (FileNotFound → driverMissing, erişim/"already open" → accessDenied, DllNotFound →
wintunMissing, "RF kill" → rfKill, Wintun/netsh → network, gerisi radio). Sonra `PeerUp/PeerDown` her seferinde yeni
`status`; `AwdlHost.Failed` → `failed`.

**Uygulama tarafı** (`CoreAirDropService`, ayrıntı `app/README.md`): LAN düğümü hemen başlar; sürücü bağlıysa ve ayar
açıksa arka planda host başlatılır, `ready` gelince `NetworkBinding.Resolve("NitroDrop AWDL")` ile ikinci düğüm açılır (iki
bağlama: PC↔PC LAN'da sürer, iPhone'lar AWDL'den). UAC reddi, host hatası, zaman aşımı (60 s) ya da host kapanması →
yalnız LAN + Türkçe durum metni. BLE: AWDL hazırken alma modu Herkes ya da gönderim sürerken açık.

## Test sürüşü (ilk gerçek deneme)

Kullanıcının yapacakları:
1. **Önkoşullar:** sürücü kurulu ve kartta bağlı (Gelişmiş Kurulum sihirbazı bitmiş; Ayarlar → Wi-Fi Sürücüsü "Kurulu"),
   uygulama derlenmiş (`dotnet build AirDrop.slnx`; çıktıda `NitroDrop.Radio.exe` var). Wintun ilk başlangıçta onayla
   indirilir (geliştirici makinesinde önceden koymak için: `tools\fetch-wintun.ps1 -InstallForUser`). İnternet için başka bir bağlantı (Ethernet/USB/telefon)
   — kart sürücümüzdeyken Windows Wi-Fi'ı yok.
2. NitroDrop'u **normal** başlat (yönetici olarak değil). Tek bir UAC penceresi çıkar ("NitroDrop.Radio") → **Evet**.
   İlk seferde Wintun sürücüsü + "NitroDrop AWDL" ağ adaptörü oluşturulur (sistem değişikliği; uygulama kapanınca adaptör silinir).
3. Yakındakiler'deki çip kaybolur (hazır); Ayarlar → Wi-Fi Sürücüsü satırında "iPhone bağlantısı hazır · kanal 6 · 0 cihaz".
   Hata varsa metin nedeni söyler; ayrıntı `%LOCALAPPDATA%\airdrop-win\logs\airdrop.log` (`[radio]` satırları) ve
   `...\logs\radio.log`.
4. iPhone: Denetim Merkezi → AirDrop → **Herkes (10 dk)**, Paylaş → AirDrop ekranını aç. Bekle: "N cihaz" artıyor mu
   (AWDL eşi), PC iPhone'da görünüyor mu? Küçük bir fotoğraf gönder, sonra PC → iPhone.
5. Kapatma: NitroDrop'u tepsiden çık → `NitroDrop.Radio.exe` birkaç saniyede kapanmalı (Görev Yöneticisi), radyo durur.
   Ayarlar'daki anahtarı kapat/aç: host kapanıp yeniden UAC ile açılır (UAC'de "Hayır" denirse yalnız LAN + "Yönetici izni
   verilmedi" metni; anahtarı kapat/aç = yeniden dene).
6. Sorun olursa log'ları ve şu sayaçları gönder: `radio.log` (TX/RX, `NO OVERLAP`, peer ekle/sil), `airdrop.log`.
   Ayıklama düğmeleri aşağıdaki "İlk donanım test planı" 7. maddede (şimdilik kodda; `start` komutunda `listenerOnly` var).

## İlk donanım test planı

Ön koşul: sürücü M3 yüklü (`driver/README.md`), Intel inbox Wi-Fi durdurulmuş, yönetici PowerShell, iPhone
AirDrop "Herkes (10 dk)", aynı odada.

1. **Radyo:** `AdWifiRadio.OpenAsync` → `Info` (MAC, firmware, `RadioMode == IBSS`, kanal 6). 30 s RX:
   `Stats.RxFrames` artıyor, `RxDroppedByDriver` ~0.
2. **Dinleme:** iPhone'da Paylaş → AirDrop ekranını aç. Konsolda `AwdlHost` loguyla iPhone'un PSF/MIF'leri
   ayrıştırılıyor mu (`RxMalformed == 0`), `PeerUp` geliyor mu? (Telefon kanal 6'yı sadece slot 8'de kullanıyor, capture'da öyle.)
3. **Yayın:** Bizim MIF/PSF'lerimiz telefonda görülüyor mu? Dolaylı ölçüt: telefonun bizi seçim/senkron
   parametrelerinde (`master`, `sync`) göstermesi. Gerekirse ikinci bir Linux/Owly makinesiyle sniff.
   `Stats.TxSuccess/TxFailed`, `TxNoAckNotHonored`.
4. **Zamanlama:** `HostStats.TimerLateAvgUs/MaxUs` (hedef p99 < 1 ms). Peer'in `SyncOffsetUs`'i.
5. **IP:** Wintun kur + yapılandır. `ping fe80::<iphone-eui64>%idx` (Owly echo'su gibi). Telefonun SYN'i
   (`SynHeld/SynReleased`) ve `Delivered` artıyor mu?
6. **AirDrop:** (uygulama bunu kendisi yapıyor: yukarıdaki "Test sürüşü") BLE reklamı + "NitroDrop AWDL" düğümü, telefonda PC görünüyor mu? Küçük
   dosya gönder. Sonra Windows → iPhone yönü.
7. Sorun ayıklama düğmeleri: `UpgradeNoAckDataToQos=false`, `NoAck=false` (ACK'li), `ListenerOnly` (yalnız dinleme),
   `AwdlHostOptions.SpinUs=300`, `RssiThreshold=null`.

## Testler

`app/AirDrop.Tests/Awdl/` (52 test, tamamı geçiyor, 5 tekrarda kararlı):
- `AwdlWireTests.cs`: Owly unit test portları + capture vektörleri (`OwlyCaptureVectors.cs`, kaynak ve kare numarası belirtilmiş).
- `AwdlSimulationTests.cs`: `VirtualAir` ile deterministik çok düğümlü senaryolar.
- `AwdlInteropTests.cs`: IOCTL v2 düzenleri, QoS yükseltmesi, TX_RESULT, BLE yükü, saat ve bekleyici.
- `AwdlEndToEndTests.cs`: gerçek zamanlı iki host üzerinden AirDrop HTTPS aktarımı + yabancı kaynak filtresi.
- `RadioPipeTests.cs`: radyo hostu borusu (codec, gerçek adlandırılmış boru üstünde sahte yığınla gidiş-dönüş, ömür
  kuralları, PID doğrulamaları). Uygulama tarafı kararları: `AirDrop.Tests/RadioServiceTests.cs`.
