# NitroDrop USB planı: WinUSB üstünde user-mode 802.11 sürücüsü

Tarih: 2026-10-28. Kaynaklar 2026-10-28'de okundu (Linux master mt76, linux-firmware, libwdi master, OpenIPC/devourer master, t4t5/owly HEAD, Microsoft Learn, Microsoft Store Policies v7.20).
İşaretler: **[doğrulandı]** = kaynak kodda ya da resmî metinde okundu. **UNVERIFIED** = çıkarım, ikincil kaynak ya da test gerektiriyor.

Bu plan `docs/driver-research.md`'deki KMDF/AX203 planının yerine geçer. O plan, Secure Boot açıkken Microsoft imzası (EV + attestation) gerektirdiği için **ertelendi**.

---

## 0. Özet ve kararlar

| Konu | Karar |
|---|---|
| Kernel sürücüsü | **Yok.** Sadece Microsoft'un inbox `winusb.sys`'i yüklenir. Secure Boot / HVCI kuralları bizim koda hiç dokunmaz |
| Yonga | **Birincil: MediaTek MT7612U** (`0e8d:7612`). Önerilen ürünler: **Alfa AWUS036ACM** ve **Netgear A6210**. **Yedek: Realtek RTL8821AU/8812AU** (TP-Link Archer T2U Plus, Alfa AWUS036ACH v.2) |
| WinUSB bağlama | **Tercih: sertifikasız yol.** Inbox `winusb.inf` içindeki "WinUsb Device" sürücüsü, SetupDi ile **tek bir cihaz örneğine** seçilir. **Yedek: libwdi tarzı** kendinden imzalı katalog. Bu yol x64'te çalışır, ARM64'te ve S modunda çalışmaz |
| Store | Store'daki uygulama **MSIX** olur: imza ücretsiz, yönetici yetkisi yok, sürücü yok. Yükseltilmiş bağlama adımı GitHub'da ayrı, isteğe bağlı bir **"NitroDrop USB Kurulumu"** yardımcısıdır. Store kurulum programı sertifika ya da INF kurmaz |
| Dil | User-mode radyo sürücüsü **Rust** ile yazılır: `nitro-radio` crate'i, WinUSB'ye `windows` crate ile doğrudan erişir. Owly (Rust) ile aynı süreçte çalışır. C# uygulaması, Owly'nin ürettiği IPv6 trafiğini **Wintun** üzerinden görür |
| Kanal | Önce sabit **kanal 6** (iş sahibinin şartı). Owly verisine göre MT7612U ile **149/44 çok daha hızlı**; bu daha sonra bir seçenek olarak eklenir |

---

## 1. Kernel sürücüsü olmadan WinUSB bağlama

### 1.1 Neden Secure Boot ve HVCI açıkken çalışır

- Kernel-mode code signing politikası, Windows 10 1607'den beri *"Windows will not load any new kernel-mode drivers which are not signed by the Dev Portal"* der. Bu kural, yüklenen **kernel ikilisi** için geçerlidir. Bizim durumda yüklenen tek ikili `winusb.sys`. Microsoft imzalı, inbox ve HVCI uyumlu. **[doğrulandı]** https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-
- Bizim paketimiz (INF + `.cat`) sadece **PnP sürücü paketi imza** denetiminden geçer. Bu denetim user-mode'da, Driver Store'a eklenirken yapılır. Sayfadaki metin: *"In addition to driver code signing, you also need to meet the PnP device installation signing requirements"*. https://learn.microsoft.com/en-us/windows-hardware/drivers/install/pnp-device-installation-signing-requirements--windows-vista-and-later-
- Katalog imzası ise sadece "kernel ikilisinin gömülü imzası yoksa" kernel kurallarına uymak zorunda. `winusb.sys` Microsoft kataloğuyla doğrulanır. `Include=winusb.inf` için bu ayrım Microsoft metninde kelimesi kelimesine geçmiyor; bizim çıkarımımız. **UNVERIFIED (resmî ifade yok, ama libwdi pratiği tutarlı)**
- 2026 Mart/Nisan'da cross-signed sürücülere güven kaldırıldı. Bu sadece üçüncü taraf kernel ikililerini etkiliyor, bizi etkilemez. https://www.osnews.com/story/144691/microsoft-removes-trust-for-drivers-signed-with-the-cross-signed-driver-program/
- **Bu makinede kanıt:** `tools/usb-list.ps1` çıktısında ACER kameranın `MI_02` ("APP Mode") arayüzü zaten inbox `winusb.inf` ile `WINUSB` servisine bağlı. Secure Boot ACIK (bkz. §7). Bu bağlantı MS OS descriptor ile otomatik kurulmuş. Bizim dongle'larda bu descriptor yok, ama kanıt şunu gösteriyor: user-mode bir uygulamanın WinUSB ile konuşması bu makinede normal bir yol.

### 1.2 Yol A (önerilen): inbox "WinUsb Device", sertifikasız ve cihaz örneğine özel

- Microsoft'un belgelediği elle yol: Aygıt Yöneticisi → Sürücüyü güncelleştir → Bilgisayarımda ara → *Listeden seçeyim* → **Evrensel Seri Veri Yolu aygıtları** → **WinUsb Device**. Ardından cihazın `Device Parameters` anahtarına `DeviceInterfaceGUIDs` değeri eklenir ve cihaz yeniden takılır. https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-installation **[doğrulandı]**
- Aynı sayfada şu not var: *fiziksel port değişirse adımlar tekrarlanmalı*. Yani bağlama **cihaz örneği başına** yapılıyor. Bizim için bu avantaj: aynı VID/PID'li başka bir dongle (örneğin internet için kullanılan) etkilenmez.
- Sertifika yok, katalog yok, bizim dosyamız yok. pbatard (libwdi yazarı) bunun ARM64'te bile çalıştığını doğruladı (Aralık 2025). Kendinden imzalı yol ARM64'te çalışmıyor. https://github.com/pbatard/libwdi/issues/289
- **Programatik karşılığı (yardımcı EXE içinde, yönetici yetkisiyle):**
  1. `SetupDiGetClassDevs` ile hedef devnode bulunur (tam instance ID).
  2. `SetupDiBuildDriverInfoList(SPDIT_CLASSDRIVER)` (USBDevice sınıfı) çağrılır, `winusb.inf` / "WinUsb Device" düğümü seçilir.
  3. `SetupDiSetSelectedDriver` + `DiInstallDevice` ile o tek devnode'a kurulur.
  4. `SetupDiOpenDevRegKey(DIREG_DEV)` ile `DeviceInterfaceGUIDs = {NitroDrop GUID}` yazılır.
  5. `CM_Reenumerate_DevNode` ya da disable/enable ile cihaz yeniden başlatılır.
  
  **UNVERIFIED: kod olarak denenmedi, ilk prototip işi (M0).** Alternatif: `DeviceInterfaceGUIDs` yerine inbox WinUSB'nin varsayılan arayüz GUID'i (`{dee824ef-729b-4a0e-9c14-b7117d33a817}`, `GUID_DEVINTERFACE_WINUSB`?) kullanılabilir mi? **UNVERIFIED**.
- **Risk:** Windows Update, hardware ID'si birebir eşleşen bir MediaTek sürücüsü bulursa elle seçilen bu sürücüyü "daha iyi eşleşme" diyerek değiştirebilir. https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-windows-selects-a-driver-for-a-device **UNVERIFIED: MT7612U için WU sürücüsü var mı, bilinmiyor.** Önlemler:
  - Uygulama açılışta bağlamayı denetler, kaybolduysa kullanıcıya "yeniden bağla" önerir.
  - İsteğe bağlı olarak Grup İlkesi / `ExcludeWUDriversInQualityUpdate` gibi ayarlar belgelenir. Bunlar otomatik uygulanmaz.

### 1.3 Yol B (yedek): libwdi/Zadig tarzı kendinden imzalı paket

libwdi kaynağına göre akış (`libwdi/pki.c`, `libwdi.c`, `installer.c`) **[doğrulandı]**:

1. **`CreateSelfSignedCert`**
   - Makine anahtar kabı açılır: `"libwdi key container"`, `CRYPT_MACHINE_KEYSET`.
   - Her çalıştırmada yeni bir **RSA-4096** anahtar üretilir: `CryptGenKey(AT_SIGNATURE, CRYPT_EXPORTABLE)`.
   - Bununla SHA-256 self-signed bir sertifika oluşturulur. EKU sadece Code Signing'dir ve critical işaretlidir; geçerlilik 2029-01-01'e kadardır.
   - Subject `CN=USB\VID_xxxx&PID_xxxx (libwdi autogenerated)` biçimindedir.
2. **`AddCertToStore`**: sertifika LocalMachine **Root** ve **TrustedPublisher** depolarına eklenir.
3. **`CreateCat`**: katalog `CryptCATOpen` / `CryptCATAdminCalcHashFromFileHandle` / `CryptCATPersistStore` ile oluşturulur.
4. **`SelfSignFile`**: katalog `SignerSignEx` (SHA-256) ile imzalanır.
5. **`DeletePrivateKey`**: her durumda `CRYPT_DELETEKEYSET` ile **özel anahtar silinir**. Kaynaktaki yorum: *"we \*MUST\* ensure that the private key is destroyed, so that it cannot be reused"*.
6. INF şablonu (`winusb.inf.in`): `Include=winusb.inf`, `Needs=WINUSB.NT`, `AddService=WinUSB`, `KmdfService=WINUSB`, `DeviceInterfaceGUIDs`, `CatalogFile`. Şablonda eski co-installer bölümleri duruyor; biz Microsoft'un güncel co-installer'sız örneğini kullanmalıyız: https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-installation
7. Kurulum ayrı, yükseltilmiş bir `installer_x64.exe` ile yapılır. Önce `UpdateDriverForPlugAndPlayDevicesU(NULL, hwid, inf, INSTALLFLAG_FORCE)` denenir; olmazsa `SetupCopyOEMInf` ile paket hazırlanır, ardından `CM_Reenumerate_DevNode` çağrılır.

- **Cihaz örneğine özel mi:** Hayır. `UpdateDriverForPlugAndPlayDevices`, o hardware ID'ye sahip **tüm** takılı cihazları günceller; libwdi'de örnek-bazlı bir seçenek yok. Örnek-bazlı bağlama için Yol A'daki SetupDi seçimi, bu INF ile birlikte kullanılabilir.
- **Windows 11 durumu:**
  - pbatard, Aralık 2023: *"This works fine on Windows 11 x64. This doesn't work on Windows 11 ARM64."* https://github.com/pbatard/libwdi/issues/289
  - 2021 Insider build'leri `0x800b0109` ile reddetmişti; final sürüm Windows 10 gibi davrandı. https://community.osr.com/t/windows-11-and-alternative-driver-installation-method-in-libwdi/57493 , https://github.com/pbatard/libwdi/issues/242
  - Microsoft'un PnP sayfasına göre S modu ve x86/amd64 dışı mimariler **WHQL imzalı katalog** ister.
- **HVCI:** OSR başlığında Windows 10'da HVCI açıkken kurulumun uyarıyla çalıştığı bildirilmiş. Windows 11 + Memory Integrity için açık bir rapor bulunamadı. **UNVERIFIED.** Mantıken etkilememeli, çünkü HVCI kernel kod bütünlüğüyle ilgili, katalog güveni user-mode'da kalıyor. **M0'da Memory Integrity açık bir makinede test edilmeli.**
- **Sıralama:** AllSignersEqual varsayılan olarak açık, yani Authenticode imzalı paket ile WHQL aynı imza puanını alır; eşitlik tarih ve sürümle bozulur. https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-the-driver-selection-process , https://learn.microsoft.com/en-us/windows-hardware/drivers/install/signature-score--windows-vista-and-later- . Kesin sayısal aralıklar **UNVERIFIED**.

### 1.4 Kök sertifikanın güvenlik etkisi (Yol B)

- Özel anahtar imzadan hemen sonra siliniyor **[doğrulandı]**. Ama anahtar `CRYPT_EXPORTABLE` olarak, kısa bir süre makine anahtar deposunda duruyor. O pencerede yönetici yetkisindeki bir kod anahtarı alabilir. Yönetici zaten makinenin sahibi olduğu için pratik risk düşük.
- **Kalıcı etki:** Root deposunda, 2029'a kadar geçerli, code-signing EKU'lu bir kök kalır. Anahtar yok olduğu için teorik bir risk. Ama birikiyorlar, ve Defender "cihazın güvenlik duruşunu etkileyen değişiklik" ölçütüne takılabilir. https://learn.microsoft.com/en-us/defender-xdr/criteria
- **Öneri:** kurulumdan hemen sonra sertifikayı Root ve TrustedPublisher'dan **silmek**. Driver Store doğrulamayı paket eklenirken yapıyor; `winusb.sys`'in yüklenmesi bizim imzamıza bağlı değil. **UNVERIFIED:** paket Store'a eklendikten sonra yeni bir port/devnode kurulurken katalog yeniden denetleniyor mu? M0 testi: sertifikayı sil → dongle'ı başka porta tak → bağlandı mı?
- Yol A'da bu sorunların hiçbiri yok. Yol A'nın tercih edilme sebebi bu.

### 1.5 Alternatifler

| Seçenek | Durum |
|---|---|
| MS OS 1.0/2.0 descriptor (`WINUSB` compatible ID) | Cihaz firmware'inde descriptor gerekir: *"the device firmware must have Microsoft OS descriptors"*. Perakende dongle'larda yok. **Uygulanamaz.** https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/automatic-installation-of-winusb |
| UsbDk (daynix, Apache-2.0) | Hub sınıfına takılan **üçüncü taraf kernel filter sürücüsü**. Microsoft/attestation imzası **UNVERIFIED**; Store 10.2.4'e takılır. https://github.com/daynix/UsbDk |
| libusbK / libusb-win32 | Üçüncü taraf kernel sürücüleri. İmza durumu **UNVERIFIED**; WinUSB'ye göre bir avantajları yok |
| libusb-1.0 | Windows'ta WinUSB backend'ini kullanır. Kullanılabilir, ama Rust'ta `WinUsb_*`'ı doğrudan çağırmak bağımlılığı azaltır. https://github.com/libusb/libusb/wiki/Windows |

### 1.6 Microsoft Store politikası

Microsoft Store Policies v7.20 (2026-09-14): https://learn.microsoft.com/en-us/windows/apps/publish/store-policies **[doğrulandı]**

- **10.2:** *"Your product must not jeopardize or compromise user security, or the security or functionality of the device… You will not disable any platform safety or comfort features."*
- **10.2.4:** *"Generally, dependency on non-Microsoft provided drivers or NT services is not allowed but may be considered case by case for WHCP certified drivers… you must disclose that dependency… in the certification notes."* WinUSB Microsoft'un sürücüsü olduğu için bizi doğrudan engellemiyor; yine de bağımlılık açıklamada ve sertifikasyon notlarında belirtilmeli.
- **10.1.5:** uygulama içi eklenti edinimi serbest, *"excluding non-Microsoft drivers or NT services"*.
- **10.2.7** temiz kaldırma ister; **10.2.8** Windows ayarlarını değiştirmek için kullanıcı onayı ve "desteklenen yöntemler" ister.
- **10.2.9 (MSI/EXE):** *"The binary and all of its Portable Executable (PE) files must be digitally signed with a code signing certificate that chains up to… the Microsoft Trusted Root Program"*. Ayrıca sessiz kurulum, web stub olmayan tek parça kurulum programı ve sürümlü indirme URL'si şart. Kod imzalama dokümanına göre MSI/EXE için *"self-signed certificates are not accepted"*; MSIX ise Store tarafından **ücretsiz** yeniden imzalanıyor. Ücretsiz seçenek olarak açık kaynak için **SignPath Foundation** gösteriliyor. https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
- **MSIX:** *"MSIX doesn't support Windows drivers"*, HKLM yazımları sanallaştırılıyor; yükseltme gerektiren uygulamalar kabul edilmiyor (2023 tarihli sayfa, güncel `allowElevation` tutumu **UNVERIFIED**). https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-prepare

**Sonuç:** Store'daki Win32 kurulum programının sessizce kök sertifika ve INF kurması iki yönden kötü:
1. "Ücretli sertifika yok" kuralıyla çatışıyor (10.2.9; SignPath bir çıkış olabilir).
2. 10.2 / 10.2.8 kapsamında güvenlik duruşu değişikliği sayılır ve büyük olasılıkla sertifikasyonda reddedilir.

**Dağıtım:**
- **Store:** NitroDrop MSIX olarak dağıtılır. Normal kullanımda yönetici yetkisi gerekmez; WinUSB'ye user-mode'dan erişilir. Açıklamada "desteklenen bir USB Wi-Fi adaptörü ve tek seferlik bağlama gerekir" yazar.
- **GitHub:** ayrı, açık kaynak **NitroDrop USB Kurulumu** yardımcısı (mümkünse SignPath ile imzalı). Yol A ile tek cihaz örneğini bağlar; "Geri al" düğmesi vardır.
- Store'daki uygulama yardımcıyı **indirip çalıştırmaz**, sadece GitHub sayfasına link verir (10.1.5 ve 10.2.4'e güvenli mesafe). Bu yorum **UNVERIFIED**; sertifikasyon notunda açıkça sorulmalı.

### 1.7 Mevcut Wi-Fi sürücüsü ve geri alma

- MT7612U dongle'ının Windows Update'ten sürücü alıp almadığı **UNVERIFIED**. Alfa ayrı bir paket dağıtıyor: https://store.rokland.com/pages/alfa-awus036acm-driver-support-page . Realtek dongle'lar çoğunlukla WU'dan sürücü alır (**UNVERIFIED**).
- Yol A yardımcısı bağlamadan önce mevcut sürücüyü kaydeder (instance ID, eski INF/provider/servis). Bunu `tools/usb-list.ps1`'in okuduğu alanlarla yapar.
- **Geri alma:**
  - **Yol A:** silinecek bir OEM INF'imiz yok. `pnputil /remove-device "<instance>"` ve ardından `pnputil /scan-devices` en iyi eşleşmeyi yeniden seçer (vendor sürücüsü varsa o gelir). Son olarak `DeviceInterfaceGUIDs` değeri silinir.
  - **Yol B:** `pnputil /enum-drivers` ile bizim `oemNN.inf` bulunur → `pnputil /delete-driver oemNN.inf /uninstall` → `/remove-device` → `/scan-devices` → sertifikalar silinir. `/delete-driver` Windows 10 1607+, `/remove-device` ve `/scan-devices` 2004+ gerektirir. https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- Dahili Intel AX203 hiç değişmez; internet onun üzerinden devam eder. README'deki "Intel kart AWDL'e ayrılır" kararı bu planla tersine dönüyor: **dongle AWDL'e ayrılır**.

---

## 2. Yonga seçimi

### 2.1 Owly'nin destek tablosu
Kaynaklar: https://github.com/t4t5/owly/blob/HEAD/crates/owly-radio/drivers.toml ve https://github.com/t4t5/owly#supported-hardware **[doğrulandı]**

- **`[drivers.mt76x2u]`**:
  - `status="tested"`, `mode="active-monitor"`, `channels=[149, 44, 6]`, `rx_ampdu=true`, `supports_tx_ldpc=true`.
  - Notlar: *"MediaTek MT7612U USB (0e8d:7612)… Both ways with an iPhone… on 149 a 3 MB photo… about 5 s, against 27 s for a 1.1 MB one on 6. Its active monitor answers A-MPDUs with a Block Ack itself… Channel changes can cancel the driver's TX-status cleanup, stalling injection that waits for ACKs."*
  - **Owly'de "tested" işaretli tek USB yonga bu.**
- **Diğer USB girdileri:**
  - `rtw89_8852bu`: untested, ad-hoc yok, gizli AP yanında çalışıyor.
  - `rtl8xxxu` (RTL8188FTV): untested, sadece 2.4 GHz, fotoğraf ~75 s.
- **Tabloda yok:** `mt7921u`, `mt76x0u`, `rtl88xxau`, `rtw88_8812au`. PCIe `mt7921e` ile ilgili not: *"sends injected unicast at 1 Mbit/s whatever rate is asked for, and not at all on 5 GHz"*. Bu, CONNAC2 (MT7921) ailesine karşı güçlü bir kanıt.
- **"Active monitor":** monitor arayüzü istasyonun MAC'ini taşır ve donanım o adrese gelen çerçeveleri ACK'ler. İstediğimiz model bu: hem tüm çerçeveleri al hem kendi MAC'ini ACK'le.

### 2.2 Karşılaştırma

| | **MT7612U** (mt76x2u) | MT7610U (mt76x0u) | MT7921AU (mt7921u) | RTL8812AU / 8821AU |
|---|---|---|---|---|
| Port karmaşıklığı | **Düşük–orta.** RF/synth/kalibrasyon MCU'da; MAC register seviyesinde. ≈2.2k satır C mantığı (§3). devourer bunu user-mode'da yaptı | Orta. Benzer MAC, 1T1R, RF kodu host'ta | **Yüksek.** CONNAC2 tam offload firmware, MCU komutları, ham TX kontrolü zayıf | **Yüksek.** PHY/RF tabloları host'ta (Jaguar1), halmac tarzı init |
| Firmware | `mt7662.bin` (81,908 B) + `mt7662_rom_patch.bin` (26,350 B). Güncel kod bunları yüklüyor; `mt7662u*.bin` hâlâ var ama kullanılmıyor **[doğrulandı]** | `mt7610u.bin` | `WIFI_RAM_CODE_MT7961_1.bin` + `WIFI_MT7961_patch_mcu_1_2_hdr.bin` | `rtw88/rtw8812a_fw.bin` / `rtw8821a_fw.bin` |
| Firmware lisansı | LICENCE.mediatek: MediaTek yongalı cihazlarla kullanım ve dağıtım serbest, *"but not as part of the Linux kernel or in any other form which would require these files themselves to be covered by the terms of the GNU GPL"*. **MIT uygulamanın yanında dağıtılabilir** | aynı | aynı | LICENCE.rtlwifi_firmware: değiştirmeden dağıtım serbest, tersine mühendislik yasak |
| Kaynak lisansı | **BSD-3-Clause-Clear** (mt76x2/mt76x02 dosyalarındaki SPDX satırı). **Not: ISC değil.** Eski mt76 dosyaları ISC'ydi, bugünkü başlıklar BSD-3-Clause-Clear. İkisi de izin verici ve MIT ile uyumlu; Clear sürümü patent lisansı vermediğini açıkça söylüyor | mt76x0 `init.c/phy.c/main.c/eeprom.c` **GPL-2.0-only** (rapora göre; **UNVERIFIED**, tekrar bakılmalı) | BSD-3-Clause-Clear | **rtw88: GPL-2.0 OR BSD-3-Clause** (8812AU/8821AU desteği Linux 6.13/6.14'te geldi). aircrack-ng/morrownr/devourer Realtek kodu GPL-2.0 |
| Monitor + inject | İyi; Owly'de test edilmiş, injected A-MPDU çalışıyor (devourer) | Linux'ta iyi, Owly'de yok | Zayıf (Owly mt7921e notu) | Mükemmel (FPV standardı) |
| Kendi MAC'ine HW ACK + promisc | **Evet.** `MT_AUTO_RSP_CFG` EN + `MAC_ADDR_DW0/1`. Owly active monitor kullanıyor; devourer havada ölçtü: responder açıkken 3500+ ACK, kapalıyken 0 **[doğrulandı: devourer docs/mt7612u.md]** | Muhtemel, **UNVERIFIED** | Firmware'e bağlı, **UNVERIFIED** | Evet; devourer'da Realtek ACK/BA responder var |
| TR'de fiyat (**UNVERIFIED**, arama snippet'leri) | AWUS036ACM ≈ 14.000 TL (Trendyol, resmî ithalatçı; ABD'de ≈ 47 $); Netgear A6210 Amazon.com.tr'de listeli | AWUS036ACHM Amazon.com.tr'de listeli | AWUS036AXML ≈ 15.000 TL | Archer T2U Plus ≈ 455–760 TL; AWUS036ACH v.2 ≈ 2.000 TL |

### 2.3 devourer (OpenIPC) — en önemli bulgu
https://github.com/OpenIPC/devourer **[doğrulandı]**

- *"The Realtek userspace Wi-Fi driver"*. C/C++20 ile yazılmış, libusb üzerinden Linux/macOS/**Windows**/Android'de çalışıyor (*"devourer builds Windows first-class"*). Aktif bir proje: son push 2026-10-27.
- Repo lisansı **GPL-2.0**, ama **MT7612U alt ağacının çekirdek dosyaları farklı**: `src/mt7612u/` altındaki `usb.cpp, async.cpp, mcu.cpp, fw.cpp, eeprom.cpp, init.cpp, phy.cpp, tx.cpp, rx.cpp, radiotap.cpp, caps.cpp, beacon.cpp, internal.h, regs.h, initvals.h, mt7612u.h` hepsi **`SPDX-License-Identifier: BSD-3-Clause-Clear`** ve *"Ported from mt76"*. `station.cpp`, `Mt7612uRadio.*` ve `Mt7612uUsbIds.h` SPDX başlığı taşımıyor; bunlar GPL-2.0 sayılır ve **kopyalanmaz**.
- **Pratik sonuç:** Windows'ta çalışan, BSD lisanslı, ≈5k satırlık bir user-mode MT7612U referansı hazırda var. Bizim port "mt76'yı sıfırdan okumak" yerine "devourer'ın BSD çekirdeğini Rust'a çevirmek + mt76 ile çapraz kontrol" olur. Risk çok düşer.
- **devourer'ın belgelediği MT7612U tuzakları** (`docs/mt7612u.md`, `docs/mt7612u-usb-wedge.md`):
  1. **ACK gelmeyen unicast'te ≈20 ms/çerçeve:** *"The MAC arms an ACK timeout for a peer that never answers"*. ACK-req bitini temizlemek bunu çözmüyor. AWDL'de peer ACK verdiği için daha az sorun olmalı; **M3'te ölçülmeli**.
  2. Alıcı **1 Hz `MCU_CAL_TEMP_SENSOR` tik'i** istiyor, yoksa RX çöküyor.
  3. **Bulk-IN boşaltılmadan RX açılırsa yonga kilitleniyor** ve fiziksel olarak yeniden takılması gerekiyor. RX okuma havuzu, `MAC_SYS_CTRL` RX'ten **önce** kurulmalı.
  4. FCS teslim edilmiyor; son 4 bayt FCE bilgisi.
  5. Hızlı kanal değişimi yok (MCU `CMD_SWITCH_CHANNEL_OP` + kalibrasyon). Sabit kanalda sorun değil.

### 2.4 Öneri

**Birincil: MT7612U.** Satın alınabilecek ürünler:

1. **Alfa AWUS036ACM** (`0e8d:7612`). devourer'ın donanımda test ettiği birim. TR'de ≈14k TL (Trendyol, resmî ithalatçı); AliExpress/eBay/ikinci elden çok daha ucuz olabilir.
   https://www.trendyol.com/alfa-network/alfa-awus036acm-kablosuz-usb-adaptor-p-1106227746
2. **Netgear A6210** (`0846:9053`). https://www.amazon.com.tr/Netgear-100PES-AC1200-High-3-0-adapt%C3%B6r%C3%BC-A6210-PES/dp/B00OT586RQ (fiyat **UNVERIFIED**). İkinci el piyasada yaygın.
3. **ASUS USB-AC55 / USB-AC54** (`0b05:17eb` / `0b05:1833`). İkinci elde bulunursa.
4. Jenerik "MT7612U / MT7612UN" ilanları (ör. Hepsiburada "Sxs Pentest Twin 1300") ancak **gelince VID:PID doğrulanırsa** kullanılır. Comfast CF-WU782AC'nin revizyonları karışık (**UNVERIFIED**); VID:PID görülmeden alınmamalı.

**Yedek: RTL8821AU / RTL8812AU.**
- **TP-Link Archer T2U Plus** (RTL8821AU, `2357:0120`, ≈500–750 TL, her yerde bulunuyor): https://www.trendyol.com/tp-link/archer-t2u-plus-ac600-mbps-antenli-wireless-usb-adaptor-p-7029153
- **Alfa AWUS036ACH v.2** (RTL8812AU, `0bda:8812`, ≈2.000 TL).
- Port kaynağı: **rtw88** (GPL-2.0 OR BSD-3-Clause seçeneği). devourer'ın Realtek kodu GPL olduğu için sadece davranış referansı olarak kullanılır, ya da ayrı bir GPL süreci olarak çalıştırılır.
- Owly'de test edilmemiş. İş yükü MT7612U'nun belki 2–3 katı.
- Archer T4U v2/v3 ve T2U v3'ten kaçının (yonga revizyonları belirsiz).

**Önerilmeyenler:**
- MT7921AU (AXML): CONNAC2 tam offload ve zayıf injection.
- MT7610U: Owly'de test edilmemiş, 1T1R, mt76x0 kodunun bir kısmı GPL-only.
- Tenda U-serisi (AIC8800): Linux'ta bile monitor/inject desteği zayıf ve vendor kodu. İnternet için kullanılmaya devam eder.

---

## 3. MT7612U: Linux sürücüsünden user-mode'a harita

Kaynak: `drivers/net/wireless/mediatek/mt76/` (Linux master, 2026-10-28). Satır sayıları `wc -l`. "Port" sütunu, sabit 2.4 GHz kanal 6 HT20 için gereken C-eşdeğeri mantıktır: AP, beacon, kripto, rate control, güç tasarrufu ve DFS yok.

### 3.1 Dosya/fonksiyon haritası

| Dosya | Toplam | Port ≈ | Gerekenler |
|---|---|---|---|
| `usb.c` | 1229 | 150 | `__mt76u_vendor_request` (10× deneme, 300 ms), `__mt76u_rr/wr`, `mt76u_rmw`, `mt76u_copy`, `mt76u_single_wr`, `mt76u_set_endpoints`, `mt76u_get_rx_entry_len` |
| `mt76x02_usb_core.c` | 286 | 50 | `mt76x02u_skb_dma_info` (TXINFO), `mt76x02u_tx_prepare_skb`, `mt76x02u_mac_start` |
| `mt76x02_usb_mcu.c` | 297 | 110 | `__mt76x02u_mcu_send_msg`, `mt76x02u_mcu_wait_resp`, `mt76x02u_mcu_wr_rp/rd_rp`, `mt76x02u_mcu_fw_reset`, `mt76x02u_mcu_fw_send_data` |
| `mt76x2/usb.c` | 154 | 25 | ID tablosu, probe, ASIC rev kontrolü |
| `mt76x2/usb_init.c` | 261 | 130 | `mt76x2u_init_dma`, `power_on`, `power_on_rf(_patch)`, `init_eeprom`, `init_hardware` |
| `mt76x2/usb_mcu.c` | 255 | 150 | `load_ivb`, `enable_patch`, `reset_wmt`, `load_rom_patch`, `load_firmware`, `mcu_init` |
| `mt76x2/usb_mac.c` | 174 | 120 | `mt76x2u_mac_reset`, `mac_fixup_xtal`, `mac_stop` |
| `mt76x2/usb_phy.c` | 201 | 90 | `mt76x2u_phy_set_channel` (HT20), `phy_channel_calibrate` |
| `mt76x2/usb_main.c` | 118 | 25 | `start`, `set_channel`, `config` (monitor dalı) |
| `mt76x2/init.c` | 204 | 110 | `mt76x2_reset_wlan`, `mt76_write_mac_initvals` |
| `mt76x2/mac.c/.h`, `mcu.c` | 178 | 95 | `mac_stop/resume`, `mcu_set_channel`, `load_cr`, `init_gain` |
| `mt76x02_mcu.c` | 171 | 40 | `function_select`, `set_radio_state`, `calibrate` |
| `mt76x02_mac.c` | 1255 | 170 | `setaddr`, `set_bssid`, `write_txwi`, `process_rx`, `load_tx_status`, `edcca_init` / **`edcca_tx_enable`** (AUTO_RSP_EN'i ve PA/LNA pinlerini açar), `check_mac_err` |
| `mt76x02_util.c`, `mt76x02_txrx.c` | 882 | 70 | filtre semantiği, rate tablosu, `set_tx_ackto` |
| `mt76x02_eeprom.c/.h`, `mt76x2/eeprom.c/.h` | 927 | 270 | hw cap, rx gain, 2G güç bilgisi, rate power (USB'de EEPROM vendor request ile okunur, efuse yolu yok) |
| `mt76x02_phy.c`, `mt76x2/phy.c` | 553 | 250 | rxpath, txdac, bw, band, txpower regs, tx delay, gain adj |
| `mt76x02_beacon.c` | 215 | 10 | `init_beacon_config` |
| `mt76x02_regs.h` + mac/dma/mcu başlıkları | ~1180 | 330 | sabitler, descriptor struct'ları |
| `mac80211.c`, `tx.c`, `mt76.h`, `mt76x02_dfs.c`, debugfs, mmio | ~7000 | 0 | gereksiz |

**Toplam ≈ 2.2k satır C mantığı** (≈600'ü sabit ve tablo). Rust + WinUSB tesisatıyla (açma, pipe policy, overlapped RX havuzu, TX yazıcı, çerçeve API'si) **≈2.5–3k satır**. TSSI/sıcaklık telafisi başta atlanırsa radyo kısmı ≈1.8k'ya iner. Karşılaştırma: AX203 KMDF planı ≈16–19k satırdı.

### 3.2 USB taşıma katmanı
- **Register erişimi:** her erişim bir control transfer.
  - Okuma `0xC0`, yazma `0x40`; `wValue=addr>>16`, `wIndex=addr&0xffff`, 4 bayt LE.
  - İstek kodu adres tipine göre değişir:
    - MAC/BBP: `0x07` MULTI_READ / `0x06` MULTI_WRITE
    - CFG alanı: `0x47` / `0x46`
    - EEPROM okuma: `0x09`
    - `0x01` DEV_MODE: 1 = MCU reset, `0x12` = IVB yükle
    - `0x42` WRITE_FCE
    - Class istek `0x21`/`0x01`: `enable_patch` ve `reset_wmt`
- **MCU çalışınca:** initvals tablosu MCU `CMD_RANDOM_WRITE` ile komut başına 24 çift halinde gider.
- **Endpoint'ler:** descriptor sırasıyla atanır.
  - IN: `[0]` = paket RX, `[1]` = MCU yanıtı.
  - OUT: `[0]` = komut/firmware, `[1..4]` = BE/BK/VI/VO, `[5]` = mgmt.
  - Gerçek endpoint numaraları çalışma anında okunmalı (**UNVERIFIED** 0x84/0x85, 0x08, 0x04–0x07, 0x09).
- **MCU mesajı:** `out_ep[0]`'a `[TXINFO: LEN | SEQ<<16 | CMD_TYPE<<20 | PORT=CPU_TX(2)<<27 | TYPE_CMD b30][payload][pad4][4×0]` gönderilir. Yanıt `in_ep[1]`'den 1024 bayt okunur; ilk u32 `RX_FCE_INFO` ve seq'in eşleşmesi gerekir (`EVT_CMD_DONE`).
- **RX transferi** (`RX_BULK_AGG_EN` kapalı, transfer başına bir çerçeve): `[u32 dma_len][RXWI 32 B][802.11 çerçeve (+L2PAD 2 B)][pad][u32 FCE info]`.
- **TX (data, `out_ep[1..4]`):** `[TXINFO: LEN | 80211 b19 | WIV b24 | QSEL=EDCA | DPORT=WLAN][TXWI 20 B][802.11 header (+2 B pad if hdrlen%4)][body][pad4][4×0]`.

### 3.3 Descriptor'lar
- **TXWI (20 B):**
  - `flags`: TS b3, AMPDU b4, …
  - `rate`: MCS 5:0, LDPC b6, BW 8:7, SGI b9, STBC b10, PHY 15:13. PHY değerleri: CCK=0, OFDM=1, HT=2, VHT=4.
  - **`ack_ctl`**: **REQ b0** (ACK bekle + retry), **NSEQ b1** (HW sıra no).
  - `wcid`: injection için 0xff.
  - `len_ctl`: pad hariç uzunluk.
  - `txstream`: rev ≥ E4 için 0x13.
  - `pktid`: TX status'ta geri gelir.
  - **Çerçeve başına rate ve no-ACK descriptor'da tamamen bizim kontrolümüzde.** Retry limiti global (`MT_TX_RETRY_CFG` 0x134c).
- **RXWI (32 B):**
  - `rxinfo`: UNICAST, MCAST, BCAST, MYBSS, CRCERR, L2PAD, AMPDU, ACTION b28, …
  - `ctl`: WCID, MPDU_LEN 29:16.
  - `tid_sn`, `rate`.
  - `rssi[4]`: dBm = rssi + offset − lna_gain (EEPROM'dan).
  - **RXWI'da TSF/zaman damgası yok.**
- **TX status:** önce `TX_STAT_FIFO_EXT` 0x1798 (retry, pktid), sonra `TX_STAT_FIFO` 0x1718 (VALID, SUCCESS, ACKREQ, WCID, RATE) okunur. Her okuma bir control transfer.

### 3.4 Sıralı init dizisi (mt76x2u)
1. **Probe:** `MT_ASIC_VERSION` (0x0000) okunur; `0x76320044` = VHT yok.
   - WinUSB'de `usb_reset_device` yok. Önce `0x01/1` ile MCU reset yapılır. **UNVERIFIED:** önceki bir oturumdan kalan çalışan firmware'de bu yeterli mi?
2. **EEPROM:** 128 × `0x09` ile 512 B okunur. MAC 0x004, NIC_CONF 0x34/0x36/0x42, XTAL_TRIM 0x3a/0x9e, BT_RCAL 0x138.
3. **`mt76x2_reset_wlan`:** `MT_WLAN_FUN_CTRL` 0x80: WLAN_EN|WLAN_CLK_EN, RESET_RF pulse.
4. **`mt76x2u_power_on`** (CFG alanı): 0x148 MTCMOS power-up ve ACK poll, 0x1204/0x80/0x64, `power_on_rf(0/1)`, 0x130/0x1c/0x14 patch.
5. **`mt76x02_wait_for_mac`:** MAC_CSR0 0x1000 ≠ 0 / ~0.
6. **ROM patch:**
   - `U3DMA_CFG` 0x9018, ardından fw_reset.
   - FCE kurulumu: 0x800=1, 0x9a0=0x400230, 0x9a4=1, 0x9c4=0x44, 0xa6c=3.
   - Patch gövdesi (30 B başlıktan sonrası) 0x90000'a 2040 B'lık parçalarla gönderilir.
   - `enable_patch`, `reset_wmt`; done bit poll'u (0x708 b0 ya da 0x730 b1).
7. **RAM firmware:**
   - 32 B başlık okunur (ilm_len, dlm_len).
   - ILM → 0x80000; DLM → 0x110000 (rev ≥ E3 için 0x110800). Parçalar 14,584 B.
   - `load_ivb` (0x01/0x12), ardından `COM_REG0` b0 poll'u.
   - **Parça başına akış:** WRITE_FCE ile `FCE_DMA_ADDR` 0x230 ve `FCE_DMA_LEN` 0x234 → `out_ep[0]`'a bulk → 0x9a8 artır → 5–10 ms bekle. Toplam ≈0.3–0.5 s.
8. **Hazırlık bekleme:** `WPDMA_GLO_CFG` 0x208 boşta → MAC hazır.
9. **`init_dma`:** `U3DMA_CFG` |= RX_DROP_OR_PAD | RX_BULK_EN | TX_BULK_EN; RX_BULK_AGG_EN kapatılır.
10. **`mcu_init`:** `function_select(Q_SELECT)`, `set_radio_state(RADIO_ON)`.
11. **`mac_reset`:**
    - `PBF_*_MAX_PCNT`, **initvals** (`RX_FILTR_CFG` 0x15f97, `TX_RETRY` 0x47f01f0f, …).
    - **`AUTO_RSP_CFG` 0x1404 = 0x13.**
    - WMM parametreleri, `mac_fixup_xtal`.
12. **`mt76x02_mac_setaddr`:**
    - `MAC_ADDR_DW0/1` 0x1008/0x100c = MAC, U2ME_MASK=0xff.
    - `MAC_BSSID_DW0/1` 0x1010/0x1014.
    - APC_BSSID[0..7] sıfırlanır.
13. **Tablo temizliği:** WCID tablosu (0xa800 / 0x1800) ve anahtar tablosu temizlenir.
14. **`init_beacon_config`:** `BEACON_TIME_CFG` 0x1114 temizlenir.
15. **`mcu_load_cr`, `set_rxpath`, `set_txdac`.**
16. **`mac_stop`.**
17. **Kanal 6 HT20:**
    - rx gain, txpower regs, tx delay, band/bw.
    - **`CMD_SWITCH_CHANNEL_OP`** iki kez gönderilir, ardından `INIT_GAIN`.
    - Kalibrasyonlar: R (bir kez), RXDCOC, RC (bir kez); sonra `phy_channel_calibrate`: TX_LOFT, TXIQ, RXIQC_FI, TEMP_SENSOR, TX_SHAPING.
    - **`edcca_init` → `edcca_tx_enable(true)`:** AUTO_RSP_EN ve PA/LNA pinleri açılır.
    - `mac_resume`.
18. **Bizim MAC:** AWDL MAC'imiz `MAC_ADDR_DW0/1`'e yazılır. **Donanım ACK'i bu adrese verir.** BSSID `00:25:00:ff:94:73`'ü APC_BSSID'e yazmak ACK için gerekmez; isteğe bağlı.
19. **RX filtresi:** `RX_FILTR_CFG = 0x15f93`.
    - Bitler drop maskesi: initvals değerinden PROMISC (b2) çıkarılmış hali.
    - OTHER_BSS (b3) zaten kapalı.
    - Sonuç: tüm adreslerden data ve mgmt alınır; control ve CRC hatalı çerçeveler alınmaz.
20. **`mac_start`:** **Önce RX okuma havuzu kurulur** (devourer tuzağı), sonra `MAC_SYS_CTRL` = TX|RX (0x0c).
21. **Periyodik işler:** 100 ms'de bir `check_mac_err`; **1 Hz `MCU_CAL_TEMP_SENSOR`** (devourer'a göre zorunlu); TSSI/VGA güncellemesi isteğe bağlı.

### 3.5 HW ACK mekanizması
- ACK ve Block Ack, auto-responder tarafından üretilir (`AUTO_RSP_EN`, A1 == `MAC_ADDR` ve U2ME mask). Bu, PROMISC drop bitinden bağımsız çalışır. Kanıtlar:
  - Owly *"active monitor answers A-MPDUs with a Block Ack itself"*.
  - devourer ölçümü: 3500+ ACK'e karşı 0.
- **Uyarı 1:** `edcca_tx_enable(false)` AUTO_RSP'i kapatır. ED/CCA izlemeyi kapalı tutun.
- **Uyarı 2:** Linux RX BA'yı peer başına WCID girdisiyle açar (`WCID_ADDR(idx)+4` bit 16+tid). Peer'i bir WCID slotuna yazmak gerekebilir. **UNVERIFIED.**
- IBSS modu gerekmez. IBSS SYNC_MODE TSF'yi başkalarından benimser; AWDL'de bu istenmez.

### 3.6 AWDL zamanlaması USB üzerinden user-mode'da mümkün mü?
- **Owly zaten bunu yapıyor:** Owly Linux'ta tamamen userspace (nl80211 + radiotap soket). MT7612U de zaten bir **USB** cihazı. Yani "USB gecikmesi + userspace zamanlama" kombinasyonu iPhone ile iki yönlü çalışıyor **[doğrulandı: drivers.toml notu]**. Bizim farkımız iki nokta:
  - (a) Linux kernel'in mt76u katmanı yerine bizim WinUSB katmanımız var. İkisi de URB/IRP tabanlı; kernel'in ek avantajı küçük.
  - (b) Windows zamanlayıcısı.
- **AWDL bütçesi:** AW 16 TU (16.4 ms). Unicast AW kenarlarından 3 TU içeride gönderilir; multicast 16 TU payla slot 0 ve 10'da gider (Owly SPEC). Bu da ≈1 ms seviyesinde jitter toleransı demek.
- **USB:** high-speed (USB 2.0) bulk OUT bir 125 µs microframe'de planlanır. Overlapped `WinUsb_WritePipe` gecikmesi tipik olarak 0.2–1 ms (**UNVERIFIED, M2'de ölçülecek**). `RAW_IO` ve 16–32 bekleyen okuma ile RX gecikmesi benzer.
- **Zamanlayıcı:** `CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)` ≈0.5 ms çözünürlük verir. Kritik iş parçacığı `THREAD_PRIORITY_TIME_CRITICAL` + MMCSS ("Pro Audio") ile çalışır.
- **TSF:** `MT_TSF_TIMER_DW0/1` (0x111c/0x1120) iki control transferle okunur. Bu, host saati ile TSF arasında ≈0.1–1 ms belirsizlikli bir örnek verir (**UNVERIFIED**). RX'te zaman damgası yok, URB tamamlanma zamanı kullanılır.
- `BEACON_TIME_CFG` TIMER_EN gerekebilir; SYNC_MODE 0 = serbest çalışma (**UNVERIFIED**, RT28xx semantiği).
- **Donanımda zamanlı TX yok.** Beacon motorunun TS alanı AWDL action frame'i bozar.
- **Sonuç: uygulanabilir.** Owly'nin `tx_noack=true` modeli seçilir: unicast tek seferde gönderilir, TX status beklenmez, yani status FIFO takılma sorunu da olmaz. Zamanlama host'ta, TSF örneklerinden ve peer'in AWDL PSF/MIF çerçevelerindeki zaman bilgisinden yapılır. Bu Owly'nin zaten yaptığı şey.
- **Ana risk:** devourer'ın "ACK'siz unicast'te 20 ms" bulgusu. AWDL peer ACK verdiği için bu daha hafif olmalı, ama no-ACK bayraklı unicast'te ölçülmeli (M3).
- **Kanal hızı:** kanal 6'da 1.1 MB için 27 s, kanal 149'da 3 MB için ≈5 s. Kanal 6 çalışır ama yavaştır. MT7612U'da 5 GHz kalibrasyon yolu ≈150 satır ek iş (LC cal, 5G rx gain, 5G güç bilgisi). TR regülasyonunda 149 ve 44 kullanılabilir mi, ayrıca kontrol edilmeli (**UNVERIFIED**).

---

## 4. Dil ve mimari

### 4.1 Seçenekler

| | C# (.NET 10, P/Invoke `winusb.dll`) | **Rust kütüphanesi (`windows` crate ile WinUSB)** | C kütüphanesi |
|---|---|---|---|
| Owly entegrasyonu | Owly Rust; C# ↔ Rust FFI her çerçevede sınır geçişi demek, ya da Owly'yi C#'a yeniden yazmak gerekir | **Aynı süreç, aynı dil.** Owly'nin `owly-radio` backend trait'ine yeni bir "winusb-mt7612u" backend'i eklenir | C ABI ile Rust'a bağlanır; ek bir dil |
| Zamanlama | GC duraklamaları (≈ms) AW kenarlarında risk; `GCSettings.LatencyMode` yardımcı olur ama garanti yok | GC yok, tahmin edilebilir | iyi |
| Bellek güvenliği | iyi | iyi | zayıf (USB parse kodu saldırı yüzeyi: havadan gelen çerçeveler) |
| Referans kod | mt76 C, devourer C++ | aynı; devourer C++ → Rust çevirisi düz bir iş | doğrudan |
| Uygulama ile bağ | doğrudan | C ABI (`cdylib`) ya da ayrı süreç | C ABI |

**Öneri: Rust.**
- `nitro-radio` crate'i: WinUSB + MT7612U, ≈2.5–3k satır.
- Owly'nin `owly-awdl` / `owly-radio` crate'leri port edilir (MIT; Linux'a özel `nl80211`/netlink/NetworkManager kısımları çıkarılır).
- Hepsi tek bir **`nitrodrop-awdl.exe`** (ya da `cdylib`) içinde çalışır.
- C# WPF uygulaması (`AirDrop.Core`) AWDL katmanını bir IPv6 arayüzü olarak görür. Mevcut `--interface` soyutlaması (`Net/NetworkBinding.cs`) aynen kullanılır; bu sayede `AirDrop.Core`'a dokunmadan entegrasyon olur.

### 4.2 Sanal ağ arayüzü: Wintun mu, süreç içi IPv6 yığını mı?
- **Wintun** (https://www.wintun.net) **[doğrulandı]**:
  - Kaynak GPL-2.0, ama **önceden derlenmiş, imzalı `wintun.dll`** *"more permissive license"* ile dağıtılıyor ve *"may be distributed with your software"*.
  - DLL, *"Microsoft's driver signing requirements"*'a göre imzalı; yani bizim sertifikamız gerekmez. WireGuard'ın Windows istemcisinin kullandığı sürücü bu.
  - WHQL mi attestation mı olduğu **UNVERIFIED**. Secure Boot açık ve HVCI açık makinelerde yaygın olarak çalışıyor.
  - **Ama:** Wintun bir **kernel sürücüsü kurar** (DLL ilk adaptör oluşturulurken sürücüyü yükler) ve **yönetici yetkisi** ister. Store 10.2.4'e göre *"dependency on non-Microsoft provided drivers… generally not allowed… case by case for WHCP certified"*. MSIX içinde de çalışmaz (yükseltme gerekir).
  - **Sonuç:** Wintun Store sürümünde değil, GitHub'daki USB Kurulumu yardımcısıyla birlikte (aynı yükseltilmiş adımda) kurulabilir.
- **Süreç içi IPv6 yığını** (öneri, uzun vadede):
  - AWDL'nin taşıdığı trafik sadece link-local IPv6 + mDNS (UDP 5353) + AirDrop HTTPS (TCP 8770/8771).
  - `smoltcp` (Rust, 0BSD/MIT benzeri, **UNVERIFIED lisans adı**) ile Rust süreci içinde IPv6/UDP/TCP sonlandırılır. C# tarafına localhost üzerinden bir köprü verilir: mDNS ve HTTPS proxy'si, ya da `AirDrop.Core`'un dinlediği bir loopback soketine akış aktarımı.
  - **Artısı:** sürücü yok, yönetici yetkisi yok, Store uyumlu.
  - **Eksisi:** TCP yığınının kalitesi (pencere, yeniden iletim) ve AirDrop'un peer IPv6 adresini görmesi gerektiği yerler (Discover yanıtı vb.) için ek iş.
- **Plan:**
  - Geliştirme ve M5 için **Wintun**: hızlı, çünkü Owly zaten TUN modeline göre yazılmış.
  - Store sürümü için **süreç içi yığın** (M6).
  - İkisi de aynı `PacketSink` arayüzünün arkasında tutulur.

### 4.3 Süreç yapısı (hedef)
```
NitroDrop.App (WPF, MSIX)  ── AirDrop.Core (mDNS/HTTPS) ── IPv6 ──┐
                                                                    │ Wintun (dev) / loopback köprü (Store)
nitrodrop-awdl.exe (Rust, kullanıcı süreci, yönetici değil)  ◄──────┘
   ├─ owly-awdl  (AWDL durum makinesi, PSF/MIF, kanal dizisi, zamanlama)  [MIT, port]
   ├─ ble        (mevcut tools/ble_test.py mantığı → windows crate BLE)
   └─ nitro-radio (WinUSB, MT7612U: init, RX havuzu, TX, TSF)            [BSD-3-Clause-Clear kökenli]
            │ WinUsb_* (overlapped)
        winusb.sys (Microsoft, inbox)  ── MT7612U dongle
```
- **Firmware:** `mt7662.bin` + `mt7662_rom_patch.bin`, LICENCE.mediatek metniyle uygulama klasöründe dağıtılır. LICENCE.mediatek'e göre GPL'li bir bileşenin içine konmaz.

---

## 5. Fazlı plan

| Faz | İş | Başarı ölçütü | Süre (tek geliştirici, kaba) |
|---|---|---|---|
| **M0: bağlama** | Dongle alınır. `tools/usb-list.ps1` ile VID:PID ve mevcut sürücü kaydedilir. Yol A (SetupDi + inbox WinUsb Device) prototipi ve geri alma yazılır. Test matrisi: Secure Boot açık, **Memory Integrity açık**, standart kullanıcı ile çalıştırma, port değiştirme, WU'nun sürücüyü geri getirip getirmediği. Yol B yedek olarak denenir: sertifika kurulumdan sonra silinince ne oluyor? | Probe `Service=WinUSB` gösteriyor; `WinUsb_Initialize` başarılı; geri alma vendor sürücüsünü geri getiriyor | 1 hafta |
| **M1: firmware ALIVE** | Rust: WinUSB açma, endpoint keşfi, vendor istekleri, EEPROM okuma (MAC görünüyor), power-on, ROM patch, RAM firmware, MCU komutu/yanıtı | `MT_ASIC_VERSION`, EEPROM MAC'i ve firmware sürümü loglanıyor; süreç yeniden başlatılınca dongle yeniden takılmadan init tekrarlanabiliyor | 2–3 hafta |
| **M2: monitor RX** | mac_reset/initvals, kanal 6 HT20 + kalibrasyonlar, RX havuzu, RXWI → radiotap, pcap yazıcı, 1 Hz temp-cal tik'i. Gecikme ölçümü (bulk IN/OUT, TSF okuma) | Wireshark'ta beacon'lar ve iPhone AWDL action frame'leri (BSSID `00:25:00:ff:94:73`) görünüyor; 1 saat kesintisiz RX | 2–3 hafta |
| **M3: inject + HW ACK** | TXWI ile ham TX (rate, no-ACK), `MAC_ADDR`'a bizim MAC'imiz, AUTO_RSP. ACK'ler ikinci bir sniffer ile doğrulanır (Linux live USB'de ikinci bir dongle ya da Intel kart monitor modda). devourer'ın 20 ms/çerçeve sorunu ölçülür | Enjekte edilen AWDL çerçevesi havada görünüyor; iPhone'un bize gelen unicast'i ACK'leniyor (ve BA) | 2–3 hafta |
| **M4: Owly portu** | `owly-awdl` Windows'a: nl80211/netlink yerine `nitro-radio` backend, BLE wake-up (Windows BLE API), Wintun ile `awdl0` eşdeğeri, IPv6 link-local | iPhone NitroDrop'u AirDrop listesinde görüyor (BLE + AWDL + mDNS) | 4–6 hafta |
| **M5: AirDrop uçtan uca** | `AirDrop.Core`'u Wintun arayüzüne `--interface` ile bağlama; /Discover, /Ask, /Upload | iPhone → PC ve PC → iPhone dosya aktarımı | 2–3 hafta |
| **M6: dağıtım** | USB Kurulumu yardımcısı (bağla, geri al, Wintun), SignPath başvurusu, MSIX Store paketi, süreç içi IPv6 yığını (Wintun'suz Store yolu), firmware lisans dosyaları | Store sertifikasyonu geçiyor; GitHub sürümü yayında | 3–4 hafta |
| **M7 (opsiyonel)** | 5 GHz (149/44) kalibrasyon yolu; yedek yonga (rtw88 tabanlı RTL8821AU) | 149'da fotoğraf < 10 s | 2–6 hafta |

**Toplam:** ≈4–6 ay. AX203 KMDF planıyla benzer süre, ama **imza/sertifika engeli yok** ve donanım riski çok daha düşük (devourer + Owly kanıtları).

**Faz 0 önerisi (paralel, 1–2 gün):** dongle'la Linux live USB'de Owly'yi `mt76x2u` active-monitor modunda çalıştırıp iPhone ile gönderip alın. Ardından `usbmon` + Wireshark ile **tam USB trafiği** kaydedin (vendor istekleri, bulk). Bu, user-mode portun birebir "altın referansı" olur.

---

## 6. Riskler

1. **WU'nun bağlamayı geri alması** (Yol A'da elle seçilen sürücünün eşleşme puanı zayıf). Önlem: açılışta denetim + "yeniden bağla". **UNVERIFIED.**
2. **Self-signed yol (B):** ARM64 ve S modunda çalışmıyor; Insider build'lerde daha önce sıkılaştırıldı; Defender ya da kurumsal politikalar kök sertifika eklemeyi engelleyebilir.
3. **Store sertifikasyonu:** GitHub yardımcısına bağlantı ve WinUSB bağımlılığı. 10.2.4 ve 10.1.5 yorumu sertifikasyon notunda açıkça yazılmalı. Reddedilirse uygulama sadece GitHub'da dağıtılır. **UNVERIFIED.**
4. **MT7612U tuzakları:** RX açmadan önce bulk-IN boşaltılmazsa wedge (yeniden takma gerekir); 1 Hz temp-cal; ACK'siz unicast'te 20 ms; firmware zaten çalışırken yeniden başlatma (WinUSB'den `usb_reset_device` yok, port-cycle **UNVERIFIED**).
5. **Zamanlama jitter'ı:** Windows zamanlayıcı ve USB toplamı > ~3 TU olursa unicast'ler AW dışına taşar. Owly'nin `tx_noack` + AW payı modeli bunu hafifletir. Gerekirse TX'i AW başında toplu kuyruklamak denenir.
6. **Kanal 6 yavaşlığı:** 1.1 MB ≈ 27 s. Kullanıcı deneyimi zayıf; M7 (149/44) önemli.
7. **Ürün tedariki:** TR'de MT7612U pahalı (resmî ithalat ≈14k TL) ya da revizyonu belirsiz jenerik ürünler. VID:PID gelince doğrulanmalı; `tools/usb-list.ps1` bunu gösterir.
8. **Wintun:** yönetici yetkisi + kernel sürücüsü, Store'da kullanılamaz. M6'da süreç içi yığın şart.
9. **Lisans:**
   - mt76 / devourer çekirdeği BSD-3-Clause-Clear: telif bildirimleri korunmalı, patent lisansı verilmiyor.
   - devourer'ın SPDX'siz dosyaları (GPL) **kopyalanmaz**.
   - Owly MIT.
   - LICENCE.mediatek: firmware GPL bir bileşenin içine konmaz.
   - OpenDrop GPL-3.0 sadece referans (mevcut app/README notu).
10. **Güç ve USB:** dongle USB 3.0 portta 2.4 GHz'i parazitleyebilir (bilinen USB3 gürültüsü). USB 2.0 port ya da uzatma kablosu önerilir (**UNVERIFIED**, M2'de gözlenmeli).

---

## 7. Araç: `tools/usb-list.ps1`

Salt okunur bir tarama aracı:
- USB aygıtlarını listeler: VID:PID, arayüz (MI_xx), sınıf, **servis**, INF ve sağlayıcı.
- Bilinen yongalarla eşler: MT7612U = Primary, MT7610U = Backup, MT7921AU / RTL88xx = Candidate, AIC8800 = Unsupported.
- Secure Boot (kayıt defterinden) ve Memory Integrity durumunu gösterir.
- Sürücü, sertifika ya da INF kurmaz; kayıt defterine yazmaz; yönetici yetkisi istemez. `-Json` ve `-All` seçenekleri var.

```powershell
powershell -NoProfile -File tools\usb-list.ps1          # özet
powershell -NoProfile -File tools\usb-list.ps1 -All     # tüm USB aygıtları
```

2026-10-28 bu makinedeki (AERONDIGHT) çıktı:
```
Secure Boot: ACIK (kayit defteri)
Memory Integrity (HVCI) calisiyor: HAYIR

VidPid    Interface Name                        Class     Service  Inf        Chip Tier
0408:403A MI_00     ACER HD User Facing         Camera    usbvideo oem55.inf
0408:403A MI_02     APP Mode                    USBDevice WINUSB   winusb.inf
04F3:0C4F           ELAN WBF Fingerprint Sensor Biometric WUDFRd   oem18.inf

Desteklenen bir USB Wi-Fi yongasi bulunamadi. Onerilen: MT7612U (ornegin Alfa AWUS036ACM).
```
- Tenda USB (AIC8800) şu an takılı değil.
- Kameranın `MI_02` arayüzü inbox WinUSB'ye bağlı (§1.1'deki kanıt).
- **Not:** Memory Integrity bu makinede kapalı. M0'daki HVCI testi için açılması gerekecek (Secure Boot zaten açık).

---

## Kaynaklar
- **Owly:**
  - https://github.com/t4t5/owly/blob/HEAD/crates/owly-radio/drivers.toml
  - https://github.com/t4t5/owly
- **devourer:**
  - https://github.com/OpenIPC/devourer
  - https://github.com/OpenIPC/devourer/blob/master/docs/mt7612u.md
  - https://github.com/OpenIPC/devourer/blob/master/docs/mt7612u-usb-wedge.md
  - https://github.com/OpenIPC/devourer/tree/master/src/mt7612u
- **mt76:** https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/drivers/net/wireless/mediatek/mt76 (`usb.c`, `mt76x02_*.c`, `mt76x2/usb*.c`, `mt76x2/init.c`, `mt76x02_regs.h`)
- **linux-firmware:**
  - https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/mediatek
  - WHENCE: https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/WHENCE
  - LICENCE.mediatek: https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/LICENSES/LICENCE.mediatek
- **rtw88 8812AU:**
  - https://cateee.net/lkddb/web-lkddb/RTW88_8812AU.html
  - https://github.com/morrownr/8821au-20210708
- **libwdi:**
  - https://github.com/pbatard/libwdi (`libwdi/pki.c`, `libwdi/libwdi.c`, `libwdi/installer.c`, `libwdi/winusb.inf.in`)
  - https://github.com/pbatard/libwdi/wiki/Certification-Practice-Statement
  - https://github.com/pbatard/libwdi/issues/289
  - https://github.com/pbatard/libwdi/issues/242
  - https://github.com/pbatard/libwdi/issues/155
- **Microsoft:**
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/winusb-installation
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/automatic-installation-of-winusb
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/pnp-device-installation-signing-requirements--windows-vista-and-later-
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/signature-categories-and-driver-installation
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-windows-ranks-driver-packages
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-windows-selects-a-driver-for-a-device
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-store
  - https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- **Store:**
  - https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
  - https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
  - https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-prepare
- **Wintun:** https://www.wintun.net
- **UsbDk:** https://github.com/daynix/UsbDk
- **libusb (Windows):** https://github.com/libusb/libusb/wiki/Windows
