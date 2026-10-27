# NitroDrop: Marka Kimliği (Faz 2, son paket)

Seçilen konsept: **C, Kesişim**, rafine edilmiş hali (R1). Önce/sonra karşılaştırması `refinement.png` dosyasında.
Rafine, işareti gerçek cihaz oranlarına çekti: telefon 16×34 ve büyük köşe (r6), ada kesiği var; ekran 16:10 ve dar köşe (r2,5).
Turuncu kesişim alanı iki farklı köşe karakteri taşıyor, bu da işareti genel "kopyala / pencereler" ikonlarından ayırıyor.
Dizüstü tabanlı R2 alternatifi de denendi; 16 px'te kalabalıklaştığı için elendi.

## İçerik
| Yol | Ne |
|---|---|
| `NitroDrop Marka Kimliği Kılavuzu.pdf` | 12 sayfalık kılavuz: kapak, hikâye, yapı ızgarası, güvenli alan ve min boy, düzenler, renk (HEX/RGB/CMYK + WCAG), tipografi, Made in Türkiye imzası, doğru/yanlış kullanım, ikon ve tepsi, örnekler, teslim listesi |
| `NitroDrop Logo - <Varyant> [VEKTÖR].svg/.pdf` | Ana (Renkli) · Ana (Tek Renk) · Siyah-Beyaz · İkon · Uygulama İkonu |
| `ek/` | Negatif, dikey ve imzalı düzenler, beyaz tek renk (SVG) |
| `app/NitroDrop.ico` | 16 · 20 · 24 · 32 · 40 · 48 · 64 · 256 (16–32 piksel ızgarasına elle oturtuldu) |
| `app/tray-light.ico`, `app/tray-dark.ico` | Tek renk tepsi simgeleri: açık temada koyu çizim, koyu temada beyaz çizim (16–32) |
| `app/png/`, `app/svg/` | Uygulama ikonu 16–1024 PNG, tepsi PNG'leri, küçük boy SVG kaynakları |
| `app/store/` | MSIX/Store: Square44 (scale-100…400 + targetsize 16/24/32/48/256, unplated ve lightunplated), Square150, Wide310x150, Square310, StoreLogo, SplashScreen |
| `app/github-social-preview.png` | 1280×640, "by Nitrobyte · Made in Türkiye" satırıyla |
| `app/readme-banner-light/dark.png` | 1280×320, README için (GitHub `<picture>` ile tema seçimi) |
| `app/theme-tokens.md` | WPF arayüzü için accent tokenları (açık/koyu) |

## Yeniden üretme
```
cd brand/final/_build
python refinement.py    # önce/sonra panosu
python build_final.py   # logolar, ICO, PNG, Store, sosyal görseller
python guide.py         # kılavuz PDF + preview/sayfa-NN.png
```
Gerekenler: Python 3, `uharfbuzz`, `Pillow`; Google Chrome (headless). İsteğe bağlı: `pymupdf` (PDF kontrolü için).
Fontlar `_build/fonts` klasöründe (Schibsted Grotesk, JetBrains Mono; OFL). Kelime işaretleri yola çevrildiği için logo dosyaları font gerektirmez.

README'de banner kullanımı:
```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="brand/final/app/readme-banner-dark.png">
  <img alt="NitroDrop" src="brand/final/app/readme-banner-light.png">
</picture>
```
