# airdrop-win sürücüsü: Intel Wi-Fi 6 AX203 için KMDF

Intel AX203 (PCI `8086:51F1`, subsystem `8086:0274`; So MAC ve Hr2 RF) için yazdığımız kendi KMDF çekirdek sürücüsü. Firmware'i ayağa kaldırıyor. Hedef: sabit kanal 6 ve BSSID `00:25:00:ff:94:73` üzerinde IBSS MAC context, monitor RX ve ham 802.11 enjeksiyonu, hepsi özel bir IOCTL arayüzünden. **NDIS değil**, sistemde ağ adaptörü olarak görünmez.

Bu belge **M1 + M2 + M3** durumunu anlatıyor: firmware bring-up, monitor RX (M2), IBSS + ham enjeksiyon (M3). Hepsi yalnız cihaz modeline karşı test edildi; donanımda **hiç denenmedi**. İlk donanım denemesi için bkz. [İlk donanım testi](#ilk-donanım-testi-runbook), riskler için [Riskler](#riskler-ve-varsayımlar).

## Lisans ve kaynak

| Kaynak | Lisans | Kullanım |
|---|---|---|
| OpenBSD `if_iwx.c` (rev 1.230), `if_iwxreg.h`, `if_iwxvar.h` | ISC | `core/` buradan port edildi. Orijinal ISC başlıkları ve atıflar dosyalarda duruyor. |
| Linux iwlwifi (mvm) | BSD-3-Clause (dual) | Referans: yanıt kuralları, struct boyutları; M2'de RX FCS/A-MPDU/rate çözümü, M3'te IBSS MAC context, beacon şablonu, multicast/broadcast station sırası ve enjeksiyon rate'i. Intel'in dual BSD/GPL bildirimi `core/iwx_radio.c` başında. |
| t4t5/owly | MIT | **Kod alınmadı.** Yalnız davranış modeli (Intel'de "exclusive" mod, kanal 6, AWDL BSSID'si üzerinde ad-hoc hücre ACK veriyor, monitor arayüzünden enjeksiyon, `tx_noack`). Bkz. [Owly eşlemesi](#owly-eşlemesi-m3). |
| itlwm / AppleIntelWifi | GPL-2.0 | **Kod alınmadı.** Yalnız anlamak için okundu. |
| Firmware `iwlwifi-so-a0-hr-b0-77.ucode` | LICENCE.iwlwifi_firmware | **Repoda yok.** `tools/fetch-firmware.ps1` indiriyor. `driver/firmware/` `.gitignore`'da. |

Repodaki bütün kod permissive (ISC/BSD).

## Mimari

```
driver/
  core/        taşınabilir iwx çekirdeği (OS bağımsız, yalnızca osdep.h'ye bağlı)
    osdep.h      OS soyutlaması: MMIO, PCI config, DMA, kilit, event, log, firmware, RX teslimi, fatal
    iwx_reg.h    if_iwxreg.h portu (donanım/firmware ABI)
    iwx_var.h    if_iwxvar.h alt kümesi + airdrop eklemeleri (softc)
    iwx_core.h   backend'in gördüğü genel API
    iwx_fw.c     TLV ayrıştırıcı (iwx_parse_firmware / iwx_read_firmware)
    iwx_trans.c  PRPH/SRAM erişimi, nic_lock, DMA halkaları, APM, MSI-X, context-info gen3 + IML, ISR/DPC
    iwx_cmd.c    host komut kuyruğu (senkron/asenkron), yanıt kopyalama, bildirim dağıtımı, ALIVE
    iwx_init.c   attach/detach, preinit/init/stop, NVM, init_hw komut sırası, hata kaydı dökümü
    iwx_radio.c  RX MPDU -> osdep_rx_frame (FCS, A-MPDU, rate çözümü); M2 LISTENER ve M3 IBSS bring-up/teardown;
                 veri kuyrukları (SCD_QUEUE_CONFIG), TX_CMD gen3 enjeksiyonu, TX yanıtı, flush
  kmdf/        Windows backend
    driver.c     DriverEntry, PnP/Power, BAR0, DMA enabler, kesme (ISR/DPC), fatal work item
    osdep_kmdf.c osdep.h'nin KMDF karşılığı
    ioctl.c      IOCTL kuyrukları, RX okuma kuyruğu, bekleyen INJECT istekleri (iptal edilebilir), dosya temizliği
    airdrop_queues.h  RX halkası + TX sonuç slotları (WDF tipi yok, host testinde aynen koşuyor)
  include/airdrop_ioctl.h  kullanıcı/çekirdek ABI (GUID, IOCTL kodları, yapılar ve boyut assert'leri)
  tests/       mock osdep + cihaz modeliyle user-mode host testleri
  tools/adwifi-cli/  user-mode test istemcisi (C): info / start / read (pcap+radiotap) / inject / stop / run
  airdropwifi.inf          KMDF 1.33 INF (kurulmaz, bkz. aşağısı)
  build.ps1                cl/link ile doğrudan derleme
```

### KMDF backend

- **PnP ve güç**
  - `EvtDevicePrepareHardware`: BAR0, `MmMapIoSpaceEx(PAGE_READWRITE | PAGE_NOCACHE)` ile eşlenir. PCI config `BUS_INTERFACE_STANDARD` ile okunur. Ardından softc ve RX halkası ayrılır ve `iwx_attach` çağrılır.
  - `EvtDeviceD0EntryPostInterruptsEnabled`: MSI-X etkin mi diye config-space capability'ye bakılır, sonra `iwx_preinit` çalışır. Bu adım INIT firmware'ini koşturup NVM'i okur ve cihazı yeniden durdurur. Hata olursa D0 başarısız sayılmaz. Hata `InitStatus`'a yazılır ve IOCTL'lerden döner. Böylece cihaz Device Manager'da sarı ünlemde kalmaz ve kaldırılabilir.
  - `EvtDeviceD0ExitPreInterruptsDisabled`: radyo kapatılır, bekleyen RX okumaları boşaltılır, kuyruktaki enjeksiyonlar `AIRDROP_TXST_FLUSHED` sonucuyla tamamlanır.
  - `EvtDeviceReleaseHardware`: `iwx_detach`, ardından bütün DMA common buffer'ları ve havuzlar serbest bırakılır, `MmUnmapIoSpace` yapılır.
- **DMA:** `WdfDmaEnablerCreate(WdfDmaProfileScatterGather64)` kullanılıyor. Her halka, context-info, IML ve firmware bölümü `WdfCommonBufferCreateWithConfig` ile ayrılıyor. Cihaza giden adres her zaman DMA adapter'ın **logical address**'i. Bu yüzden IOMMU/Kernel DMA Protection açıkken de çalışması gerekir (INF'te `DmaRemappingCompatible=1`).
- **Kesme:** `WdfInterrupt`, tek mesajlı MSI-X; olmazsa MSI.
  - ISR (DIRQL) cause register'larını okuyup W1C ile temizler, birikmiş cause'ları saklar.
  - DPC işi yapar: RX, ALIVE, hata. Sonunda her zaman automask bitini yazar (iwx ile aynı).
  - Legacy INTx/ICT yolu çekirdekte port edildi, ancak INF MSI istiyor.
- **Kilitler**
  - `CoreLock` (WDFWAITLOCK): bütün PASSIVE çekirdek çağrılarını (init/stop/komut) seri hale getirir.
  - `CoreSpin` (`osdep_lock`): DPC ile paylaşılan halka durumunu korur.
  - `RxLock`: RX kopya halkası ve okuma kuyruğu içindir.
  - `TxLock`: bekleyen INJECT isteklerinin slot tablosu. Sıra her zaman `CoreSpin → RxLock/TxLock`, tersi yok. Hiçbir istek spinlock altında tamamlanmaz (`AirdropRxDeliverPending`, `AirdropTxCompleteDone` kilitten sonra).
- **Varsayılan kuyruk paralel** (M1'de sıralıydı): sonuç isteyen INJECT, firmware raporu gelene kadar `WdfRequestMarkCancelableEx` ile bekliyor; sıralı kuyruk bu sürede diğer istekleri tutardı. Çekirdeğe dokunan her yol `CoreLock` aldığı için seri kalıyor.
- **Bekleme:** ALIVE, INIT ve CMD olmak üzere üç `KEVENT` var. Çekirdek `iwx_wait_cond` ile "event + koşul" bekliyor. Bir fatal hata (FW_ERR/HW_ERR) bütün bekleyenleri uyandırır ve onlar `EIO` döner.
- **Fatal:** `osdep_fatal` bir work item kuyruğa koyar. Work item PASSIVE'de hata kaydını döker (`iwx_dump_error_log`) ve cihazı durdurur.
- **HVCI/NX**
  - `DriverEntry` içinde `ExInitializeDriverRuntime(DrvRtPoolNxOptIn)` çağrılıyor ve `POOL_NX_OPTIN=1` tanımlı.
  - Bütün ayırmalar `ExAllocatePool2(POOL_FLAG_NON_PAGED)`. Firmware dosya tamponu `POOL_FLAG_PAGED`.
  - Çalıştırılabilir ayırma yok. Link seçenekleri `/INTEGRITYCHECK /GUARD:CF /NXCOMPAT`. `dumpbin` ile RWX bölüm olmadığı doğrulandı.
- **Firmware dosyası:** `IoGetDriverDirectory(DriverDirectoryImage)` ile driver store'dan okunuyor (INF `DestinationDirs = 13`). Bulunamazsa `\SystemRoot\System32\drivers\airdrop-win\` deneniyor. Üst sınır 8 MB.

### IOCTL ABI (`include/airdrop_ioctl.h`)

Arayüz GUID'i `GUID_DEVINTERFACE_AIRDROP_WIFI` = `{6c1b3e0a-9d42-4f5e-8a71-2f3c4b5d6e7f}`, cihaz tipi `0x8A57`.

ABI sürümü **2**. v1 yapıları önek olarak korunuyor (v1 istemci 144 baytlık `AIRDROP_INFO` ve 16 baytlık `AIRDROP_START_PARAMS` gönderebilir).

| IOCTL | Fonksiyon | Yöntem | Durum |
|---|---|---|---|
| `IOCTL_AIRDROP_GET_INFO` | 0x800 | BUFFERED | `AIRDROP_INFO` (176 B): MAC, fw, hw_rev/rf_id, durum, rfkill, MSI-X, son fw hatası, RX çerçeve/düşen/kötü FCS, TX gönderilen/başarılı/başarısız/flush/bekleyen, radyo kanalı ve modu, kanal listesi |
| `IOCTL_AIRDROP_START` | 0x801 | BUFFERED | `AIRDROP_START_PARAMS` (24 B): kanal, BSSID, beacon aralığı (TU, 0 = 10000), bayraklar `LISTENER_ONLY` (M2) / `KEEP_BAD_FCS` / `NO_PROMISC`. Cihaz kapalıysa önce `iwx_init` |
| `IOCTL_AIRDROP_STOP` | 0x802 | BUFFERED | radyo teardown + `iwx_stop`, kuyruktaki TX'ler FLUSHED, RX okumaları iptal |
| `IOCTL_AIRDROP_READ_FRAME` | 0x803 | OUT_DIRECT | `AIRDROP_RX_HEADER` (64 B: TSF, GP2, rate_n_flags ham + çözülmüş encoding/MCS/NSS/bant genişliği/legacy rate, RSSI, kanal, phy_info, A-MPDU ref, FCS/BAD_FCS/SGI/LDPC/STBC/AMSDU bayrakları, DroppedSinceLast) + FCS'siz 802.11 çerçevesi. 128 slotluk halka; doluysa yeni çerçeve düşer ve sayılır |
| `IOCTL_AIRDROP_INJECT_FRAME` | 0x804 | BUFFERED | `AIRDROP_TX_HEADER` + FCS'siz MPDU (≤ 2346 B). Rate: legacy 500 kb/s biriminde (2,4,11,22 CCK; 12…108 OFDM) veya `RATE_HT` ile MCS 0–15 (+`SGI`). `ASSIGN_SEQ`, `NOACK`, `HIGH_PRI`. Çıkış tamponu `AIRDROP_TX_RESULT` (24 B) kadarsa istek TX sonucu gelene kadar bekler: SUCCESS/FAILED/FLUSHED/NO_STATUS, fw status, retry, RTS retry, sequence, kullanılan rate. 64 çerçeve uçuştaysa `STATUS_DEVICE_BUSY` |

Cihaz exclusive açılır. SDDL yalnız SYSTEM ve Administrators'a izin veriyor (`D:P(A;;GA;;;SY)(A;;GA;;;BA)`). Handle kapanınca okumalar iptal edilir ve radyo kapanır.

## iwx init sırası (korundu)

```
D0 entry -> iwx_preinit
  prepare_card_hw -> start_hw -> run_init_mvm_ucode(readnvm=1):
     load_ucode_wait_alive:
        read_firmware (TLV) -> start_fw:
           nic_init (RX/TX halkaları, MSI-X conf) -> ctxt_info_gen3_init
           (prph_scratch + IML + dram lmac/umac/paging) -> UREG_CPU_INIT_RUN=1
        ALIVE bekle (1 s) -> load_pnvm (So+Hr: PNVM yok, sku_id != 0 ise yalnız doorbell
        ve PNVM_INIT_COMPLETE beklemesi) -> post_alive
     NVM_ACCESS_COMPLETE -> INIT_COMPLETE bekle -> NVM_GET_INFO
  stop_device
IOCTL START -> iwx_init -> start_hw -> init_hw:
  load_ucode_wait_alive (regular) -> TX_ANT -> (PHY_CFG yalnız tx_with_siso_diversity ise; So+Hr: hayır) -> BT -> SOC -> [DQA*] ->
  LTR -> TEMP_THRESHOLDS -> POWER_TABLE -> MCC_UPDATE "ZZ" -> SCAN_CFG (reduced) -> beacon filtresi kapalı
```

\* DQA_ENABLE, iwx'teki gibi yalnız `CAPA_DQA_SUPPORT` varsa gönderilir. Gerçek `-77` imajı bu biti **ilan etmiyor**, yani komut gönderilmiyor. Linux'ta durum farklı olabilir. M2'de gerçek donanımda kontrol edilecek.

### Bilinçli sapmalar (her biri kaynakta etiketli)

| Etiket | Ne |
|---|---|
| D-FW1 | `iwx_read_firmware` ikiye bölündü; `iwx_parse_firmware` tampon üzerinde çalışıyor (test edilebilsin diye) |
| D-FW2 | iwx'te olmayan ek sınır kontrolleri (kısa dosya, TLV taşması, kısa debug TLV'leri) |
| D-FW3 | çekirdekte snprintf yok, kendi sürüm biçimlendiricimiz var |
| D-FW4 | debug-destination TLV saklanıyor ama uygulanmıyor |
| D-TR1 | mbuf/bus_dma yerine sabit RX common buffer'ları; çerçeve kopyalanıyor, buffer değiş tokuşu yok |
| D-TR2 | `bus_dmamap_sync` yerine `osdep_barrier` (common buffer coherent) |
| D-TR3 | `iwx_intr_msix`/`iwx_intr` ISR (DIRQL) ve DPC olarak ikiye bölündü |
| D-TR4 | attach'ta yalnız komut kuyruğu (txq[0]) ayrılıyor; veri kuyrukları M2/M3'te |
| D-TR5 | `stop_device`: net80211 BA temizliği yok, bitmeyecek komutların yanıt tamponları atılıyor |
| D-TR6 | PCI config erişimi `osdep_pci_read32` üzerinden |
| D-TR7 | ICT, MSI-X modunda da iwx gibi ayrılıp programlanıyor |
| D-TR8 | completion descriptor rbid kontrolü yalnız tanı amaçlı |
| D-CMD1 | tsleep/wakeup yerine `iwx_wait_cond`; fatal durumda bütün bekleyenler uyanır |
| D-CMD2 | slot başına tamamlanma bayrağı |
| D-CMD3 | büyük komutlar tek 4 KiB DMA tamponundan geçiyor (iwx'te mbuf cluster) |
| D-CMD4 | yanıt kuralı Linux'taki gibi: `qid & 0x80` olmayan her paket komutunu tamamlar |
| D-CMD5 | cihazdan gelen qid/idx sınır kontrolünden geçiyor |
| D-CMD6 | net80211 tüketicileri (scan, BA, rate control…) yok; bilinen bildirimler yok sayılıyor |
| D-CMD7 | kilitleme: halka durumu `osdep_lock` ile, PASSIVE yol `CoreLock` ile |
| D-IN1 | `iwx_attach` yalnız `0x51F1` kabul ediyor; cihaz tablosu So/So-F + Hr satırlarına indirildi |
| D-IN2 | attach'ta yalnız komut kuyruğu ayrılıyor |
| D-IN3 | `init_hw`: iwx'in bazı hata yollarında atladığı `nic_unlock` her yolda yapılıyor (iwx hatası) |
| D-IN4 | POWER_TABLE seviye 0 (CAM): güç tasarrufu yok, monitor/inject'e uygun |
| D-IN5 | PNVM ayrıştırma port edilmedi (So+Hr'nin PNVM'i yok) |
| D-IN6 | yanıt paketi gelmezse NVM okuma güvenli biçimde başarısız oluyor |
| D-IN7 | kanal listesi user-mode için `sc_nvm.channels[]` içinde (net80211 yok) |
| D-IN8 | fw hata kaydı `osdep_log`'a gidiyor, `error_id` GET_INFO'da raporlanıyor |
| D-RX1 | net80211 yok: decrypt kontrolü, duplicate tespiti ve reorder yok (monitor için ham çerçeve) |
| D-RX2 | firmware'in başlıktan sonraki 2 baytlık pad'i yerinde kaldırılıyor |
| D-RX3 | minimum çerçeve 10 bayt (ACK/CTS); kontrol çerçeveleri de geçiyor |
| D-RX4 | FCS Linux `iwl_mvm_create_skb` gibi: IN_CRC32 filtresiyle (monitor) 4 bayt FCS, değilse `mac_flags1` MIC_CRC_LEN kesiliyor; yukarı hiç FCS çıkmıyor |
| D-RX5 | CRC/overrun hatası sayılıp düşürülüyor; `KEEP_BAD_FCS` ile `BAD_FCS` bayrağıyla teslim (Linux monitor davranışı) |
| D-RX6 | rate_n_flags v1/v2 çözümü, A-MPDU referansı (toggle biti, asla 0), kısa preamble yalnız CCK'de |
| D-CMD8 | yalnız komut kuyruğu yanıtları bekleyen komutun tamponuna kopyalanıyor (TX_CMD yanıtları veri kuyruğu qid'iyle geliyor) |
| D-RA1 | M2 değerleri OpenBSD'de monitor modundaki sıfır `ic_bss` ile aynı (intval 0, dtim 0, associd 0, macaddr 00..) |
| D-RA2 | iwx `flush/drain` için hep `IWX_STATION_ID` (0) kullanıyor; port gerçekten eklenen istasyonu (monitor: 2) kullanıyor (iwx'te gizli hata) |
| D-RA3 | iwx monitor enjeksiyonunu `first_data_qid`'e (monitor modda 0 = komut kuyruğu) koyuyor; port monitor istasyonu için açılan kuyruk 1'i kullanıyor |
| D-RA4 | M3 iwx'te yok: Linux `iwl_mvm_start_ap_ibss` (>22000) sırası. Sabit id'ler: bcast sta 2 / kuyruk 1, mcast sta 3 / CAB kuyruğu 2. TIME_QUOTA ve session protection yok |
| D-RA5 | IBSS context'ine PROMISC + CONTROL_AND_MGMT + IN_CRC32 ekleniyor (`NO_PROMISC` hariç); Linux bunun için ikinci bir monitor context kullanırdı |
| D-RA6 | beacon şablonu burada üretiliyor (Linux'ta mac80211): SSID "owly", 6/12/24 temel OFDM rate, DS + IBSS parametreleri, 6 Mb/s, HT IE yok |
| D-RA7 | teardown komutu başarısız olursa `iwx_stop` (cihaz durur, bütün kuyruklar FLUSHED); firmware'den yanlış qid dönerse kuyruk yine kaldırılıyor (iwx sızdırırdı) |
| D-TX1 | rate kontrolü/şifreleme yok: her zaman `CMD_RATE | ENCRYPT_DIS`; legacy anten A, MCS 8–15 anten A+B (Linux inject rate'i) |
| D-TX2 | TX_CMD gen3'te "no ACK" biti yok: unicast QoS data'da Ack Policy = No Ack yazılıyor, diğer unicast'ta `NOACK_NOT_HONORED` raporlanıyor |
| D-TX3 | payload kuyruk başına 64 × 2368 B DMA slotuna kopyalanıyor (mbuf eşlemesi yok), en fazla 64 çerçeve uçuşta |
| D-TX4 | kabul edilen her çerçeve tam olarak bir `osdep_tx_status` alıyor: TX yanıtı, sonraki SSN (NO_STATUS), TXPATH_FLUSH veya kuyruk reset'i (FLUSHED) |

## Durum

### Bitti (M1)

- [x] PnP, BAR0, MSI-X (MSI fallback), 64-bit DMA, common buffer'lar, temiz unload/remove
- [x] TLV ayrıştırma; context-info gen3, IML ve PRPH scratch kurulumu; firmware self-load ve ALIVE (v4–v7)
- [x] iwx init sırası: preinit (INIT ucode + NVM) ve init (regular ucode + init_hw komutları)
- [x] Host komut kuyruğu: senkron/asenkron, WANT_RESP, büyük komut, narrow→LONG_GROUP; bildirim dağıtımı
- [x] Fatal hata yolu: hata kaydı dökümü ve cihazın durdurulması
- [x] IOCTL ABI, INF (InfVerif `/v /w` ve `/v /k`: VALID), firmware indirme betiği

### Bitti, yalnız modelde doğrulandı (M2: monitor RX)

- [x] `-77` komut sürümleri: PHY_CONTEXT v4 (UHB yapısı), RLC_CONFIG v2, MAC_CONTEXT v5, BINDING v2 (lmac 24G), ADD_STA v12, SCD_QUEUE_CONFIG v3, TX_CMD v10 / yanıt v7 (rate_n_flags v2), BEACON_TEMPLATE v13. MLD API kullanılmıyor (So MAC'te iwx de kapatıyor).
- [x] Açılış (`iwx_auth` + `iwx_run`, monitor): PHY ADD (1×1) → RLC → MAC ADD LISTENER (PROMISC, CONTROL_AND_MGMT, ACCEPT_GRP, BEACON, PROBE_REQ, CRC32) → BINDING → ADD_STA (sta 2) → SCD ADD (kuyruk 1, tid 15) → PHY MODIFY + RLC (2×2) → ADD_STA update → MAC MODIFY assoc → SF FULL_ON → MCAST_FILTER pass-all
- [x] Kapanış (`iwx_run_stop` + `iwx_deauth`): drain/flush/undrain → SF INIT_OFF → beacon filtresi kapalı → MAC MODIFY → flush → SCD REMOVE → REMOVE_STA → BINDING/MAC/PHY REMOVE
- [x] RX: FCS kesme, kötü FCS sayma/teslim, pad kaldırma, rate çözümü, TSF, A-MPDU referansı, 128 slotluk IOCTL halkası ve düşme sayaçları

### Bitti, yalnız modelde doğrulandı (M3: IBSS + enjeksiyon)

- [x] IBSS: PHY ADD + RLC → MAC ADD IBSS (bizim MAC, BSSID `00:25:00:ff:94:73`, cck 0x01 / ofdm 0x15 ack rate, 11g EDCA, TGN, bi 10000) → BEACON_TEMPLATE → BINDING → mcast sta 3 + CAB kuyruğu 2 → bcast sta 2 + kuyruk 1 → SF INIT_OFF; ters sırada kapanış
- [x] TX_CMD gen3: narrow header, TB0 20 B + TB1 (cmd + başlık + pad) + TB2 payload, MH_SIZE/PAD offload, gen3 byte-count tablosu, `qid<<16 | cur_hw` doorbell
- [x] TX yanıtı: status/retry/rate raporu, SSN ile halka geri kazanımı, flush raporu; INJECT IOCTL'inin bekleyen/iptal edilebilir sonucu
- [x] `tools/adwifi-cli`: derleniyor (`/W4 /WX /sdl`), çalıştırılmadı

### Yapılmadı

- [ ] Donanım testi (aşağıdaki runbook)
- [ ] DQA_ENABLE'ın gerçek fw'de gerekip gerekmediği (gerçek `-77` ilan etmiyor, gönderilmiyor)
- [ ] 5 GHz IBSS (Owly'ye göre Intel'de iPhone'lar 5 GHz hücreye katılmıyor; kanal 6 hedef)
- [ ] Gen2 (AX210 öncesi) TX komutu: bu cihazda gerekmiyor, `ENOTSUP`
- [ ] Otomatik kurulum/geri dönüş aracı

### Owly eşlemesi (M3)

Owly Linux'ta Intel kartları için "exclusive" modda çalışıyor: istasyon bağlantısı kapalı (firmware ilişkiliyken AWDL çerçevesi yukarı vermiyor), kanal 6, AWDL BSSID'si üzerinde bir ad-hoc (IBSS) hücre telefonun unicast çerçevelerine ACK veriyor, çerçeveler bir monitor arayüzünden enjekte ediliyor ve okunuyor, varsayılan `tx_noack`.

| Owly / Linux | airdrop-win |
|---|---|
| `iw ... ibss join` (BSSID `00:25:00:ff:94:73`, kanal 6) → mac80211 + iwlmvm IBSS | `START` (bayraksız): MAC_CONTEXT IBSS + beacon şablonu + binding + bcast/mcast station; firmware bizim MAC'e gelen unicast'a ACK |
| ayrı monitor arayüzü (RX) | aynı context'te PROMISC/CONTROL_AND_MGMT/CRC32 filtreleri → `READ_FRAME` |
| monitor arayüzünden enjeksiyon | `INJECT_FRAME` → bcast station kuyruğu 1, açık rate |
| `tx_noack` | multicast/action zaten ACK'siz; unicast QoS data'da Ack Policy; diğer unicast'ta firmware ACK bekler (`NOACK_NOT_HONORED`) |
| `tx_rate` | `AIRDROP_TX_HEADER.Rate` (legacy) / `RATE_HT` + MCS |
| shared mod (istasyonla birlikte) | yok; Intel inbox sürücüsü dururken sistemde Wi-Fi olmaz |

## Derleme, test, paket

Gereken araçlar: VS 2022 Build Tools (MSVC), Windows SDK/WDK 10.0.26100 ve KMDF 1.33 kütüphaneleri.

```powershell
# firmware (SHA-256 sabitli; git.kernel.org olmazsa GitLab aynası)
powershell -ExecutionPolicy Bypass -File tools\fetch-firmware.ps1

# sürücü (/W4 /WX, /kernel, CFG, Spectre)
cd driver
.\build.ps1 -Name airdropwifi -Sources kmdf\driver.c,kmdf\osdep_kmdf.c,kmdf\ioctl.c,core\iwx_fw.c,core\iwx_trans.c,core\iwx_cmd.c,core\iwx_init.c,core\iwx_radio.c
#  -> driver\out\airdropwifi.sys

# host testleri (/W4 /WX, user-mode)
powershell -ExecutionPolicy Bypass -File driver\tests\build-tests.ps1

# test istemcisi (VS Build Tools komut istemi)
cd driver\tools\adwifi-cli
cl /nologo /W4 /WX /O2 adwifi-cli.c /link cfgmgr32.lib
```

Paket için `airdropwifi.inf`, `airdropwifi.sys` ve `iwlwifi-so-a0-hr-b0-77.ucode` aynı klasöre konur. `Inf2Cat` ile `.cat` üretilir, ardından Microsoft **attestation signing** (Partner Center, EV sertifikası) yapılır. Secure Boot ve HVCI açık kalır, test-signing **kullanılmaz**.

**Faz 1 (EV sertifika yokken):** `driver\package.ps1` kataloğu Inf2Cat ile **imzasız** üretir (`driver\out\airdropwifi.cat`); uygulama
inf + sys + cat'ı yanında taşır, kullanıcının makinesinde Gelişmiş Kurulum sihirbazı firmware'i indirir ve `.sys` + `.cat`'i kuruluma
özel sertifikayla imzalar (bkz. `app/README.md`). INF'i değiştirdikten sonra `package.ps1` yeniden çalıştırılmalı (katalog INF hash'ini
içerir); `DriverVer` UTC'ye göre bugünden ileri olmamalı.

### Testler (`driver/tests`)

`mock_osdep.c` hem osdep'i taklit ediyor hem de küçük bir AX210 ailesi **cihaz modeli** içeriyor:

- BAR0 register dosyası, PRPH erişimi, GP_CNTRL/RESET el sıkışmaları.
- DMA adresleri birebir eşleniyor (paddr == vaddr). Model, sürücünün programladığı her işaretçiyi gerçek cihaz gibi izliyor.
- `UREG_CPU_INIT_RUN` yazıldığında model context-info'yu, PRPH scratch'i, IML register'larını ve lmac/umac bölüm kopyalarını **doğruluyor**, sonra RX halkasına ALIVE yazıp kesme cause'u yükseltiyor.
- Komut kuyruğunun doorbell'inde (WRPTR) TFD'leri okuyor (tb sayısı ve uzunlukları, wide header, idx/qid) ve firmware gibi yanıt veriyor: generic status, NVM_GET_INFO v4, MCC_UPDATE, INIT_COMPLETE, PNVM_INIT_COMPLETE, ADD_STA status, BINDING status, SCD_QUEUE_CONFIG (kuyruğu DMA adresinden bulup `tx_queue_cfg_rsp`), TXPATH_FLUSH (kuyruk başına read_after_flush).
- Veri kuyruklarının doorbell'inde TFD'yi çözüyor (TB0 20 B, TB1 = TB0+20, narrow header code/qid/idx, MH_SIZE/PAD, çerçeveyi TB'lerden yeniden kuruyor, byte-count girişini kontrol ediyor) ve TX_CMD yanıtı + SSN dönüyor. Yanıt tutma (`tx_hold`), hata status/retry, yanlış qid, cihaz kopması (`gone`: okumalar 0xffffffff) ayarlanabilir.
- Kesmeler, sürücü beklemeye geçtiğinde ISR + DPC olarak teslim ediliyor.

Kapsam:

| Test | Kontrol ettiği |
|---|---|
| `test_layouts` | cihaz ABI struct boyutları (ctxt_info_gen3 0x68, TFD, RX desc, mpdu desc 64) ve IOCTL struct boyutları |
| `test_fw_synthetic` | sentetik TLV imajı: lmac/umac/paging bölümleme, IML, cmd_versions, api/capa bitleri, phy_sku, sürüm metni; bozuk magic, kısa dosya, TLV taşması, bilinmeyen TLV, kesik dosya |
| `test_fw_real` | gerçek `-77` imajı: `77.f92b5fed.0`, 52 RT bölümü (lmac 15 / umac 15 / paging 20), IML 13944 B, 191 cmd version |
| `test_attach_reject` | yanlış PCI ID ve yanlış RF reddediliyor, kaynak sızıntısı yok |
| `test_bringup` (sentetik ve gerçek fw) | tam preinit + init: context-info doğrulaması, PNVM doorbell, NVM sırası, MAC adresi, **init_hw komut sırası**, MCC "ZZ", POWER CAM, GET_INFO, radyo parametre doğrulaması, stop ve yeniden init, sızıntı/kilit kontrolleri |
| `test_failures` | kötü ALIVE status, yanıt vermeyen fw (ETIMEDOUT), CMD_FAILED yanıtı, firmware dosyası yok, ALIVE boyut uyuşmazlığı |
| `test_cmd_queue` | tek TB'lı komut (tb_len, adres, cur/WRPTR), narrow→LONG_GROUP, iki TB'lı büyük komut (cmd_big_dma), aşırı büyük komut reddi, WANT_RESP yanıt kopyası, halkanın sarması, asenkron komut, FW_ERR durumunda bekleyenin uyanması |
| `test_rx_ring` | free RBD tablosu (rbid, 4K hizalı 64-bit adres), 512 girişlik halkanın sarması, FRBDCB yazma indeksi `(hw-1) & ~7`, MPDU meta (kanal, gp2, RSSI), CRC hatasının düşürülmesi, `sc_rx_enabled` kapısı, başlık pad'inin kaldırılması |
| `test_queues` | IOCTL RX halkası (FIFO, taşma = yeni çerçeve düşer, DroppedSinceLast), TX slotları (cookie nesli, geç status, iptal öncesi/sonrası, abort, cihaz kopması) |
| `test_radio` (gerçek fw) | M2 açılış 12 / kapanış 14 komut ve M3 açılış 10 / kapanış 9 komut: sıra **ve payload'lar bayt bayt** (iwx_reg.h struct'larından alan alan kurulan beklenen değerlerle); RX meta/FCS/kötü FCS/pad/ACK/A-MPDU/rate v1-v2/pasif kanal; enjeksiyon TFD/byte tablosu/doorbell, rate_n_flags (OFDM, CCK, HT SGI, MCS 9 A+B), NOACK/Ack Policy, sequence, başarısız status + retry, fire-and-forget, SSN ile NO_STATUS, 300 çerçeve halka sarması, 64 uçuşta + 65. `ENOBUFS`, stop'ta 64 FLUSHED; ADD_STA/BINDING/yanlış qid hatalarında temiz geri alma, mod değiştirerek tekrar başlatma, teardown hatasında `iwx_stop`, radyo açıkken fw hatası, RX/TX sırasında cihaz kopması; kilit/sızıntı kontrolleri |

Son koşum: **2330 kontrol, 0 hata**. Çekirdek sürücü `/kernel /W4 /WX /guard:cf /Qspectre` ile uyarısız derleniyor ve linkleniyor (RWX bölüm yok), INF `/v /w` ve `/v /k` VALID.

## Intel inbox sürücüsüne dönüş

Bu INF otomatik kurulmaz. Intel `Netwtw*` sürücüsü aynı donanım kimliğine daha iyi eşleşmeyle bağlı kalır. airdrop-win şu iki yoldan biriyle elle seçilir:

- Device Manager → Sürücüyü güncelleştir → "Listeden seç"
- `pnputil /add-driver airdropwifi.inf` ile pakete ekleyip cihaza elle atama

Geri dönmek için iki yol var:

- Device Manager → Sürücüyü geri al / "Intel(R) Wi-Fi 6 AX203"ü seç
- `pnputil /delete-driver oemNN.inf /uninstall`, ardından `pnputil /scan-devices`

Sürücü PnP remove'u destekliyor (ReleaseHardware'de cihaz durdurulur, bütün DMA serbest kalır). Bu yüzden geçiş yeniden başlatma gerektirmemeli.

## İlk donanım testi (runbook)

Amaç: ilk denemede makineyi kaybetmeden, ne olduğunu kaydederek M1→M2→M3'ü sırayla doğrulamak. Bu, **yalnız test makinesi** için geçici bir düzendir; dağıtım planı (attestation, Secure Boot açık) değişmiyor.

**0. Hazırlık (geri dönüş garantisi)**
1. BitLocker kurtarma anahtarını kaydet ve makine dışında sakla: `manage-bde -protectors -get C:` (Secure Boot değişikliği kurtarma ekranı açtırır). Gerekirse önce `manage-bde -protectors -disable C: -RebootCount 3`.
2. Sistem geri yükleme noktası oluştur. Intel inbox sürücüsünün paketini not et: `pnputil /enum-drivers` içinde `Netwtw*.inf` (`oemNN.inf`) ve `pnputil /enum-devices /instanceid "PCI\VEN_8086&DEV_51F1*" /drivers`.
3. **İnternet için ayrı bir USB Wi-Fi/Ethernet adaptörü** tak ve çalıştığını doğrula; AX203 bizim sürücüye geçince dahili Wi-Fi olmayacak.
4. AX203'ün tam instance yolunu yaz: `pnputil /enum-devices /class Net /connected` → `PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_..\...`.

**1. İmzasız sürücü yükleyebilmek**
1. UEFI'de Secure Boot'u kapat (HVCI/Memory Integrity açık kalabilir; sürücü HVCI uyumlu derleniyor).
2. `bcdedit /set testsigning on`, yeniden başlat. Paket `.sys` + `.cat` bir test sertifikasıyla imzalanır (`signtool sign /fd sha256 /f test.pfx`), sertifika `LocalMachine\Root` ve `TrustedPublisher`'a eklenir.

**2. Log yakalama (kurmadan önce aç)**
- Hızlı yol: DebugView (Sysinternals) yönetici olarak, Capture Kernel + Enable Verbose Kernel Output. `KdPrintEx` IHVDRIVER filtresi için: `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Debug Print Filter" /v IHVDRIVER /t REG_DWORD /d 0xffffffff`, yeniden başlat.
- Sağlam yol (çökmeyi de görür): ağ üzerinden çekirdek debugger. Hedefte `bcdedit /debug on` + `bcdedit /dbgsettings net hostip:<host> port:50000` (Intel inbox NIC değil, KDNET destekli **ayrı** bir kablolu adaptör), hostta `windbg -k net:port=50000,key=<anahtar>`. `!wdfkd.wdflogdump airdropwifi` KMDF iç logunu, `ed nt!Kd_IHVDRIVER_Mask 0xffffffff` DbgPrint'i açar.
- Çökme dökümü: Sistem Özellikleri → Kernel memory dump; `C:\Windows\MEMORY.DMP` saklanır.

**3. Driver Verifier (yalnız bizim sürücü)**
`verifier /standard /driver airdropwifi.sys` ve ek olarak `verifier /flags 0x209BB /driver airdropwifi.sys` (special pool, IRQL kontrolü, pool tracking, deadlock detection, DMA verification, security checks, misc). KMDF için: `verifier /rc 1 2 4 5 6 9 10 11 18 19 20 22` uygun alt küme; ayrıca `HKLM\SYSTEM\CurrentControlSet\Services\airdropwifi\Parameters\Wdf` altında `VerifierOn=1`, `VerboseOn=1`. Yeniden başlat; `verifier /query` ile doğrula. Döngüye girerse Güvenli Mod'da `verifier /reset`.

**4. Kurulum (yalnız belirli cihaza)**
```
pnputil /add-driver airdropwifi.inf
pnputil /enum-drivers            (airdropwifi için oemMM.inf adını not et)
Device Manager → AX203 → Sürücüyü güncelleştir → Bilgisayarımı tara → Listeden seç → airdrop-win
```
(INF otomatik eşleşmez; Intel sürücüsü daha iyi eşleşmede kalır. `devcon update` kullanma: bütün eşleşen cihazlara uygular.) Device Manager'da cihaz sarı ünlemsiz olmalı.

**5. Kademeli doğrulama** (yönetici komut istemi, `adwifi-cli`)
1. `adwifi-cli info` → state `DOWN`, MAC NVM'deki adres, firmware `77.f92b5fed.0`, kanal listesi dolu, init status 0. (M1)
2. `adwifi-cli run --listener -c 6 -- read -n 200 -w m2.pcap` → yakındaki AP beacon'ları ve probe'lar görünmeli; `m2.pcap` Wireshark'ta radiotap ile açılır. Sonunda `info` RX sayaçları. (M2)
3. `adwifi-cli run -c 6 -- read -t 30` → IBSS açılır; aynı anda bir iPhone'da AirDrop "Herkes" açıkken AWDL action çerçeveleri (`fc d000`, kategori 0x7f, OUI 00:17:f2) görünmeli. (M3 RX)
4. `adwifi-cli run -c 6 -- inject -r 12 -n 10 -x "d0 00 00 00 ff ff ff ff ff ff <MAC> 00 25 00 ff 94 73 00 00 7f 00 17 f2 ..."` → her biri `success`; ikinci bir makinede monitor modla havada görülmeli. Unicast + `--seq` ile retry sayıları kontrol edilir. (M3 TX)
5. Her adımda başarısızlıkta: DebugView/WinDbg logu, `info` çıktısı (`last fw error`), varsa `MEMORY.DMP` saklanır, sonraki adıma geçilmez.

**6. Intel inbox sürücüsüne dönüş**
1. `adwifi-cli stop` / uygulamayı kapat.
2. Device Manager → AX203 → Sürücüyü güncelleştir → Listeden seç → "Intel(R) Wi-Fi 6 AX203" **veya** `pnputil /delete-driver oemMM.inf /uninstall /force` ardından `pnputil /scan-devices`.
3. `verifier /reset`, `bcdedit /set testsigning off`, (kullanıldıysa) `bcdedit /debug off`, yeniden başlat, Secure Boot'u UEFI'de tekrar aç. BitLocker korumasını `manage-bde -protectors -enable C:` ile geri aç.
4. Makine açılmazsa: Gelişmiş başlangıç → Başlangıç Ayarları → "Sürücü imzası zorlamasını devre dışı bırak" değil, **Güvenli Mod** → `pnputil /delete-driver oemMM.inf /uninstall /force`; o da olmazsa geri yükleme noktası.

## Riskler ve varsayımlar

**Doğrulanan:** derleme (çekirdek `/W4 /WX /kernel /guard:cf /Qspectre`, RWX bölüm yok; CLI `/W4 /WX /sdl`), INF (`/v /w`, `/v /k`), host testleri (2330 kontrol), gerçek `-77` imajının ayrıştırılması ve ondan okunan komut sürümleriyle M2/M3 komut dizilerinin bayt bayt karşılaştırılması.

**Varsayılan (modelde, donanımda değil):** cihaz modelinin firmware davranışı (yanıt biçimleri, SSN anlamı, flush yanıtı, SCD kuyruk numarası), zamanlamalar, RX desc içeriği.

1. **Donanımda hiç denenmedi.** Cihaz modeli iwx/iwlwifi'nin beklediği davranışa göre yazıldı; gerçek silikon farklı davranabilir: zamanlamalar, ALIVE öncesi register durumu, IML yanıtı.
2. **MLD API:** gerçek `-77` imajı `MLD_API_SUPPORT` ilan ediyor. iwx bunu yalnız MA/BZ'de kullanıyor ("eski cihazlarda iddia ediyor ama çalışmıyor"); biz de eski API'yi (MAC_CONTEXT/BINDING/ADD_STA) kullanıyoruz. Linux mvm ise yalnız capa bitine bakıyor; firmware eski API'yi reddederse MLD komutları (LINK_CONFIG, STA_CONFIG…) port edilmeli. **İlk donanım testinde en olası kırılma noktası.**
3. **IBSS firmware'de:** Owly Intel'de IBSS'in çalıştığını raporluyor ama bunu Linux mvm sırasıyla yapıyor; bizim tek context'te IBSS + PROMISC birleşimimiz (D-RA5) ve TIME_QUOTA göndermememiz (D-RA4) denenmedi. Firmware PROMISC'i IBSS'te yok sayarsa RX yalnız kendi BSS'imize düşer.
4. **NOACK:** gen3 TX_CMD'de no-ACK yok. Owly'nin `tx_noack`'i Linux'ta da aynı sınırda; unicast action çerçevelerinde firmware ACK bekleyip retry yapar (sonuçta `NOACK_NOT_HONORED`).
5. **Ani çıkarma (surprise removal):** D0Exit'te teardown ilk komutun timeout'unu (≈1 s) bekleyip `iwx_stop`'a düşüyor; modelde test edildi, gerçek PCIe kopmasında ek gecikme olabilir.
6. **Bus mastering:** PCI command register'ındaki BM biti PnP tarafından açılmış varsayılıyor. Kapalıysa yalnız uyarı loglanıyor.
7. **MSI-X tespiti** config-space capability'ye bakıyor; KMDF kesme kaynağı türüyle çapraz kontrol yok.
8. **`sc_flags` yarışı:** FW_ERR/HW_ERR DPC'de set edilip PASSIVE'de kilitsiz okunuyor (iwx'te de böyle). Driver Verifier'da izlenmeli.
9. **DQA_ENABLE** gerçek `-77` imajında ilan edilmediği için gönderilmiyor (iwx davranışı). Firmware veri kuyruklarında DQA bekliyorsa SCD_QUEUE_CONFIG yanıtı farklı olabilir.
10. **Bekleyen INJECT iptali:** `WdfRequestMarkCancelableEx` / `UnmarkCancelable` standart kalıbı; slot, iptal edilen isteğin firmware sonucunu beklemeye devam ediyor (cookie nesli ile). Modelde değil, yalnız `airdrop_queues.h` mantığı test edildi; KMDF tarafı Verifier ile denenmeli.
11. **`driver/out/` eski:** derlenmiş `airdropwifi.sys` ve `airdropwifi.cat` M1'den; yeni kodla `build.ps1` + `package.ps1` yeniden çalıştırılmalı (katalog `.sys` hash'ini içeriyor; uygulama paketi bunları kullanıyor).
12. **`Class=System`**, NDIS yok: Windows Wi-Fi yığını cihazı görmez; Intel sürücüsü dururken sistemde Wi-Fi olmaz. SDDL yalnız admin, cihaz exclusive.
13. **Firmware sürümü:** `-77` sabit. Daha yeni imajlar (`-89` vb.) farklı komut sürümleri getiriyor; `iwx_radio.c` desteklenmeyen sürümlerde `ENOTSUP` dönüyor.
14. **Attestation signing:** Windows Update üzerinden dağıtılamaz, retail dağıtım sınırlamaları var.
