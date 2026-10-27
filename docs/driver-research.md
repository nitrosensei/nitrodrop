# AX203 (8086:51F1 / 0274) için özel Windows sürücüsü: araştırma raporu

Tarih: 2026-10-28. Kaynaklar 2026-10-27/28'de okundu: Linux `master` (git.kernel.org), linux-firmware `main`, OpenBSD `src` master (`if_iwx.c` rev 1.230, 2026-09-19), itlwm master, t4t5/owly HEAD.
İşaretler: **[doğrulandı]** = kaynak kodda/dosyada okudum. **[doğrulanmadı]** = çıkarım ya da hafızadan bilgi, test edilmesi gerekiyor.

---

## 0. Önce iki önemli bulgu

1. **Owly (eski adı omdrop-owl) Intel kartlarda AWDL kanal atlaması yapmıyor.** `iwlwifi` için `mode = "exclusive"` ve `channels = [6]`. Kart **sabit kanal 6'da** duruyor. AWDL kanal dizisinin 16 slotunun hepsinde "ben 6'dayım" diye ilan ediyor (`Presence::Fixed`), ve telefon zaten ara sıra 6'ya uğruyor. **Bu yüzden M4'teki "hızlı kanal değiştirme" çalışan bir ürün için şart değil.** Sadece hız optimizasyonu: kanal 6'da 2.4 MB fotoğraf 10–17 saniye sürüyor. **[doğrulandı: drivers.toml, setup.rs]**
2. **ACK sorununu bir IBSS (ad-hoc) MAC context çözüyor.** Owly, istasyon arayüzünü kapatıyor. Yerine AWDL BSSID'si `00:25:00:ff:94:73` üzerinde ve istasyonun MAC adresiyle bir **IBSS cell** açıyor. Yanına da enjeksiyon için pasif bir monitor arayüzü ekliyor. Kodun yorumu aynen şöyle: *"a monitor alone on the AX201 never sees an ACK"*. IBSS firmware'e hem ACK'leyeceği bir adres hem de AWDL'yi geçiren bir BSSID filtresi veriyor. Kendi sürücümüzde bunun karşılığı: firmware'e `FW_MAC_TYPE_IBSS` tipinde bir MAC context ve `MAC_FILTER_IN_BEACON|PROBE_REQUEST|ACCEPT_GRP` göndermek (Linux'ta `iwl_mvm_mac_ctxt_cmd_ibss`). Tam promiscuous `LISTENER` MAC'i ACK vermez. **[doğrulandı: owly setup.rs `ibss_cell()`, mvm/mac-ctxt.c:771]**

Bu iki bulgu projeyi büyük ölçüde küçültüyor. Elimizde sabit kanalda çalışan bir IBSS benzeri MAC context, ham TX ve tüm çerçeveleri gören bir RX yolu olması yeterli.

---

## 1. iwlwifi'de 8086:51F1 / 0x0274 nasıl eşleşiyor

### 1.1 PCI tablosu **[doğrulandı]**
`drivers/net/wireless/intel/iwlwifi/pcie/drv.c` (master, satır 496):
```c
/* Ty/So devices */
{IWL_PCI_DEVICE(0x51F0, PCI_ANY_ID, iwl_so_long_latency_mac_cfg)},
{IWL_PCI_DEVICE(0x51F1, PCI_ANY_ID, iwl_so_long_latency_imr_mac_cfg)},
```
Bu satırlar `#if IS_ENABLED(CONFIG_IWLMVM)` bloğunun içinde. Yani So ailesini **MVM op-mode** sürüyor; Bz ve sonrasını süren yeni MLD op-mode burada devrede değil.

`iwl_so_long_latency_imr_mac_cfg` tanımı `cfg/ax210.c` satır 112–123'te:
- `.device_family = IWL_DEVICE_FAMILY_AX210`, `.gen2 = true`
- `.integrated = true` (CNVi)
- `.low_latency_xtal = true`, `.xtal_latency = 12000`
- `.ltr_delay = IWL_CFG_TRANS_LTR_DELAY_2500US`
- `.umac_prph_offset = 0x300000`
- `.imr_enabled = true`

`imr_enabled` ile `ctxt-info-v2.c:170` bağlantılı. IMR, firmware hata ayıklama dökümleri için DRAM bölgesiyle ilgili görünüyor. İlk aşamada atlanabilir. **[doğrulanmadı]**

### 1.2 RF / isim eşleşmesi **[doğrulandı]**
Yeni iwlwifi'de alt sistem kimliği tek tek listelenmiyor. RF tipi donanımdan okunuyor (`CSR_HW_RF_ID`), alt sistem kimliğinin bitleri de maske ile çözülüyor (`iwl-config.h:511–513`):
```c
#define IWL_SUBDEVICE_RF_ID(s)  (((s) & 0x00F0) >> 4)
#define IWL_SUBDEVICE_BW_LIM(s) (((s) & 0x0200) >> 9)
```
`0x0274` için: BW_LIM = 1, RF_ID = 7. Eşleşen satır `pcie/drv.c:1015`:
```c
/* HR RF */
IWL_DEV_INFO(iwl_rf_hr,       iwl_ax201_name, RF_TYPE(HR2)),
IWL_DEV_INFO(iwl_rf_hr_80mhz, iwl_ax101_name, RF_TYPE(HR1)),
IWL_DEV_INFO(iwl_rf_hr_80mhz, iwl_ax203_name, RF_TYPE(HR2), BW_LIMITED),
```
Sonuç: MAC = **So**, RF = **HR2** (Harrison Peak), 80 MHz sınırlı, isim `"Intel(R) Wi-Fi 6 AX203"` (`cfg/rf-hr.c`, `iwl_rf_hr_80mhz` → `.bw_limit = 80`).

RF'nin gerçekten HR2 olduğu çalışma anında `CSR_HW_RF_ID` okunarak teyit edilmeli. AX203 = So+HR2 olduğu hem Linux tablosunda hem OpenBSD/itlwm tablolarında aynı şekilde geçiyor. **[doğrulandı]**

Platform notu: OpenBSD `pcidevs` 0x51F0 ve 0x51F1'i "Wi-Fi 6 AX211" (CNVi) diye adlandırıyor. 0x51F1'in Alder Lake-P mi Raptor Lake-P PCH CNVi'si mi olduğunu kaynaklarda kesinleştiremedim. Sık görülen eşleme: 51F0 = ADL-P, 51F1 = RPL-P. **[doğrulanmadı]** Bu fark sürücü için önemsiz: ikisi de `so_long_latency(_imr)` ayarlarını kullanıyor.

### 1.3 Firmware dosyası **[doğrulandı]**
`iwl-drv.c:187` `iwl_drv_get_fwname_pre()`: `MAC_TYPE_SO` → `"so"` + `'a'`, `RF_TYPE_HR2` → `"hr"` + `'b'`. Ortaya çıkan önek **`iwlwifi-so-a0-hr-b0`**.

API sürümü:
- MAC tabanı `IWL_AX210_UCODE_API_MIN/MAX = 89/89` (`cfg/ax210.c:13–16`).
- RF tarafı `IWL_HR_UCODE_API_MIN/MAX = 100/100` (`cfg/rf-hr.c`).
- `iwl_get_ucode_api_versions()` RF aralığı MAC aralığının üstünde kaldığı için MAC aralığını seçiyor.
- Linux master'ın yüklediği dosya: **`iwlwifi-so-a0-hr-b0-89.ucode`**.

PNVM: linux-firmware'de sadece `iwlwifi-so-a0-gf-a0.pnvm` ve `-gf4-a0.pnvm` var. **HR için `.pnvm` dosyası yok**, bizim kart PNVM'siz çalışıyor. UEFI'den gelen PNVM/"reduce power" yolu (`fw/uefi.c`) da GF/Bz içindir. İlk aşamada atlanabilir. **[doğrulanmadı: HR'de UEFI değişkeni olmadığı]**

linux-firmware'de mevcut sürümler (`intel/iwlwifi/`): -72, -73, -74, -77, -79, -81, -83, -84, -86, -89. WHENCE sürüm dizgeleri:

| Dosya | WHENCE sürümü | Notu |
|---|---|---|
| `-89.ucode` | 86.b20dcd1f.0 | Linux master bunu ister |
| `-77.ucode` | 74.f92b5fed.0 | OpenBSD iwx bunu ister (adı `iwx-so-a0-hr-b0-77`, içerik aynı blob) |

- İndirme: https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/intel/iwlwifi
- Lisans (WHENCE): *"Redistributable. See LICENCE.iwlwifi_firmware"*, `SPDX: LicenseRef-LICENCE.iwlwifi_firmware`. Lisans metni bu oturumda URL'den çekilemedi (dosya yeniden adlandırılmış ya da taşınmış olabilir). Hafızamdaki içeriği şöyle: değiştirilmeden ikili dağıtım serbest, telif bildirimi korunmalı, Intel adı reklamda kullanılamaz, **tersine mühendislik / decompile / disassembly yasak**. **[doğrulanmadı, metin indirilip okunmalı]**
- Pratik sonuç: blob'u sürücüyle birlikte (ya da ayrı indirilen bir dosya olarak) bildirimleriyle dağıtabiliriz. Firmware'i analiz etmeye çalışmamalıyız; sadece host-command arayüzünü kullanırız.

### 1.4 Kaynak lisansları **[doğrulandı]**
İndirdiğim 40 Linux iwlwifi dosyasının (pcie/*, mvm/*, fw/*, iwl-*.c/h, cfg/*) **hepsi** `SPDX-License-Identifier: GPL-2.0 OR BSD-3-Clause`. Port edilen kodu BSD-3 altında kullanabiliriz; dosya başlığındaki telif bildirimleri korunmalı. mac80211/cfg80211 ise GPL-only, ama bize gerekmiyor.

OpenBSD `if_iwx.c`: ISC lisansı ve *"Based on BSD-licensed source modules in the Linux iwlwifi driver… dual BSD/GPLv2"* notu. itlwm deposunun tamamı **GPL-2.0** (GitHub lisans alanı).

---

## 2. Owly (t4t5/omdrop-owl → t4t5/owly) Intel'de nasıl çalışıyor

Depo yeniden adlandırılmış: https://github.com/t4t5/owly (eski URL yönleniyor). Rust ile yazılmış, MIT lisanslı. seemoo-lab/OWL'un (C, GPL-3.0 **[doğrulanmadı]**) halefi.

### 2.1 Kart durumu (`crates/owly-radio/drivers.toml`) **[doğrulandı]**
- `[drivers.iwlwifi]`: `status="tested"`, `mode="exclusive"`, `channels=[6]`.
- Test edilen kartlar: AX200 (6.19.8), AX201 (6.8 / 7.2.9), **AX211 (8086:51f1 dahil)**, BE200, Panther Lake BE, 9560, 9260.
- **AX203 (So+HR2) listede yok.** Ama MAC'i (So, 51F1) AX211 üzerinden, RF'si (HR2) AX201 üzerinden test edilmiş. AX203'ün çalışacağı makul bir beklenti. **[doğrulanmadı]**
- Bozuk kartlar: 7265 (ad-hoc başlarken firmware assert), 8260, 8265.

Notlardan önemli alıntılar:
- *"Associated, the firmware passes up no AWDL frames."* Yani bağlıyken AWDL çerçeveleri yukarı gelmiyor; exclusive mode bu yüzden şart.
- *"With the station down the firmware forgets its country and is back on the world's regulatory domain, where all of 5 GHz is No IR."* Bu yüzden 5 GHz'de IBSS EINVAL veriyor.
- Ülke öğretilince AX201 149'da cell açabiliyor, ama iPhone 5 GHz cell'e hiç bağlanmamış, 6'yı 3 saniye içinde bulmuş.
- *"Some AX211s refuse an ad-hoc interface beside the station, so the station becomes the cell."*
- `tx_noack` (exclusive mode'da varsayılan **true**): unicast tek sefer, ACK beklemeden gönderiliyor. Gerekçe: *"Intel and MT76x2U TX status also stalls after channel changes"* ve tekrar denenen bir çerçeve arkadakileri bekletiyor.

### 2.2 Kurulum sırası (`crates/owly-radio/src/injection/setup.rs`, `exclusive()` + `ibss_cell()`) **[doğrulandı]**
1. Bağlantıyı kaydediyor ve NetworkManager'ı bırakıyor (`set_managed false`). İstasyon arayüzünü down yapıyor.
2. Regdom: ayarlıysa `country` uygulanıyor.
3. `add_iface(phy, IBSS, Adhoc)`: ayrı bir ad-hoc arayüz açıyor. Reddedilirse istasyonun tipini ad-hoc'a çeviriyor (nl80211 `NEW_INTERFACE` / `SET_INTERFACE`, iftype ADHOC).
4. `cell_mac`: cell'e **istasyonun MAC'ini** veriyor. Telefon bu adrese gönderdiği için ACK bu adres sayesinde geliyor.
5. IPv6'yı cell'de kapatıyor, up yapıyor.
6. Her frekans için `join_ibss(cell, f)` (nl80211 `JOIN_IBSS`, BSSID `00:25:00:ff:94:73`, **HT** cell). Gerekçe yorumda: iPhone unicast'i HT hızında gönderiyor, HT olmayan cell bunları ne alıyor ne ACK'liyor. mac80211 cell'i başlatmadan önce kanalı bir kez tarıyor; Owly 10 saniye bekliyor.
7. `monitor(Kind::Monitor{active:false}, sta_mac)`: istasyonun MAC'iyle pasif bir monitor arayüzü ekliyor. AWDL RX ve TX bunun üzerinden yapılıyor (radiotap ile pcap benzeri soket).
8. Enjeksiyon radiotap'i (`radiotap.rs`): `TX_FLAGS` alanında `F_TX_NOACK = 0x0008`, `RATE` (legacy) ya da `MCS` (20 MHz, bant genişliği/MCS/GI "known" bitleri).

Mekanizma (mac80211 davranışı, **[doğrulanmadı ama tutarlı]**): monitor arayüzünden enjekte edilen çerçevenin `addr2`'si (TA) aynı kanaldaki IBSS vif'inin MAC'iyle eşleşiyor. mac80211 bu durumda çerçeveyi o vif üzerinden sürücüye veriyor. mvm tarafında olan:
- Yönetim/multicast çerçeveler: `iwl_mvm_tx_skb_non_sta()` içinde ADHOC dalında `bcast_sta` (veri değilse) ya da `mcast_sta` (veriyse) kullanılıyor (`mvm/tx.c:817–827`).
- Peer'e giden unicast: IBSS istasyon girdisi üzerinden gidiyor.
- Hız: radiotap'ten `iwl_mvm_get_inject_tx_rate()` ile `rate_n_flags`'e çevriliyor (`mvm/tx.c:294`).

Ek bilgiler:
- iwlwifi modül parametresi ya da kernel yaması **gerekmiyor**; drivers.toml'da iwlwifi için hiçbiri yok. Owly kodunda geçen "patch owl-05" eski OWL'un noack davranışına bir atıf, kernel yaması değil.
- Zamanlama: AW = 16 TU, slot = 4 AW = 64 TU, kanal dizisi 16 slot. Unicast sadece paylaşılan AW içinde ve kenarlarda 3 TU pay bırakılarak gönderiliyor. Multicast 16 TU pay ile slot 0 ve 10'da gidiyor (`docs/SPEC.md` AWDL-16).
- iPhone kanal 6'ya 16 slotun 1–2'sinde, 44/149'a ise birkaçında uğruyor. Kanal 6'nın yavaş olmasının sebebi bu.
- Sınırlamalar: kanal 6 yavaş (fotoğraf 10–17 s); telefonda "Everyone for 10 minutes" seçili olmalı; `awdl0` üzerinde TCP 8771 açık olmalı.

### 2.3 Bizim sürücü için çıkan "minimum firmware senaryosu"

| Linux/Owly'de | Bizim sürücüde (firmware komutu) |
|---|---|
| ad-hoc vif + JOIN_IBSS (HT, ch6, BSSID AWDL, MAC = bizim) | `MAC_CONTEXT_CMD` (ya da MLD API'de `MAC_CONFIG_CMD`) `FW_MAC_TYPE_IBSS`, filtreler `IN_BEACON\|IN_PROBE_REQUEST\|ACCEPT_GRP`, gerekirse `IN_PROMISC\|IN_CONTROL_AND_MGMT` ek denemesi; `PHY_CONTEXT_CMD` (ch6 HT20); `BINDING_CONTEXT_CMD`; bcast/mcast internal station (`ADD_STA` + kuyruk) |
| monitor vif (RX) | IBSS MAC'inin RX'i ve/veya ikinci bir `FW_MAC_TYPE_LISTENER` MAC'i (mvm "snif" yolu, `iwl_mvm_mac_ctxt_cmd_listener` → `IN_PROMISC\|CONTROL_AND_MGMT\|BEACON\|PROBE_REQ\|CRC32\|ACCEPT_GRP`) |
| radiotap TX | gen3 TX cmd (`iwl_tx_cmd_gen3`): `IWL_TX_FLAGS_CMD_RATE` + `rate_n_flags`, ACK'siz gönderim (TX cmd'de ACK beklentisi yok) |
| IBSS beacon'ı | `BEACON_TEMPLATE_CMD`. Gerekli mi? IBSS MAC'i beacon şablonu olmadan firmware'i assert ettirir mi? **[doğrulanmadı]** Owly'de cell aralığı 10000 TU, yani beacon neredeyse hiç gitmiyor |

**Güçlü öneri (Faz 0):** Bu laptopta Linux live USB ile Owly'yi exclusive modda çalıştırın ve `trace-cmd record -e iwlwifi:* -e iwlwifi_msg:*` ile **tam host-command dizisini** kaydedin. iwlwifi'nin `iwlwifi_dev_hcmd` tracepoint'i komut ID'si ve payload'u veriyor. Bu kayıt Windows sürücüsü için birebir "altın referans" olur: hangi komut, hangi sürüm, hangi alanlar. Aynı adımda AX203'te Owly'nin gerçekten çalıştığı da kanıtlanmış olur.

---

## 3. Port edilmesi gereken minimum iwlwifi kod yolu

Satır sayıları Linux master'daki dosyaların toplamı (wc -l). "Gerekli kısım" sütunu tahmindir.

### 3.1 PCIe bring-up (gen2 transport, AX210 ailesi = "context info v2/gen3")

| Dosya | Toplam | Gerekli (≈) | Ana fonksiyonlar |
|---|---|---|---|
| `pcie/trans.c` | 4386 | 1200 | `iwl_trans_pcie_alloc`, `iwl_pcie_prepare_card_hw`, `iwl_trans_pcie_sw_reset`, `_iwl_trans_pcie_start_hw`, `iwl_pcie_set_interrupt_capa` (MSI-X tablo eşlemesi), `iwl_trans_pcie_grab_nic_access`, `iwl_pcie_conf_msix_hw`, prph/mem read-write |
| `pcie/trans-gen2.c` | 672 | 450 | `iwl_pcie_gen2_apm_init`, `iwl_pcie_gen2_nic_init`, `iwl_trans_pcie_gen2_start_fw`, `iwl_pcie_spin_for_iml`, `iwl_trans_pcie_gen2_fw_alive`, `_iwl_trans_pcie_gen2_stop_device` |
| `pcie/ctxt-info-v2.c` | 632 | 350 | `iwl_pcie_ctxt_info_v2_alloc` (PRPH scratch + PRPH info + context info v2 + **IML** DMA'ya kopyalanıyor), `iwl_pcie_ctxt_info_v2_kick`, `_free` |
| `pcie/ctxt-info.c` | 270 | 120 | `iwl_pcie_init_fw_sec` (fw bölümleri → DRAM; gen3 de kullanıyor) |
| `pcie/internal.h` + `iwl-csr.h` + `iwl-prph.h` + `iwl-fh.h` | ~3180 | ~1200 | register tanımları |
| `iwl-trans.c` | 1008 | 200 | ince sarmalayıcı |

Akış (gen3): APM init → NIC init (RX/TX kuyruk DMA'ları) → fw bölümleri DRAM'e → context info v2 (RX/TX ring adresleri, fw bölüm tablosu, IML) → `CSR_CTXT_INFO_ADDR` / `UREG_CPU_INIT_RUN` kick → IML spin → firmware ALIVE notification. `low_latency_xtal` / `xtal_latency=12000` ve LTR ayarları `iwl_pcie_gen2_apm_init` ile PRPH scratch üzerinden uygulanıyor.

### 3.2 Firmware yükleme ve ALIVE

| Dosya | Toplam | Gerekli | Not |
|---|---|---|---|
| `iwl-drv.c` | 2226 | 900 | `.ucode` TLV parser (`iwl_parse_tlv_firmware`): IML, bölümler, `IWL_UCODE_TLV_API/CAPA`, `CMD_VERSIONS` (hangi komut hangi sürümde), `fw_name_pre` |
| `fw/img.c`, `fw/file.h` | 1236 | 500 | TLV tanımları, `iwl_fw_lookup_cmd_ver` |
| `fw/notif-wait.c` | 135 | 135 | ALIVE / init-complete beklemesi |
| `fw/paging.c` | 296 | 0–200 | gen3'te paging context info'ya dahil mi? **[doğrulanmadı]** |
| `fw/pnvm.c`, `fw/uefi.c` | 1817 | 0 | HR'de PNVM yok |
| `mvm/fw.c` | 2025 | 700 | `iwl_mvm_load_ucode_wait_alive` (`:286`), `iwl_run_unified_mvm_ucode` (`:598`), `iwl_mvm_up` (`:1734`): `INIT_EXTENDED_CFG`, `NVM_ACCESS_COMPLETE`, `iwl_send_phy_cfg_cmd`, `iwl_send_tx_ant_cfg`, BT coex config, `SOC_CONFIGURATION_CMD` (xtal latency), `MCC_UPDATE`, `RSS/RX queue config`. SAR/PPAG/TAS/geo (ACPI) **atlanacak** |

### 3.3 NVM / regülasyon
- `iwl-nvm-parse.c` (2238 satır, ≈600 gerekli): `NVM_GET_INFO` yanıtı (MAC adresi, anten, bant/kanal bayrakları), `iwl_parse_nvm_mcc_info` / MCC yanıtından kanal bayrakları.
- `mvm/nvm.c` (686 satır, ≈250 gerekli): `iwl_mvm_update_mcc` / `MCC_UPDATE_CMD`.
- **Kritik:** 5 GHz No-IR sorunu. Firmware LAR (location-aware regulatory) kullanıyor. Bizim sürücü doğrudan `MCC_UPDATE_CMD` ile "TR" gönderebilir. Firmware kabul ederse 5 GHz'de TX mümkün olabilir; Owly'nin Linux'ta gördüğü sorun buydu. **[doğrulanmadı]**

### 3.4 MAC context / PHY context / binding / station
| Dosya | Toplam | Gerekli | Fonksiyonlar |
|---|---|---|---|
| `mvm/mac-ctxt.c` | 1934 | 400 | `iwl_mvm_mac_ctxt_cmd_common` (`:537`), `_cmd_ibss` (`:771`), `_cmd_listener` (`:734`), `iwl_mvm_mac_ctxt_send_beacon_v9` (IBSS beacon gerekirse) |
| `mvm/phy-ctxt.c` | 395 | 200 | `iwl_mvm_phy_ctxt_add/changed` → `PHY_CONTEXT_CMD` (ADD/MODIFY, kanal + genişlik + rx chains) |
| `mvm/binding.c` | 176 | 120 | `BINDING_CONTEXT_CMD` (MAC ↔ PHY) |
| `mvm/sta.c` | 4321 | 700 | `iwl_mvm_add_int_sta_common`, `iwl_mvm_send_add_bcast_sta`, `iwl_mvm_add_mcast_sta`, `iwl_mvm_add_snif_sta`, TVQM kuyruk ayırma (`iwl_mvm_tvqm_enable_txq` → `SCD_QUEUE_CONFIG_CMD`) |
| `mvm/link.c`, `mld-mac.c`, `mld-sta.c` | ~1500 | 0–600 | **Firmware `IWL_UCODE_TLV_CAPA_MLD_API_SUPPORT` bildirirse** mvm, `MAC_CONFIG_CMD` / `LINK_CONFIG_CMD` / `STA_CONFIG_CMD` yollu MLD API'yi kullanıyor (`mvm/ops.c:1226,1433`). so-hr-b0-77/89'un bunu bildirip bildirmediği bilinmiyor. Yüklemede TLV'den okunmalı **[doğrulanmadı]**. OpenBSD iwx -77 ile eski `MAC_CONTEXT_CMD` yolunu kullanıyor, -77'de eski API'nin kabul edildiği belli |
| `mvm/time-event.c` | 1492 | 0–300 | Sabit kanalda gerekmez. Kanal değişiminde ya da ROC'ta `SESSION_PROTECTION_CMD` / `TIME_EVENT_CMD` |

### 3.5 RX yolu (tüm çerçeveler + metadata)
- `pcie/rx.c` (2496 satır, ≈900 gerekli): multi-queue RX, RBD/used ring (AX210'da `iwl_rx_transfer_desc` / `iwl_rx_completion_desc`), RB yeniden doldurma, `iwl_pcie_rx_handle`, MSI-X ISR'ları (`iwl_pcie_irq_rx_msix_handler`, `iwl_pcie_irq_msix_handler`).
- `mvm/rxmq.c` (2634 satır, ≈600 gerekli): `iwl_mvm_rx_mpdu_mq`. `iwl_rx_mpdu_desc` (AX210 için v3) alanlarını içeriyor: `mpdu_len`, `status` (CRC/overrun), `rate_n_flags`, `energy_a/b`, `channel`, `gp2_on_air_rise`, `tsf_on_air_rise`, `phy_info`. Radiotap bilgisi buradan üretiliyor (`iwl_mvm_rx_fill_status`, `iwl_mvm_rx_he` atlanabilir). Reorder buffer, crypto ve HE-SIG ayrıntıları atlanabilir.
- `fw/api/rx.h` (1446 satır) tanımları.
- Bize gereken: kendi metadata başlığımız (freq, rssi, rate, flags, gp2/tsf) ve ardından ham 802.11 çerçevesi. Ya da doğrudan radiotap üretiriz; Owly/OWL zaten radiotap okuyor.

### 3.6 TX yolu (enjeksiyon)
- `pcie/tx-gen2.c` (1437 satır, ≈700 gerekli): TFH TFD (`iwl_tfh_tfd`), `iwl_txq_gen2_tx`, `iwl_txq_gen2_build_tfd`, byte-count tablosu (AX210 bayt cinsinden), host komut kuyruğu `iwl_pcie_gen2_enqueue_hcmd`.
- `pcie/tx.c` (2736 satır): ortak txq alloc/reclaim kısmı ≈500.
- `mvm/tx.c` (2318 satır, ≈500 gerekli):
  - `iwl_mvm_set_tx_params` (gen3 TX cmd: `offload_assist`, `flags`, `rate_n_flags`)
  - `iwl_mvm_get_inject_tx_rate` (`:294`, legacy/HT/VHT → `rate_n_flags`)
  - `iwl_mvm_tx_skb_non_sta` (`:770`)
  - TX yanıtı `iwl_mvm_rx_tx_cmd` (`:1894`, status: ACK/fail)
- **Hangi station id:** ADHOC vif'te veri olmayan çerçeveler için `bcast_sta`, veri çerçeveleri için `mcast_sta`. MONITOR vif'te `snif_sta` + `snif_queue`. Bizim minimal tasarımda 1 internal station + 1 TX kuyruğu ile her şeyi açık rate'le ve ACK'siz göndermek ilk hedef olmalı.
- ACK'li ve retry'lı unicast için firmware'in peer'e ait bir station girdisi isteyip istemediği bilinmiyor **[doğrulanmadı]**. Owly zaten noack kullanıyor.

### 3.7 Hızlı kanal değişimi (opsiyonel M4)
- `PHY_CONTEXT_CMD` action MODIFY ile kanal değiştirilir; binding ve MAC aynı kalır. Gecikme ölçülmedi. Muhtemelen birkaç ms, ama AWDL'nin 16 TU (16.4 ms) AW sınırları için yetip yetmeyeceği **bilinmiyor**.
- Owly notu: Intel'de kanal değişiminden sonra TX status takılıyor.
- Alternatif: firmware'in kendi zaman çizelgesi (time event / session protection). Ama bunlar AWDL'ye göre tasarlanmamış.
- Pratik plan: önce sabit ch6 (Owly'nin kanıtlanmış modeli), sonra deneysel 6↔44/149.

### 3.8 Toplam LoC tahmini

| Bileşen | Linux'tan minimal port (≈ satır) |
|---|---|
| PCIe transport + context info + IML + MSI-X | 3,000 |
| Firmware parse / ALIVE / notif-wait | 1,700 |
| MVM init (`iwl_mvm_up` alt kümesi), NVM/MCC | 1,800 |
| MAC/PHY/binding/station/kuyruk | 1,500 |
| RX (pcie + mpdu → metadata) | 1,500 |
| TX (pcie gen2 + tx cmd + status) | 1,700 |
| fw/api başlıkları (sadece kullanılan struct'lar) | 2,500 |
| **Port edilen toplam** | **≈ 13–14k** (yüksek belirsizlik, ±40%) |
| Windows KMDF iskeleti, IOCTL, firmware dosya okuma, DMA/IRQ uyarlaması, log | 3–5k |
| **Genel toplam** | **≈ 16–19k satır C** |

Karşılaştırma: OpenBSD `if_iwx.c` 12,903 satır, `if_iwxreg.h` 8,961, `if_iwxvar.h` 858. Bunlara net80211 entegrasyonu, scan, rate control, AMPDU, WoWLAN vb. dahil. Gereksizler atılınca iwx tabanlı minimal port **≈ 7–9k satır + başlıklar** olur ve Linux mvm'den belirgin şekilde küçüktür.

---

## 4. Windows tarafı

### 4.1 KMDF PCIe sürücüsünün temelleri
- **Yapı:** NDIS ya da WiFiCx miniport **değil**, düz bir KMDF function driver. User-mode erişimi `WdfDeviceCreateDeviceInterface(GUID_AWDLRADIO)` ile sağlanır. IOCTL'ler (öneri):
  - `SET_CHANNEL`, `START_IBSS`/`STOP`, `SET_MAC`, `SET_MCC`
  - `TX_FRAME` (METHOD_IN_DIRECT; metadata + 802.11 çerçeve)
  - `RX_FRAMES` ("inverted call": manuel kuyrukta bekleyen IRP'ler; ya da user-mode'a map edilmiş paylaşımlı ring)
  - `GET_STATS`, `GET_TSF/GP2`
- **BAR eşleme:** `EvtDevicePrepareHardware` içinde `CmResourceTypeMemory` → `MmMapIoSpaceEx(..., PAGE_READWRITE|PAGE_NOCACHE)`; erişim `READ/WRITE_REGISTER_ULONG`. `EvtDeviceReleaseHardware` içinde `MmUnmapIoSpace`.
- **Kesmeler:** INF'te `Interrupt Management\MessageSignaledInterruptProperties` altında `MSISupported=1` ve `MessageNumberLimit` (ör. 16). KMDF bir mesaj kaynağı başına bir `WDFINTERRUPT` ister; çoklu mesajda `WdfInterruptCreate` `EvtDevicePrepareHardware` içinden raw/translated kaynakla çağrılır (KMDF ≥1.11). iwlwifi MSI-X'te "default" vektörü (FH/HW olayları) ve RX kuyruk vektörlerini ayırıyor. **İlk aşamada 1–2 vektör yeterli**: tek RX kuyruğu, `iwl_pcie_set_interrupt_capa` mantığı. ISR'da kesmeyi maskeleyip `EvtInterruptDpc`'de işlemek gerekiyor.
- **DMA:** `WdfDeviceSetAlignmentRequirement` + `WdfDmaEnablerCreate(WdfDmaProfileScatterGather64Duplex)`. Halkalar, context info, PRPH scratch, IML, byte-count tabloları ve **firmware bölümleri** (DRAM'de kalmalı; her biri yüzlerce KB) için `WdfCommonBufferCreate(WithConfig)`. RX buffer'ları için de common buffer havuzu kullanılabilir (4 KB RB × birkaç yüz; Linux AX210 HE'de 4096 RBD kullanıyor, biz 256–512'ye indirebiliriz). Bellek `NonPagedPoolNx`; HVCI uyumu için RWX olmamalı.
- **Firmware dosyası:** `.ucode` sürücü dizininden (`\SystemRoot\System32\drivers\` ya da DriverStore) `ZwCreateFile`/`ZwReadFile` ile PASSIVE_LEVEL'de okunur. INF `CopyFiles` ile kurulur.
- **Güç:** İlk sürümde idle power-down kapalı (`WdfDeviceAssignS0IdleSettings` yok), D0Entry/D0Exit'te firmware'i durdurup yeniden başlatmak yeterli. **Risk:** CNVi cihazı D3'e geçmezse Modern Standby (S0ix) bozulabilir, uykuda pil tüketimi artabilir. **[doğrulanmadı]**
- **BT coex:** CNVi'de Bluetooth ayrı bir USB fonksiyonu ve Intel BT sürücüsünde kalır. Firmware init'inde BT coex config (`iwl_mvm_send_bt_init_conf`) gönderilmeli.
- **Hata ayıklama:** Wi-Fi kartını biz alacağımız için KDNET'i bu kartla kullanamayız. USB-Wi-Fi ve çoğu USB-Ethernet KDNET'i desteklemiyor **[doğrulanmadı, desteklenen NIC listesi kontrol edilmeli]**. Pratik yol: `DbgPrint`/WPP + DebugView ya da `!wmitrace`, tam bellek dökümü (complete memory dump) ve WinDbg ile post-mortem. Firmware hatalarında Linux'taki gibi `iwl_fwrt_dump_error_logs` mantığıyla SRAM'den error table okumak (`ERROR_EVENT_TABLE`) çok değerli; bunu M1'e koyun.

### 4.2 Cihazı Intel sürücüsünden almak ve geri vermek
- **INF:** `[Manufacturer]` altında en özel donanım kimliği:
  `PCI\VEN_8086&DEV_51F1&SUBSYS_02748086` (gerekirse `&REV_xx`).
  Sınıf olarak `Net` **kullanmayın**; özel bir setup class ya da `System` kullanın, yoksa NDIS beklentileri devreye girer.
- **Kurulum:** `pnputil /add-driver awdlradio.inf /install` çalıştırın. Ama Windows sürücü sıralamasında WHQL imzalı Intel sürücüsü test imzalı sürücüden **önce gelir**, yani otomatik seçim Intel'i tutar. Bu yüzden Aygıt Yöneticisi → Sürücüyü güncelleştir → "Bilgisayarımdaki sürücüler listesinden seçeyim" → "Disketi var" ile elle seçmek gerekiyor. Hem donanım kimliği eşleşmesi hem imza puanı sıralamayı etkiler. **[doğrulanmadı: tam rank hesabı]**
- **Windows Update'in geri yüklemesini engelleme:** Grup İlkesi "Device Installation Restrictions" (cihaz örnek kimliğine göre engelleme ilkesi bizim kurulumumuzu da engeller, dikkatli olun) ya da "Windows Update ile sürücüleri dahil etme" ilkesi.
- **Geri dönüş:** `pnputil /enum-drivers` ile bizim `oemNN.inf` bulunur, `pnputil /delete-driver oemNN.inf /uninstall /force` ile silinir, ardından `pnputil /scan-devices` çalıştırılır. Intel paketi (`Netwtw*.inf`, sürüm laptopa göre değişir) DriverStore'da kaldığı için otomatik geri yüklenir. Önceden **sistem geri yükleme noktası** oluşturun ve Intel paketini `pnputil /export-driver` ile yedekleyin.
- İnternet trafiği ayrı USB Wi-Fi adaptöründen gideceği için Intel NIC'in kaybolması sorun değil.

### 4.3 Test imzalama (Windows 11)
- `bcdedit /set testsigning on` **Secure Boot açıkken reddedilir** ("The value is protected by Secure Boot policy"). Microsoft dokümanı önce UEFI'den Secure Boot'u kapatmayı söylüyor, değişiklik yeniden başlatmadan sonra etkin olur:
  https://learn.microsoft.com/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- **BitLocker:** Secure Boot kapatılınca BitLocker kurtarma anahtarı istenir. Anahtarı önceden yedekleyin ya da BitLocker'ı askıya alın (`manage-bde -protectors -disable C:`).
- **Memory Integrity (HVCI):** test imzalı sürücü bir test sertifikasıyla imzalı olmalı (`signtool` + `makecert`/`New-SelfSignedCertificate`) ve HVCI uyumlu olmalı (NX pool, W^X).
- Kalıcı dağıtım için test imzalama uygun değil. Alternatif Microsoft attestation signing (EV sertifika + Partner Center, yıllık ücretli), ya da kullanıcıların test modunda kalması.
- Kaynak: https://community.osr.com/t/windows-11-driver-development-and-secure-boot/59576

### 4.4 Önceki çalışmalar
| Proje | Platform | 51F1 / So+HR2 desteği | Lisans | Değerlendirme |
|---|---|---|---|---|
| **Linux iwlwifi (mvm)** | Linux | Var (`so_long_latency_imr` + AX203) **[doğrulandı]** | GPL-2.0 OR BSD-3 | Referans uygulama ve en güncel firmware API (fw -89). IBSS/monitor/inject hepsi var. Ama mac80211'e sıkı bağlı ve büyük (mvm ≈ 40k satır) |
| **OpenBSD iwx** (`sys/dev/pci/if_iwx.c`) | OpenBSD | **Var**: `PCI_PRODUCT_INTEL_WL_22500_17 = 0x51f1`; So+HR2 → `iwx_cfg_so_a0_hr_b0` (/* AX203 */), fw `iwx-so-a0-hr-b0-77`; `sc_imr_enabled=1`, `xtal_latency=12000`, LTR 2500 **[doğrulandı]** | ISC (+ dual BSD/GPL notu) | **Tek dosya, mac80211'siz**, gen3 context info + IML + PNVM kodu içeriyor, MONITOR modu var (`FW_MAC_TYPE_LISTENER`, `IWX_MONITOR_STA_ID`). **IBSS yok** ("IBSS channel undefined"), 802.11ax rate yok (bize gerekmiyor) |
| **itlwm** (OpenIntelWireless) | macOS (IOKit) | Var: `0x51F1` → `iwl_so_long_latency_trans_cfg`, "So with Hr" → `iwl_cfg_so_a0_hr_a0` / `iwl_ax203_name`, fw `so-a0-hr-b0-68` **[doğrulandı]** | **GPL-2.0** | OpenBSD iwx'in **yabancı bir çekirdeğe taşınmış hali**. DMA/IRQ/firmware uyarlaması için ders niteliğinde. Son push 2024-07, fw -68 eski. GPL olduğu için kodu kopyalarsak sürücümüz GPL olur |
| **FreeBSD iwlwifi** (LinuxKPI) + **FreeBSD iwx** (15.0) | FreeBSD | Linux kodu olduğu için var. iwx Haiku üzerinden OpenBSD'den gelmiş | dual / ISC | LinuxKPI yaklaşımı (Linux kodunu shim ile derlemek) Windows'ta çok ağır olur. iwx portu ise iwx'in taşınabilirliğinin ikinci kanıtı |
| **Haiku** | Haiku | iwx'i FreeBSD compat katmanıyla taşıdı (R1/beta4) | | Aynı ders |
| Windows | | Bilinen bir iwlwifi/iwx Windows portu **bulamadım** | | Saha boş |

Kaynaklar: https://man.openbsd.org/iwx , https://github.com/openbsd/src/blob/master/sys/dev/pci/if_iwx.c , https://github.com/OpenIntelWireless/itlwm , https://people.freebsd.org/~ziaee/tmp/iwx.4.pdf

### 4.5 Önerilen taban kod
**Birincil iskelet: OpenBSD `if_iwx.c` + `if_iwxreg.h` (ISC).** Referans ve eksikler için: **Linux iwlwifi mvm/pcie (BSD-3 seçeneğiyle)**.

Gerekçe:
1. 0x51F1 ve So+HR2 (AX203) zaten tanımlı, gen3 context-info/IML/LTR/xtal/IMR ayrıntıları dahil.
2. mac80211 bağımlılığı yok, kod tek dosyada ve sıralı. Windows'ta net80211'i atıp kendi ince katmanımızı koymak kolay.
3. itlwm ve Haiku/FreeBSD portları, iwx'in yabancı bir çekirdeğe (IOKit, FreeBSD) taşınabildiğini kanıtlıyor.
4. ISC ve BSD-3 lisansları sürücüyü istediğimiz lisansla dağıtmamıza izin veriyor. itlwm kodu GPL olduğu için **sadece okunur, kopyalanmaz**.

Linux'tan alınacaklar:
- `iwl_mvm_mac_ctxt_cmd_ibss` mantığı (iwx'te IBSS yok)
- inject rate dönüşümü
- `iwl_rx_mpdu_desc` v3'ten metadata çıkarma
- komut sürüm tabloları

Firmware: önce iwx'in test ettiği **-77** sürümüne sabitleyin; komut sürümlerini `iwx_lookup_cmd_ver` ile TLV'den okuyun. Daha sonra gerekirse -89'a geçin.

---

## 5. Riskler / bilinmeyenler
1. **IBSS MAC context, Linux mac80211 olmadan firmware'de ayağa kalkar mı?** Beacon şablonu, TSF, `ADD_STA` sırası belirsiz. Faz 0'daki trace kaydı bu riski büyük ölçüde azaltır.
2. **AX203 (So+HR2) Owly'de test edilmemiş.** Bileşenleri ayrı ayrı test edilmiş (51F1/AX211, HR2/AX201).
3. **Firmware API farkları** (-77 ile -89 arası, MLD API capa). Yanlış komut sürümü SYSASSERT demek.
4. **Regülasyon:** 5 GHz No-IR. Kanal 6 dışı TX, MCC kabulüne bağlı.
5. **Windows'a özgü:** Secure Boot kapatma + BitLocker; HVCI; Windows Update'in Intel sürücüsünü geri getirmesi; Modern Standby/D3; BSOD riski (DMA hatası belleği bozabilir; IOMMU/DMA remapping açıksa `DmaGuard` politikası müdahale edebilir **[doğrulanmadı]**).
6. **Zamanlama:** AWDL unicast'i peer'in kanal-6 slotlarına (1–2/16) ve AW kenarlarından 3 TU içeride göndermek gerekiyor. Windows user-mode zamanlayıcı çözünürlüğü (~0.5–1 ms, `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) muhtemelen yeterli, ama TX kuyruğu gecikmesi ölçülmeli. Gerekirse zamanlamayı sürücüye taşımak (GP2 tabanlı) gerekebilir.
7. **Hız:** Kanal 6'da 2.4 MB için ~10–17 s. Kullanıcı deneyimi sınırlı. 5 GHz'e geçmek (M4) belirsiz.
8. **Lisans:** Firmware tersine mühendislik yasağı. Firmware'i sadece belgelenmiş host-command arayüzüyle kullanın.
9. **IPv6 / `awdl0`:** OWL/Owly bir TUN arayüzü kullanıyor. Windows'ta Wintun (https://www.wintun.net) ya da benzeri bir sanal adaptör gerekecek; bu ayrı bir iş kalemi.
10. **User-mode tabanı:** seemoo-lab/OWL (C, GPL-3.0 **[doğrulanmadı]**) yerine **Owly** (Rust, MIT, aktif, Intel exclusive mode bilgisi içinde) daha iyi bir taban olabilir. M5 öncesi karar verilmeli.

---

## 6. Fazlı plan

**Faz 0 (1 hafta), Linux'ta altın referans**
- Live USB'de Owly'yi exclusive modda çalıştırıp iPhone ile gönderip almayı deneyin (AX203'te çalıştığını kanıtlamak için).
- `trace-cmd` ile iwlwifi host-command ve notification dizisini kaydedin. dmesg'den firmware sürümü, TLV capa'lar, `CSR_HW_REV` / `CSR_HW_RF_ID` değerleri alınır.
- Firmware'i -77 ile zorlamak isteğe bağlı (iwlwifi'nin ucode seçimi dosya varlığına göre çalışıyor).

**M1, sürücü yükleniyor + firmware ALIVE (3–5 hafta)**
- KMDF iskeleti, INF (`DEV_51F1&SUBSYS_02748086`), BAR map, MSI-X/MSI, common buffer'lar.
- iwx'ten: `iwx_prepare_card_hw`, `apm_init`, `ctxt_info_gen3_init`, IML, `iwx_start_fw`, ALIVE.
- Firmware error table dökümü.
- Ardından `INIT_EXTENDED_CFG`, `NVM_GET_INFO` (MAC adresi okunuyor), `PHY_CFG`, `TX_ANT`, `SOC_CONFIG`, BT coex, MCC.
- Başarı ölçütü: ALIVE + init complete, firmware sürüm dizgesi loglanıyor, D0Exit/D0Entry ile temiz yeniden başlatma.

**M2, sabit kanalda monitor RX (2–3 hafta)**
- PHY context (ch6 HT20), LISTENER MAC + binding (iwx MONITOR yolu).
- RX kuyruğu → kendi metadata başlığımız (freq/rssi/rate/gp2/tsf) → IOCTL ile user-mode'a.
- Basit bir pcap yazıcı aracı.
- Başarı ölçütü: Wireshark'ta beacon'lar ve iPhone AWDL action frame'leri (BSSID `00:25:00:ff:94:73`) görünüyor.

**M3, enjeksiyon + ACK (3–4 hafta)**
- TX kuyruğu (TVQM), internal station, gen3 TX cmd (`CMD_RATE`, açık `rate_n_flags`), TX status IOCTL.
- Ardından LISTENER yerine (ya da yanına) **IBSS MAC** (BSSID AWDL, MAC = kartın MAC'i). Telefonun unicast'ine ACK verildiği ikinci bir sniffer ile (USB adaptör monitor modda) doğrulanır.
- Başarı ölçütü: enjekte edilen AWDL action frame havada görünüyor, telefonun unicast'i ACK'leniyor.

**M4, AWDL zamanlaması (2–4 hafta)**
- (a) Sabit ch6 modeli (Owly gibi): GP2/TSF'yi user-mode'a vermek, AW-hizalı TX zamanlaması ve ölçümleri.
- (b) Deneysel: `PHY_CONTEXT_CMD MODIFY` ile 6↔44/149 geçiş gecikmesi ölçümü ve 5 GHz için MCC denemesi.
- Başarı ölçütü (a): iPhone peer'ı geçerli sayıyor, mDNS/TCP 8771 bağlantısı kuruluyor.

**M5, OWL/Owly entegrasyonu (4–8 hafta)**
- Owly'nin `owly-radio` backend'inin yerine Windows IOCTL backend'i.
- `awdl0` için Wintun.
- Discover/Ask/Upload akışı ve AirDrop gönder/al.
- Başarı ölçütü: iPhone → PC ve PC → iPhone dosya aktarımı.

Toplam kaba tahmin: tek geliştirici, kernel deneyimiyle **4–6 ay**. Asıl belirsizlik M1 ve M3'teki firmware davranışı.

---

## Kaynaklar
- iwlwifi pcie/drv.c: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/drivers/net/wireless/intel/iwlwifi/pcie/drv.c
- cfg/ax210.c, cfg/rf-hr.c, iwl-drv.c, iwl-config.h: aynı ağaçta (`drivers/net/wireless/intel/iwlwifi/`); Elixir: https://elixir.bootlin.com/linux/latest/source/drivers/net/wireless/intel/iwlwifi
- mvm/tx.c, mvm/mac-ctxt.c, mvm/fw.c, mvm/ops.c, pcie/ctxt-info-v2.c, pcie/trans-gen2.c: aynı ağaç
- linux-firmware: https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/intel/iwlwifi , WHENCE: https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/WHENCE
- Owly: https://github.com/t4t5/owly ; https://github.com/t4t5/owly/blob/HEAD/crates/owly-radio/drivers.toml ; https://github.com/t4t5/owly/blob/HEAD/crates/owly-radio/src/injection/setup.rs ; https://github.com/t4t5/owly/blob/HEAD/crates/owly-radio/src/radiotap.rs ; https://github.com/t4t5/owly/blob/HEAD/docs/SPEC.md
- OWL: https://github.com/seemoo-lab/owl
- OpenBSD iwx: https://github.com/openbsd/src/blob/master/sys/dev/pci/if_iwx.c , https://github.com/openbsd/src/blob/master/sys/dev/pci/if_iwxvar.h , https://man.openbsd.org/iwx
- itlwm: https://github.com/OpenIntelWireless/itlwm (hal_iwx/ItlIwx.cpp)
- FreeBSD iwx: https://people.freebsd.org/~ziaee/tmp/iwx.4.pdf
- MS testsigning: https://learn.microsoft.com/windows-hardware/drivers/install/the-testsigning-boot-configuration-option ; OSR: https://community.osr.com/t/windows-11-driver-development-and-secure-boot/59576 ; HVCI/test-sign: https://www.itechguides.com/?p=329232 ; Secure Boot politikası: https://www.thewindowsclub.com/value-is-protected-by-secure-boot-policy-and-cannot-be-modified.
- Wintun: https://www.wintun.net
