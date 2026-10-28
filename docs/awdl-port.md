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
- HTTPS'in mDNS ve dinleme adresinin Wintun arayüzüne bağlanması (aşağıdaki entegrasyon) AirDrop.Core'da test edilmedi.

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

## Entegrasyon (AirDrop.Core / App)

Bu porttan sonra çağıranın yapacakları (AirDrop.App/Core'a ben dokunmadım):

1. **Wintun:** `tools/fetch-wintun.ps1` çalıştır (sadece indirir, SHA-256 sabit, `vendor/wintun`, gitignore'da).
   Build `wintun\{amd64,arm64}\wintun.dll`'i Nitro.Awdl çıktısına kopyalıyor. Lisans: WireGuard LLC "Prebuilt
   Binaries License", değiştirilmemiş dll + yalnız `wintun.h` API'si. Dağıtımda `vendor/wintun/LICENSE.txt` yanına konur.
2. **Kurulum (bir kez, admin, sistem değişikliği):** `WintunAdapter.Create("NitroDrop AWDL")`, ardından
   `adapter.ApplyConfiguration(radio.Address)`. Bu adım `fe80::EUI64/64` adresini ekliyor, MTU 1450, DAD 0,
   router discovery kapalı, diğer link-local'ları `skipassource` yapıyor. Wintun adaptörü oluşturanın handle'ı
   kapanınca siliniyor. Bu yüzden servis/yardımcı süreç adaptörü açık tutmalı.
3. **Çalışma zamanı (admin, sürücü erişimi için):**
   ```csharp
   var radio = await AdWifiRadio.OpenAsync(new AdWifiRadioOptions { Log = log });
   using var adapter = WintunAdapter.Open();            // ya da Create (admin)
   using var sink = new WintunPacketSink(adapter);
   var host = new AwdlHost(new AwdlConfig { Addr = radio.Address, Name = Environment.MachineName,
                                           Radio = RadioConstraints.IntelExclusive }, radio, sink);
   await host.StartAsync();
   ```
4. **AirDrop.Core bağlama:** `NetworkBinding.Resolve("NitroDrop AWDL")` (ad, index ya da GUID) →
   `IPv6Index` = Wintun ifindex = link-local scope id. `AirDropNodeOptions.Binding` buna verilir. mDNS yalnız bu
   arayüzde (ff02::fb%idx) yayın/sorgu yapar. HTTPS dinleme ideali `fe80::EUI64%idx:8770`. Şu an
   `ListenEndPoint` IPv6Any döndürüyor (UNVERIFIED notu), bu da çalışır. Giden bağlantı `ConnectAsync` link-local'ı
   `IPv6Index` ile scope'luyor, `IPV6_UNICAST_IF` uygulanıyor.
5. **`CoreAirDropService` değişikliği (çağıran uygular):**
   - `_settings.Interface` varsayılanı "any" yerine, AWDL host çalışıyorsa `"NitroDrop AWDL"`:
     `Binding = NetworkBinding.Resolve(awdlRunning ? WintunAdapter.DefaultName : _settings.Interface)`.
   - `IsHardwareReady = awdlHost is { Failure: null }` ve en az `radio.Info.State == StateRadioOn`.
   - `HardwareStatus`: `"AWDL: kanal 6, {host.Peers.Count} cihaz"`; hata olursa `host.Failure.Message`,
     sürücü yoksa `FileNotFoundException`, yetki yoksa `UnauthorizedAccessException` mesajı.
   - Gönderirken (tarama açıkken) `BleAirDropAdvertiser.Start()`, bitince `Stop()`. Bu sınıf yalnız
     `net10.0-windows10.0.22621.0` TFM'inde var, App'in TFM'i buna uyuyor.
   - `host.PeerUp/PeerDown` olayları mDNS taramasını tetiklemek için kullanılabilir.
6. Admin gerekmeyen saf user-mode yol: `UserModePacketSink` + kendi TCP/IP'si olan bileşenler (testlerdeki tünel
   gibi). Gerçek iPhone ile pratik değil, Wintun yolu asıl hedef.

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
6. **AirDrop:** BLE reklamını aç, AirDrop.App'te arayüzü "NitroDrop AWDL" yap, telefonda PC görünüyor mu? Küçük
   dosya gönder. Sonra Windows → iPhone yönü.
7. Sorun ayıklama düğmeleri: `UpgradeNoAckDataToQos=false`, `NoAck=false` (ACK'li), `ListenerOnly` (yalnız dinleme),
   `AwdlHostOptions.SpinUs=300`, `RssiThreshold=null`.

## Testler

`app/AirDrop.Tests/Awdl/` (52 test, tamamı geçiyor, 5 tekrarda kararlı):
- `AwdlWireTests.cs`: Owly unit test portları + capture vektörleri (`OwlyCaptureVectors.cs`, kaynak ve kare numarası belirtilmiş).
- `AwdlSimulationTests.cs`: `VirtualAir` ile deterministik çok düğümlü senaryolar.
- `AwdlInteropTests.cs`: IOCTL v2 düzenleri, QoS yükseltmesi, TX_RESULT, BLE yükü, saat ve bekleyici.
- `AwdlEndToEndTests.cs`: gerçek zamanlı iki host üzerinden AirDrop HTTPS aktarımı + yabancı kaynak filtresi.
