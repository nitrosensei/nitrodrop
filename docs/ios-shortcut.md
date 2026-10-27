# iPhone → PC: "PC'de Aç" ve "iPhone'da ne açık" Kestirmeleri

Ücretsiz yol: iOS **Kestirmeler** (Shortcuts) uygulaması, aynı Wi-Fi'deki NitroDrop'a düz HTTP ile küçük bir
istek gönderir. Ek uygulama, iCloud, jailbreak gerekmez. NitroDrop tarafı: `app/Nitro.Sync/Net/IosReceiver.cs`.

> Neden HTTPS değil? Kestirmeler'in "URL'nin İçeriğini Al" eylemi kendinden imzalı sertifikayı kabul etmez/pinleyemez.
> Bu yüzden uç nokta **yalnızca LAN adreslerinden** (192.168.x, 10.x, 172.16–31.x, fe80::, fd00::/8) gelen ve
> **cihaza özel 256-bit token** taşıyan istekleri kabul eder. 5 hatalı token → o IP 10 dk kilit. Token diskte
> yalnızca SHA-256 özeti olarak saklanır (`%LOCALAPPDATA%\airdrop-win\sync-peers.json`).

## 1. Kurulum (bir kez)

1. NitroDrop → Ayarlar → **iPhone ekle** → ad girin ("Emre'nin iPhone'u").
2. NitroDrop bir **kurulum adresi** gösterir (QR + metin), örnek:
   `http://192.168.1.20:8772/ios/v1/activity?t=Qm9...xyz`
   Bu adres token içerir; yalnızca kendi telefonunuza aktarın. Kaybolursa NitroDrop'tan cihazı silip yeniden ekleyin
   (eski token anında geçersiz olur).
3. iPhone'da Kamera ile QR'ı okutup adresi kopyalayın (veya AirDrop/NitroDrop ile metin olarak gönderin).

> PC'nin IP'si değişirse (DHCP) kestirmedeki adres de değişmelidir. Modemde PC'ye **sabit IP (DHCP rezervasyonu)**
> verin. Port: 8772 (Windows Güvenlik Duvarı ilk açılışta izin sorar — "Özel ağlar" yeterli).

## 2. "PC'de Aç" — paylaşım menüsünden (tam bağlantı)

Safari sayfası, Instagram gönderisi, YouTube videosu, X gönderisi… "Paylaş → PC'de Aç" ile **tam URL** PC'ye gider.
iOS uygulamaların içini göremediği için *tam ekranı/sohbeti* PC'ye taşımanın tek yolu budur.

Kestirmeler → **+** → ad: **PC'de Aç**

1. Ayrıntılar (ⓘ) → **Paylaşma Ekranında Göster** açık. Kabul edilen türler: **URL'ler**, **Safari web sayfaları**, **Metin**.
2. Eylem: **URL'nin İçeriğini Al** (Get Contents of URL)
   - URL: kurulum adresi (`http://…:8772/ios/v1/activity?t=…`)
   - Yöntem: **POST**
   - İstek Gövdesi: **JSON**
     - `url` (Metin) → *Kestirme Girdisi*
     - `app` (Metin) → boş bırakılabilir (alan adından tanınır: instagram.com → Instagram, youtu.be → YouTube…)
     - `title` (Metin) → isteğe bağlı
3. (İsteğe bağlı) **Bildirim Göster**: "PC'ye gönderildi".

Kabul edilen gövde biçimleri: JSON `{"url": "...", "app": "...", "title": "..."}`, form (`url=...&app=...`) veya
düz metin olarak yalnız URL. Token `?t=` yerine `Authorization: Bearer <token>` başlığıyla da verilebilir.
Yalnızca `http://` ve `https://` bağlantıları kabul edilir; `javascript:`, `file:`, `data:` vb. **400** ile reddedilir.

PC'de ne olur: NitroDrop "iPhone'dan bağlantı geldi" bildirimi gösterir; tıklayınca varsayılan tarayıcıda açılır
(Ayarlar'da "iPhone bağlantılarını otomatik aç" seçilirse doğrudan açılır).

## 3. "iPhone'da ne açık" — kişisel otomasyon (uygulama açılınca)

Amaç: iPhone'da Instagram/WhatsApp/Safari açılınca PC'de "iPhone'da Instagram açık → **PC'de devam et**" görünsün.

Kestirmeler → **Otomasyon** → **+** → **Uygulama**

1. Uygulama: Instagram, WhatsApp, YouTube, X, Telegram, Spotify, Haritalar, Gmail, TikTok… (birden çok seçilebilir)
2. **Açıldığında** (Is Opened) işaretli.
3. **Hemen Çalıştır** (Run Immediately) seçin ve **Çalıştırıldığında Bildir** (Notify When Run) **kapalı** olsun —
   yoksa her uygulama açılışında üstte bildirim çıkar.
4. Eylem: **URL'nin İçeriğini Al**
   - URL: kurulum adresi, Yöntem: **POST**, İstek Gövdesi: **JSON**
   - `app` (Metin) → *Kestirme Girdisi* değil, **elle uygulamanın adı** (her uygulama için ayrı otomasyon kurmak
     en temizidir: "Instagram", "WhatsApp"…). Birden çok uygulamalı tek otomasyonda iOS 17+ "Kestirme Girdisi"
     açılan uygulamayı verir; o zaman `app` → *Kestirme Girdisi*.
   - `ts` (Sayı, isteğe bağlı) → *Geçerli Tarih* (yok sayılır; sunucu kendi saatini kullanır)

PC eşlemesi (tablo: `app/Nitro.Sync/Model/IphoneActivity.cs` → `ContinuationMap`):

| iPhone | PC'de devam et |
|---|---|
| Safari / Chrome | paylaşılan URL (URL yoksa bir şey yapılmaz) |
| Instagram | paylaşılan gönderi; yoksa instagram.com/direct/inbox |
| WhatsApp | WhatsApp Desktop (`whatsapp://`, kuruluysa); yoksa web.whatsapp.com |
| YouTube, X/Twitter, TikTok, Facebook, Messenger, LinkedIn, Reddit, Netflix, GitHub | ilgili web sitesi (link paylaşıldıysa o link) |
| Telegram / Spotify / Discord / Notion | masaüstü uygulaması (`tg://`, `spotify:`, `discord://`, `notion://`) kuruluysa, yoksa web |
| Haritalar / Google Haritalar | Google Haritalar (paylaşılan konum linki varsa o) |
| Mail | varsayılan posta istemcisi (`mailto:`), yoksa Gmail |
| Gmail / Outlook | mail.google.com / outlook.live.com |

**Bilinmesi gereken sınır:** otomasyon yalnızca *hangi uygulamanın açıldığını* söyler. Uygulamanın içindeki sohbet,
gönderi ya da ekran iOS korumalı alanı (sandbox) yüzünden **öğrenilemez**; Apple Handoff verisi de iCloud ile
uçtan uca şifreli olduğu için okunamaz. Belirli bir sohbet/gönderi için "PC'de Aç" paylaşım kestirmesini kullanın.

## 4. Ağ dışında ne olur?

- iPhone evden/ofisten çıkınca (mobil veri, başka Wi-Fi) istek PC'ye ulaşamaz; kestirme birkaç saniye sonra
  zaman aşımına düşer. Otomasyonda **Çalıştırıldığında Bildir kapalıysa sessizce başarısız olur** — iPhone'daki
  uygulamanın açılmasını engellemez. Paylaşım kestirmesi ise "Bağlanılamadı" hatası gösterir.
- Güvenlik gereği uç nokta internete açılmaz; modemde port yönlendirme **yapmayın**. Uzaktan kullanım
  (Tailscale vb.) ileride ayrı ele alınacak.
- Zaman aşımını kısaltmak için otomasyonda "URL'nin İçeriğini Al"dan önce **Wi-Fi ağını al** + **Eğer ağ adı
  = <ev ağı>** koşulu eklenebilir.

## 5. Sorun giderme

| Belirti | Neden |
|---|---|
| 401 | token yanlış/silinmiş — NitroDrop'tan yeni kurulum adresi alın |
| 403 "lan only" | istek LAN dışı bir adresten geldi (VPN açık olabilir) |
| 429 | çok sık istek veya 5 hatalı token sonrası 10 dk kilit |
| 400 "url" | http/https olmayan bağlantı |
| Zaman aşımı | PC kapalı/uykuda, IP değişti, güvenlik duvarı 8772'yi engelliyor |
