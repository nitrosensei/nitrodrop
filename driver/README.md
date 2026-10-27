# airdrop-win sürücüsü: Intel Wi-Fi 6 AX203 için KMDF

Intel AX203 (PCI `8086:51F1`, subsystem `8086:0274`; So MAC ve Hr2 RF) için yazdığımız kendi KMDF çekirdek sürücüsü. Firmware'i ayağa kaldırıyor. Hedef: sabit kanal 6 ve BSSID `00:25:00:ff:94:73` üzerinde IBSS MAC context, monitor RX ve ham 802.11 enjeksiyonu, hepsi özel bir IOCTL arayüzünden. **NDIS değil**, sistemde ağ adaptörü olarak görünmez.

Bu belge **M1** durumunu anlatıyor. Donanımda **hiç denenmedi**. Bkz. [Riskler](#riskler-ve-varsayımlar).

## Lisans ve kaynak

| Kaynak | Lisans | Kullanım |
|---|---|---|
| OpenBSD `if_iwx.c` (rev 1.230), `if_iwxreg.h`, `if_iwxvar.h` | ISC | `core/` buradan port edildi. Orijinal ISC başlıkları ve atıflar dosyalarda duruyor. |
| Linux iwlwifi | BSD-3-Clause (dual) | Yalnız referans (yanıt kuralları, struct boyutları). Alıntılanan yerlerde bildirim var. |
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
    iwx_radio.c  RX MPDU -> osdep_rx_frame; M2/M3 stub'ları (ayrıntılı komut planıyla)
  kmdf/        Windows backend
    driver.c     DriverEntry, PnP/Power, BAR0, DMA enabler, kesme (ISR/DPC), fatal work item
    osdep_kmdf.c osdep.h'nin KMDF karşılığı
    ioctl.c      IOCTL kuyrukları, RX okuma kuyruğu, dosya temizliği
  include/airdrop_ioctl.h  kullanıcı/çekirdek ABI (GUID, IOCTL kodları, yapılar ve boyut assert'leri)
  tests/       mock osdep + cihaz modeliyle user-mode host testleri
  airdropwifi.inf          KMDF 1.33 INF (kurulmaz, bkz. aşağısı)
  build.ps1                cl/link ile doğrudan derleme
```

### KMDF backend

- **PnP ve güç**
  - `EvtDevicePrepareHardware`: BAR0, `MmMapIoSpaceEx(PAGE_READWRITE | PAGE_NOCACHE)` ile eşlenir. PCI config `BUS_INTERFACE_STANDARD` ile okunur. Ardından softc ve RX halkası ayrılır ve `iwx_attach` çağrılır.
  - `EvtDeviceD0EntryPostInterruptsEnabled`: MSI-X etkin mi diye config-space capability'ye bakılır, sonra `iwx_preinit` çalışır. Bu adım INIT firmware'ini koşturup NVM'i okur ve cihazı yeniden durdurur. Hata olursa D0 başarısız sayılmaz. Hata `InitStatus`'a yazılır ve IOCTL'lerden döner. Böylece cihaz Device Manager'da sarı ünlemde kalmaz ve kaldırılabilir.
  - `EvtDeviceD0ExitPreInterruptsDisabled`: radyo kapatılır, bekleyen RX okumaları boşaltılır.
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
- **Bekleme:** ALIVE, INIT ve CMD olmak üzere üç `KEVENT` var. Çekirdek `iwx_wait_cond` ile "event + koşul" bekliyor. Bir fatal hata (FW_ERR/HW_ERR) bütün bekleyenleri uyandırır ve onlar `EIO` döner.
- **Fatal:** `osdep_fatal` bir work item kuyruğa koyar. Work item PASSIVE'de hata kaydını döker (`iwx_dump_error_log`) ve cihazı durdurur.
- **HVCI/NX**
  - `DriverEntry` içinde `ExInitializeDriverRuntime(DrvRtPoolNxOptIn)` çağrılıyor ve `POOL_NX_OPTIN=1` tanımlı.
  - Bütün ayırmalar `ExAllocatePool2(POOL_FLAG_NON_PAGED)`. Firmware dosya tamponu `POOL_FLAG_PAGED`.
  - Çalıştırılabilir ayırma yok. Link seçenekleri `/INTEGRITYCHECK /GUARD:CF /NXCOMPAT`. `dumpbin` ile RWX bölüm olmadığı doğrulandı.
- **Firmware dosyası:** `IoGetDriverDirectory(DriverDirectoryImage)` ile driver store'dan okunuyor (INF `DestinationDirs = 13`). Bulunamazsa `\SystemRoot\System32\drivers\airdrop-win\` deneniyor. Üst sınır 8 MB.

### IOCTL ABI (`include/airdrop_ioctl.h`)

Arayüz GUID'i `GUID_DEVINTERFACE_AIRDROP_WIFI` = `{6c1b3e0a-9d42-4f5e-8a71-2f3c4b5d6e7f}`, cihaz tipi `0x8A57`.

| IOCTL | Fonksiyon | Yöntem | M1 durumu |
|---|---|---|---|
| `IOCTL_AIRDROP_GET_INFO` | 0x800 | BUFFERED | çalışıyor: MAC, fw sürümü, hw_rev/rf_id, durum, rfkill, MSI-X, son fw hata kodu, RX sayaçları, kanal listesi |
| `IOCTL_AIRDROP_START` | 0x801 | BUFFERED | cihaz kapalıysa `iwx_init` çalışır (tam init_hw), ardından `iwx_radio_start` **`STATUS_NOT_IMPLEMENTED`** döner (M2) |
| `IOCTL_AIRDROP_STOP` | 0x802 | BUFFERED | radyo durur, RX boşaltılır |
| `IOCTL_AIRDROP_READ_FRAME` | 0x803 | OUT_DIRECT | `AIRDROP_RX_HEADER` + 802.11 çerçevesi; çerçeve yoksa istek kuyrukta bekler |
| `IOCTL_AIRDROP_INJECT_FRAME` | 0x804 | BUFFERED | başlık doğrulanır, M1'de `NOT_IMPLEMENTED` (M3) |

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

## Durum

### Bitti (M1)

- [x] PnP, BAR0, MSI-X (MSI fallback), 64-bit DMA, common buffer'lar, temiz unload/remove
- [x] TLV ayrıştırma; context-info gen3, IML ve PRPH scratch kurulumu; firmware self-load ve ALIVE (v4–v7)
- [x] iwx init sırası: preinit (INIT ucode + NVM) ve init (regular ucode + init_hw komutları)
- [x] Host komut kuyruğu: senkron/asenkron, WANT_RESP, büyük komut, narrow→LONG_GROUP; bildirim dağıtımı
- [x] RX MQ halkası ve MPDU → IOCTL READ_FRAME yolu (`sc_rx_enabled` ile kapılı; M1'de radyo açılmadığı için boş)
- [x] Fatal hata yolu: hata kaydı dökümü ve cihazın durdurulması
- [x] IOCTL ABI, INF (InfVerif `/v /w` ve `/v /h`: VALID), firmware indirme betiği

### M2: dinleme (monitor RX)
- [ ] `iwx_radio_start(LISTENER_ONLY)`: PHY_CONTEXT_CMD (ch 6, 20 MHz) → MAC_CONTEXT (MAC_FILTER_IN_PROMISC | CONTROL | BEACON, tip LISTENER) → BINDING → (gerekirse) TIME_EVENT. Komut planı ayrıntılı biçimde `core/iwx_radio.c` içinde.
- [ ] `sc_rx_enabled = 1` ve RX çerçevelerinin user-mode'a akışı; radiotap'a uygun meta (rate_n_flags v1/v2 çözümü)
- [ ] DQA_ENABLE'ın gerçek fw'de gerekip gerekmediğinin doğrulanması

### M3: IBSS ve enjeksiyon
- [ ] IBSS MAC context (BSSID `00:25:00:ff:94:73`), aux/broadcast station (ADD_STA), beacon şablonu
- [ ] Veri TX kuyruğu (SCD_QUEUE_CONFIG / `iwx_enable_txq`), TX_CMD v8/v9 (`rate_n_flags`, NOACK), byte-count tablosu
- [ ] TX tamamlama bildirimi, halka geri kazanımı, IOCTL INJECT_FRAME
- [ ] Kurulum/onarım aracı (pnputil ile kur, kaldırınca Intel inbox sürücüsünün geri gelmesi)

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
- Komut kuyruğunun doorbell'inde (WRPTR) TFD'leri okuyor (tb sayısı ve uzunlukları, wide header, idx/qid) ve firmware gibi yanıt veriyor: generic status, NVM_GET_INFO v4, MCC_UPDATE, INIT_COMPLETE, PNVM_INIT_COMPLETE.
- Kesmeler, sürücü beklemeye geçtiğinde ISR + DPC olarak teslim ediliyor.

Kapsam:

| Test | Kontrol ettiği |
|---|---|
| `test_layouts` | cihaz ABI struct boyutları (ctxt_info_gen3 0x68, TFD, RX desc, mpdu desc 64) ve IOCTL struct boyutları |
| `test_fw_synthetic` | sentetik TLV imajı: lmac/umac/paging bölümleme, IML, cmd_versions, api/capa bitleri, phy_sku, sürüm metni; bozuk magic, kısa dosya, TLV taşması, bilinmeyen TLV, kesik dosya |
| `test_fw_real` | gerçek `-77` imajı: `77.f92b5fed.0`, 52 RT bölümü (lmac 15 / umac 15 / paging 20), IML 13944 B, 191 cmd version |
| `test_attach_reject` | yanlış PCI ID ve yanlış RF reddediliyor, kaynak sızıntısı yok |
| `test_bringup` (sentetik ve gerçek fw) | tam preinit + init: context-info doğrulaması, PNVM doorbell, NVM sırası, MAC adresi, **init_hw komut sırası**, MCC "ZZ", POWER CAM, GET_INFO, M1 radyo stub'ları, stop ve yeniden init, sızıntı/kilit kontrolleri |
| `test_failures` | kötü ALIVE status, yanıt vermeyen fw (ETIMEDOUT), CMD_FAILED yanıtı, firmware dosyası yok, ALIVE boyut uyuşmazlığı |
| `test_cmd_queue` | tek TB'lı komut (tb_len, adres, cur/WRPTR), narrow→LONG_GROUP, iki TB'lı büyük komut (cmd_big_dma), aşırı büyük komut reddi, WANT_RESP yanıt kopyası, halkanın sarması, asenkron komut, FW_ERR durumunda bekleyenin uyanması |
| `test_rx_ring` | free RBD tablosu (rbid, 4K hizalı 64-bit adres), 512 girişlik halkanın sarması, FRBDCB yazma indeksi `(hw-1) & ~7`, MPDU meta (kanal, gp2, RSSI), CRC hatasının düşürülmesi, `sc_rx_enabled` kapısı, başlık pad'inin kaldırılması |

Son koşum: **1766 kontrol, 0 hata**.

## Intel inbox sürücüsüne dönüş

Bu INF otomatik kurulmaz. Intel `Netwtw*` sürücüsü aynı donanım kimliğine daha iyi eşleşmeyle bağlı kalır. airdrop-win şu iki yoldan biriyle elle seçilir:

- Device Manager → Sürücüyü güncelleştir → "Listeden seç"
- `pnputil /add-driver airdropwifi.inf` ile pakete ekleyip cihaza elle atama

Geri dönmek için iki yol var:

- Device Manager → Sürücüyü geri al / "Intel(R) Wi-Fi 6 AX203"ü seç
- `pnputil /delete-driver oemNN.inf /uninstall`, ardından `pnputil /scan-devices`

Sürücü PnP remove'u destekliyor (ReleaseHardware'de cihaz durdurulur, bütün DMA serbest kalır). Bu yüzden geçiş yeniden başlatma gerektirmemeli. M3'te bunu otomatikleştiren bir araç planlanıyor.

## Riskler ve varsayımlar

1. **Donanımda hiç denenmedi.** Doğrulanan şeyler: derleme (/W4 /WX), INF doğrulaması, host testleri ve gerçek firmware'in ayrıştırılması. Cihaz modeli iwx/iwlwifi'nin beklediği davranışa göre yazıldı. Gerçek silikon farklı davranabilir: zamanlamalar, ALIVE öncesi register durumu, IML yanıtı.
2. **Bus mastering:** PCI command register'ındaki BM biti PnP tarafından açılmış varsayılıyor. Kapalıysa yalnız uyarı loglanıyor, biz açmıyoruz.
3. **MSI-X tespiti** config-space'teki MSI-X capability'nin enable bitine bakıyor. KMDF'in kesme kaynağı türü (`CmResourceTypeInterrupt` + `MESSAGE` bayrağı) ile çapraz kontrol henüz yok.
4. **`sc_flags` yarışı:** FW_ERR/HW_ERR bayrakları DPC'de set edilip PASSIVE kodda okunuyor. Bayraklar düz `int`, kilitsiz. iwx'te de böyle ve en kötü durumda bir bekleme timeout'la bitiyor. Yine de Driver Verifier ve donanım testinde izlenmeli.
5. **DQA_ENABLE** gerçek `-77` imajında ilan edilmediği için gönderilmiyor (iwx davranışı). Firmware DQA'yı bekliyorsa komut kuyruğu dışındaki kuyruklar M3'te sorun çıkarabilir.
6. **`Class=System`**: Net sınıfı değil, NDIS yok. Windows Wi-Fi yığını cihazı görmez. Bu bilinçli, ama Intel sürücüsü dururken sistemde Wi-Fi olmaz.
7. **SDDL yalnız admin**, cihaz exclusive açılıyor: aynı anda tek bir uygulama kullanabilir.
8. **START M1'de `STATUS_NOT_IMPLEMENTED`** dönüyor (init_hw tamamlandıktan sonra). RX/TX ancak M2/M3'te gelecek.
9. **Firmware sürümü:** OpenBSD'nin kullandığı `-77` sabitlendi. Daha yeni Linux imajları (`-89` vb.) MLD API ve farklı komut sürümleri getiriyor ve test edilmedi.
10. **Attestation signing** HLK gerektirmiyor, ama Microsoft'un attestation imzalı sürücülerin dağıtım koşulları geçerli: Windows Update üzerinden dağıtılamaz, retail dağıtım sınırlamaları var.
