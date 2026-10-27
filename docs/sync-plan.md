# NitroDrop Süreklilik Eşitleme (Nitro.Sync) — Plan ve Durum

Hedef: açık Chrome/Edge sekmeleri, çalışan uygulamalar ve iPhone'dan gelen bağlantılar/etkinlik NitroDrop
kurulu cihazlar arasında görünsün; tıklayınca diğer cihazda açılsın. iPhone **Handoff kapsam dışı** (aşağıya bakın).

## Bileşenler

| Parça | Yer | Ne yapar |
|---|---|---|
| Model | `app/Nitro.Sync/Model/` | `DeviceState` (cihaz id/ad, `BrowserTab`, `AppInfo`), `StateDiff`, ETag, `StateSanitizer` (limitler), `IphoneActivity` + `ContinuationMap` |
| Uygulama listesi | `app/Nitro.Sync/Windows/WindowsAppsProvider.cs` | EnumWindows + süreç bilgisi; görünür, sahipsiz, cloaked olmayan, tool-window olmayan pencereler; UWP için ApplicationFrameHost çözümü; 16×16 ikon → PNG (System.Drawing'siz) |
| Eş protokolü | `app/Nitro.Sync/Net/` | `_nitrosync._tcp` mDNS + HTTPS JSON (AirDrop.Core'un `MiniHttpServer`/`MdnsService`/`NetworkBinding`/TLS kimliği **değiştirilmeden** kullanılır) |
| Güvenlik | `app/Nitro.Sync/Security/` | Eşleştirme (6 haneli kod), TOFU sertifika pini, istek imzası (HMAC), tekrar koruması, hız sınırı, URL politikası, DPAPI'li `sync-peers.json` |
| iOS alıcı | `app/Nitro.Sync/Net/IosReceiver.cs` | Düz HTTP, LAN-only, cihaz başına token; bağlantı + "uygulama açıldı" bildirimi (`docs/ios-shortcut.md`) |
| Uzantı köprüsü | `app/Nitro.Sync/Ipc/` | Native Messaging çerçeveleme + `NitroDrop.Sync.<SID>` adlı pipe sunucusu |
| Native host | `app/Nitro.Sync.Host/` (`NitroDrop.SyncHost.exe`) | stdio ↔ pipe aktarıcı, `register`/`unregister` |
| Tarayıcı uzantısı | `extension/` | MV3, Chrome + Edge; sekme toplama, popup ("Diğer cihazlar", "Telefona/PC'ye gönder") |
| Facade | `app/Nitro.Sync/NitroSyncService.cs` | WPF uygulamasının kullanacağı tek sınıf |

## Eş protokolü (PC ↔ PC)

- Keşif: mDNS `_nitrosync._tcp.local.`, örnek adı = cihaz id'si (12 hex), TXT `v=1 id=… name=…`. Ayrı bir
  `MdnsService` örneği (5353 SO_REUSEADDR ile paylaşılır).
- HTTPS (varsayılan port 8771, doluysa rastgele), AirDrop ile aynı TLS kimliği
  (`%LOCALAPPDATA%\airdrop-win\tls-identity.pfx`).
- Uç noktalar (`/nitrosync/v1/…`):
  - `GET /info` — herkese açık: id, ad, sürüm.
  - `POST /pair/start`, `POST /pair/finish` — eşleştirme (aşağıda).
  - `GET /state[?wait=25]` + `If-None-Match` — durum JSON'u, `ETag`; değişiklik yoksa 25 sn'ye kadar bekletir
    (long-poll), sonra `304`.
  - `POST /open {url,title}` — yalnızca http/https; varsayılan tarayıcıda açılır (`ConfirmOpen` ile onaya bağlanabilir).
  - `POST /launch {appId}` — yalnızca **hedefin kendi duyurduğu** uygulama listesinden; yerel kullanıcı onayı
    (`ConfirmLaunch`) zorunlu; exe yolu ağdan gelmez, argüman geçirilmez.
  - `POST /unpair` — karşı taraf bizi unutur.
- `/info` ve eşleştirme dışındaki her istek **imzalı** olmalı; eşleşmemiş cihaz → `401`.

### Eşleştirme (sayısal karşılaştırma, Bluetooth SSP benzeri)

1. A → B `pair/start`: A'nın id'si, sertifika SHA-256'sı, ECDH P-256 açık anahtarı, 16 bayt nonce.
2. B kendi ECDH anahtarı + nonce ile yanıt verir. Her iki taraf aynı transkripti kurar:
   `idA | idB | sertA | sertB | ecdhA | ecdhB | nonceA | nonceB` (A'nın gördüğü sertB, TLS'ten okunur).
3. **Kod** = SHA-256("nitrosync-sas" | transkript) → 6 hane. İki PC de `ConfirmPairing` ile kodu gösterir,
   kullanıcı iki ekranda aynı olduğunu onaylar. Araya giren biri sertifika/ECDH anahtarını değiştirirse kod değişir.
4. Anahtar = HKDF-SHA256(ECDH, tuz = nonceA|nonceB, info = "nitrosync-v1" | SHA-256(transkript)).
5. A onaylarsa `pair/finish` ile anahtar-onay MAC'i gönderir; B kendi kullanıcısının kararını (90 sn) bekler,
   MAC'i doğrular, kendi MAC'ini döner. İki taraf da `sync-peers.json`'a karşı tarafın **sertifika parmak izini
   (TOFU pin)** ve DPAPI ile sarılmış 32 baytlık anahtarı yazar.

### İstek imzası

`X-Nitro-Device / -Time / -Nonce / -Sig`; imza = HMAC-SHA256(anahtar, `METHOD\nPATH?QUERY\nTIME\nNONCE\nsha256(gövde)\nHEDEF_ID`).
±5 dk saat farkı, nonce 10 dk hatırlanır (tekrar saldırısı), hedef id'si imzada olduğu için başka bir PC'ye
yansıtılamaz. İstemci tarafı sunucu sertifikasını pinler (pin uyuşmazlığı → bağlantı kurulmaz).
Neden istemci sertifikası değil: `MiniHttpServer` istemci sertifikası istemiyor ve AirDrop.Core'a dokunmuyoruz.

### Sınırlar

- IP başına 30 istek ani / 10 sn⁻¹; `pair/start` IP başına 3 ani / 20 sn'de 1; aynı anda en çok 4 bekleyen eşleştirme.
- Küçük gövdeler 16 KiB; durum en çok 2 MiB; 500 sekme, 100 uygulama, başlık 300, ad 120 karakter, ikon 16 KiB.
- Gizli/InPrivate sekmeler hiç toplanmaz (uzantı `incognito: not_allowed` + `tab.incognito` filtresi + sunucu
  tarafında `Incognito` bayraklı sekmeler atılır). Gizli pencere başlıkları (InPrivate/Incognito/Gizli…) uygulama
  listesinde boş gönderilir. `chrome://`, `file://`, `about:` sekmeler paylaşılmaz.
- Uygulama listesinde exe'nin **yalnız dosya adı** gider, tam yol PC'de kalır. Hariç tutma listesi
  `AppsProviderOptions.ExcludedExecutables` (varsayılan: parola yöneticileri, kabuk süreçleri);
  `IncludeWindowTitles=false` ile pencere başlıkları kapatılabilir; `ShareApps=false` tamamen kapatır.

## Uzantı ↔ NitroDrop

```
Chrome/Edge uzantısı ──(stdio, 4 bayt LE uzunluk + UTF-8 JSON)── NitroDrop.SyncHost.exe ──(aynı çerçeve, named pipe)── NitroDrop
```

- Pipe: `NitroDrop.Sync.<kullanıcı SID>`; DACL yalnız mevcut kullanıcıya izin verir, NETWORK SID reddedilir;
  host `PipeOptions.CurrentUserOnly` ile bağlanır (sunucu başka kullanıcıysa reddeder). Tek-örnek pipe'ından ayrı.
- Mesajlar (uzantı → NitroDrop): `hello{browser}`, `tabs{browser,tabs[]}`, `getDevices`, `getTargets`,
  `sendLink{targetId,url,title}`, `continue{deviceId}`. Yanıtlar `reqId`'yi geri yollar.
  NitroDrop → uzantı: `hello`, `devices{devices[]}` (değişince kendiliğinden itilir), `targets`, `sendResult`, `error`.
- Boyut: tarayıcıya giden mesaj ≤ 1 MiB (cihaz başına en yeni 100 sekme), tarayıcıdan gelen ≤ 4 MiB.
- Tarayıcı kapanınca (pipe oturumu biter) o tarayıcının sekmeleri paylaşılan durumdan silinir.

### Kayıt (bu makinede ÇALIŞTIRILMADI)

```powershell
NitroDrop.SyncHost.exe register --extension-id kbdhonkcoaopcnmbnphldonokaooaibp
NitroDrop.SyncHost.exe unregister
```

`register`, `%LOCALAPPDATA%\airdrop-win\native-host\com.nitrobyte.nitrodrop.json` manifestini yazar ve
`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.nitrobyte.nitrodrop` ile
`HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.nitrobyte.nitrodrop` varsayılan değerine yolunu koyar.
Kurulum sihirbazı/kurulumcu bunu çağırmalı (exe yolu değişirse yeniden).

### Sabit uzantı kimliği

`extension/manifest.json` içindeki `"key"` alanı bir RSA-2048 açık anahtarıdır (SPKI, base64). Chrome/Edge
paketlenmemiş uzantının kimliğini bu anahtardan türetir: SHA-256'nın ilk 16 baytı, her hex hane `0-f → a-p`.
Bu anahtarla kimlik **`kbdhonkcoaopcnmbnphldonokaooaibp`** — "Paketlenmemiş öğe yükle" ile her makinede aynı olur,
`allowed_origins` buna göre yazılır. Özel anahtar saklanmadı (paketlenmemiş yükleme için gerekmez).
Chrome Web Store / Edge Add-ons'a yüklerken mağaza kendi kimliğini verir: o kimliği de
`register --extension-id <mağaza-id>` ile ikinci `--extension-id` olarak ekleyin (ya da mağazanın verdiği
açık anahtarı `key` alanına koyun). Yeni anahtar üretmek için: RSA-2048 üret → SPKI DER → base64 → `key`.

## iPhone

- **Handoff kapsam dışı:** Handoff etkinlikleri (NSUserActivity) Apple'ın iCloud anahtarlarıyla uçtan uca
  şifreli BLE/AWDL yükleriyle taşınır; Apple kimliği olmadan okunamaz. Denenmeyecek.
- **Ücretsiz yol:** Kestirmeler → `IosReceiver` (`docs/ios-shortcut.md`):
  - Paylaşım menüsü "PC'de Aç": tam URL (Instagram gönderisi, Safari sayfası…).
  - Kişisel otomasyon "Uygulama açıldığında": yalnızca uygulama adı → `IphoneActivity` → `ContinuationMap` ile
    PC hedefi (Instagram → instagram.com/direct/inbox, WhatsApp → `whatsapp://` kuruluysa yoksa web.whatsapp.com …).
- **Açık sınır:** uygulamanın içindeki **sohbet/ekran/gönderi bilinemez** — iOS korumalı alanı (sandbox) başka
  uygulamaların durumunu Kestirmeler'e vermez, Handoff da iCloud-şifreli. Kullanıcı bağlantı paylaşmadıkça PC
  yalnızca "iPhone'da Instagram açık" diyebilir ve uygulamanın ana sayfasını açar.
- Uygulama URI şemaları (`whatsapp:`, `tg:`, `spotify:`, `discord:`, `notion:`, `mailto:`) **sabit bir listeden**
  gelir; ağdan gelen URI asla açılmaz. Ağdan gelen URL'ler yalnızca http/https.

## UI'ya bağlama (ana ajan için)

```csharp
var sync = new NitroSyncService(new NitroSyncOptions
{
    DeviceName = settings.DeviceName,            // AirDrop'taki ad
    Certificate = airDropCertificate,           // isteğe bağlı; varsayılan aynı pfx
    Log = s => logger.Log(s),
});
sync.ConfirmPairing  = (prompt, ct) => Dispatcher.InvokeAsync(() => PairDialog.Show(prompt.PeerName, prompt.Code, ct)).Task.Unwrap(); // iki PC'de aynı kod
sync.ConfirmLaunch   = (req, ct)    => AskAsync($"{req.FromName}, {req.AppName} uygulamasını açmak istiyor", ct);
sync.ConfirmOpen     = null;           // eşleşmiş PC'den gelen linkleri doğrudan aç (veya onaya bağla)
sync.AutoOpenIosLinks = false;         // iPhone linkleri: bildirim + "PC'de devam et"
sync.ExternalLinkTargets = () => airDrop.Peers.Where(p => p.Kind == DeviceKind.iPhone)
                                   .Select(p => new LinkTarget("airdrop:" + p.Id, p.Name, "phone")).ToList();
sync.SendLinkToExternal  = (id, url, title, ct) => airDrop.SendLinkAsync(id["airdrop:".Length..], url, ct);

sync.PeerStateChanged      += (_, peer) => Ui(() => DevicesVm.Update(peer));      // SyncPeerInfo: IsPaired, IsOnline, State.Tabs/Apps
sync.LinkReceived          += (_, link) => Ui(() => Toast($"{link.FromName}: {link.Url}"));
sync.IphoneActivityChanged += (_, a)    => Ui(() => Banner($"iPhone'da {a.Target?.DisplayName ?? a.App} açık", () => sync.ContinueOnPcAsync(a)));

await sync.StartAsync();
// …
sync.GetPeers();                                   // görünür + eşleşmiş PC'ler
await sync.PairAsync(peer.DeviceId);               // PairResult(Success, Code, Error)
await sync.OpenOnPeerAsync(peer.DeviceId, url);    // bool; http/https dışı → ArgumentException
await sync.LaunchOnPeerAsync(peer.DeviceId, app.AppId);
await sync.UnpairAsync(peer.DeviceId);
IosSetup s = sync.AddIosDevice("Emre'nin iPhone'u"); // s.Url → QR olarak göster (token içerir, bir kez göster)
sync.RemoveIosDevice(s.DeviceId);
await sync.DisposeAsync();
```

Olaylar arka plan iş parçacığında gelir → UI'da `Dispatcher` kullanın. QR çizimi UI'nın işi (Nitro.Sync URL verir).

## Doğrulanmamış / açık işler

- İki **fiziksel** PC arasında LAN üzerinde denenmedi (testler loopback'te iki süreç-içi düğüm).
- Uzantı gerçek Chrome/Edge'e yüklenmedi; native host kaydı bu makinede yapılmadı (istenmedi).
  Host'un yalnızca "NitroDrop çalışmıyor" yolu elle denendi.
- MV3 service worker'ın açık native port ile canlı kalması Chrome 105+ davranışına dayanır; Edge'de gözlenmedi.
- iOS Kestirmesi gerçek iPhone'da denenmedi (uç nokta testlerde ham HTTP ile doğrulandı); "Kestirme Girdisi"nin
  çok uygulamalı otomasyonda uygulama adını verdiği iOS 17+ davranışı doğrulanmadı.
- WindowsAppsProvider bu makinede çalıştı (test), UWP/ApplicationFrameHost çözümü ve ikon kalitesi gözle kontrol edilmedi.
- Sertifika 2 yılda bir yenilenince (CertificateStore) pinler bozulur → yeniden eşleştirme gerekir; ileride
  "eski sertifikayla imzalı yeni sertifika" geçişi eklenebilir.
- Uzak (LAN dışı) eşitleme yok; Tailscale gibi bir katman ileride.
