# NitroDrop: Marka Kimliği, Faz 1 (Üç Konsept)

Tarih: 2026-10-28 · Genel pano: `overview.png` · Konsept panoları: `concept-a/board.png`, `concept-b/board.png`, `concept-c/board.png`

Her konsept klasöründe şunlar var: işaret (açık/koyu), yatay ve dikey logo (açık/koyu), yapı çizimi (48 birimlik ızgara), uygulama ikonu
(`ikon-256.svg` ana çizim; `ikon-32.svg` ve `ikon-16.svg` piksel ızgarasına ayrıca oturtulmuş küçük boylar) ve pano PNG'si.
Kelime işaretleri yola çevrildi, font gerekmez. Bütün fontlar OFL lisanslı (`_build/fonts`, lisans metinleri yanında).

Ortak kurallar:
- Apple'ın AirDrop ikonuna (mavi daire, iç içe halkalar/yaylar) benzeyen bir şey yok. Logoda "AirDrop" kelimesi ve Apple glifleri hiç kullanılmadı. Mavi de kullanılmadı.
- İmza satırı tüm konseptlerde aynı: **BY NITROBYTE | ▍MADE IN TÜRKİYE**. Font JetBrains Mono (Nitrobyte'ın mono fontu).
  "Türkiye"nin önündeki küçük çubuk Türk kırmızısı **#E30A17**. Kırmızı yalnızca bu detayda geçer.
  Bayrak ve ay-yıldız işaretin içine konmadı: kullanımı yasal kurallara bağlı ve işaretin evrenselliğini bozardı.
  Türkçe metinde "Türkiye'de yapıldı" yazılır. Panolarda "Hakkında" alt bilgisi örneği de var (… · İstanbul).
- Kontrast oranları WCAG 2.x formülüyle hesaplandı. Tablolar panolarda.

---

## A: Pas
**Fikir:** Küçük harf "n". Omuz sol gövdeden incecik çıkıp karşıya uzanır, sağ ayak ise eğik bir hız kesiğiyle ayrılıp altın rengine döner. Dosya karşı cihaza pas edilmiş gibi.

- Geometri: dış kemer r18, iç kemer r11. İç kemerin merkezi sola ve yukarı kaydırıldı, bu yüzden omuz-gövde birleşimi ince (gerçek harf tasarımındaki gibi), sağ ayağa doğru kalınlaşıyor. Kesik 3,5 birim genişlikte ve 22° eğik.
- Font: **Archivo SemiExpanded Bold** (Nitrobyte'ın ana yazı ailesi).
- Palet: Gece #17130E · Altın #E8A93A · Koyu Altın #8F5D00 (Nitrobyte altını) · Krem #F5EEE2 · Taş #6E665B · Kum #A79E91
- Kontrast: Gece/açık zemin 17,5 · Koyu Altın/açık zemin 5,3 (AA) · Altın/koyu zemin 9,4 (AAA) · Taş ikincil metin 5,3 (AA)
- 16 px: 3 px gövdeler, 1 px yatay kesik (16 px'te eğik kesik bulanıklaştığı için yataya çevrildi). Altın ayak görev çubuğunda da seçiliyor.

## B: Dosya Damlası
**Fikir:** Ucu sağ üste dönük bir damla. Sivri ucu bir kıvrım çizgisiyle ayrılıp altın rengine döner; bu, sayfanın kıvrık köşesi. İşaret hem damla (drop) hem dosya olarak okunuyor.

- Geometri: 32 birimlik kare, üç köşesi tam yuvarlak (r16). Bu, 45° dönmüş klasik damla demek. Kıvrım çizgisi ucu 10,5 birimden kesiyor, 1,7 birim boşluk bırakıyor.
- Font: **Instrument Sans SemiBold**.
- Palet: Yeşim #0E6B52 · Derin Yeşim #0C5A45 · Taze Yeşim #4CC79A · Kâğıt #F6EFE2 · Kulak Altını #E0B04F · Mürekkep #0F1A16
- Kontrast: Yeşim/açık zemin 5,9 (AA) · Taze Yeşim/koyu zemin 8,9 (AAA) · Kâğıt/Derin Yeşim karo 7,2 (AAA)
- Zayıf yanı: silüet büyük ölçüde daire. 16 px'te altın uç yalnızca 2–3 piksel kalıyor ve işaret "pasta dilimi" gibi okunabiliyor.

## C: Kesişim
**Fikir:** Dikey telefon ile yatay ekran köşeden üst üste biner. Kesiştikleri alan ortak alan: dosya, pano, bildirim ve sekmeler orada buluşur.

- Geometri: telefon 18×32 (r5), ekran 30×22 (r4,5). Kesişim alanı iki şeklin birer yuvarlak köşesini taşıyor.
- Font: **Schibsted Grotesk Bold**.
- Palet: Grafit #1D1F24 · Sinyal #FF6A3D · Koyu Sinyal #C2410C (açık zeminde metin rengi) · Çelik #B9BDC4 · Kâğıt #EEEFF1 · Duman #5B606A
- Kontrast: Koyu Sinyal/açık zemin 4,7 (AA) · Sinyal/koyu zemin 6,6 (AA) · Sinyal/açık zemin 3,2 (yalnız büyük metin veya grafik; gövde metninde kullanılmaz)
- Uygulamanın bütün özelliklerini (pano, bildirim, sekme) en iyi anlatan konsept bu. Ama "iki üst üste dikdörtgen" fikri genel UI ikonlarına (kopyala, pencereler) yakın, en az "sahiplenilebilir" olan da bu.

---

## Öneri: **A, Pas**
1. **Ayırt edici ve sahiplenilebilir.** Bir harf ama sıradan bir monogram değil. İnce omuz ve eğik kesik sayesinde Netflix N'si, kulaklık ya da mıknatıs gibi okunmuyor (ilk taslak simetrikti ve kulaklık gibi duruyordu, bu yüzden asimetrik yapıldı).
2. **Hikâye tek bakışta okunuyor.** Kemer iki cihaz arasındaki yol, ayrılan altın ayak ise karşıya ulaşan dosya. "Nitro" hızı eğik kesikte duruyor.
3. **Ana markaya bağlı ama ona yaslanmıyor.** Archivo ve Nitrobyte altını aileyi belli ediyor. Gece/krem tüketici uygulaması olarak kendi başına duruyor.
4. **Küçük boyda en sağlam olan bu.** 16 px'te harf ve renk ayrımı korunuyor, açık ve koyu görev çubuğunda net.

B ikinci sırada: "drop + dosya" hikâyesi çok güçlü ama küçük boyda daireye dönüşüyor. B seçilirse 16/24 px için ucu abartılmış ayrı bir çizim gerekir.

## Faz 2'de neler olacak (konsept seçildikten sonra)
- Marka kılavuzu PDF (önceki Nitrobyte kimlik teslimi model alınacak)
- 5 logo varyantı, her biri SVG + PDF: `NitroDrop Logo - Ana (Renkli) [VEKTÖR].svg`, `… - Ana (Tek Renk) …`, `… - Siyah-Beyaz …`, `… - İkon …`, `… - Uygulama İkonu …`
- Çok boyutlu `.ico` (16/20/24/32/40/48/64/256), açık ve koyu tepsi ikonları
- Microsoft Store görselleri (karo, logo, ekran görüntüsü çerçeveleri), GitHub sosyal önizleme, README afişi
- "Made in Türkiye" detayı Hakkında ekranına ve açılış ekranına da girecek

## Yeniden üretme
`python _build/build.py`: `uharfbuzz` ve `fonttools` gerekir. PNG'ler Chrome'un headless moduyla render edilir (bu makinede Edge kurulu değil).
