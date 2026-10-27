# NitroDrop: WPF vurgu rengi önerisi (theme tokens)

> Öneri dosyasıdır. `app/` altındaki XAML'lara dokunulmadı. Entegrasyonu arayüzü düzenleyen ajan yapacak.
> Hedef dosyalar: `app/AirDrop.App/Themes/Light.xaml`, `Dark.xaml`. Bugün ikisinde de Apple mavisi (#007AFF / #0A84FF) var; bu, markayla çelişiyor ve Apple'a benzerlik riski taşıyor.

## Tokenlar

| Token | Açık tema | Koyu tema | Kullanım |
|---|---|---|---|
| `Accent` | `#CF4518` | `#FF6A3D` | Birincil düğme dolgusu, seçim, ilerleme, odak halkası |
| `AccentHover` | `#BC3E12` | `#FF8159` | Üzerine gelince |
| `AccentPressed` | `#A8360A` | `#F05F32` | Basılı |
| `AccentTextSafe` | `#C2410C` | `#FF7A50` | Turuncu **metin** ve bağlantı (zemin üzerinde) |
| `OnAccent` | `#FFFFFF` | `#1D1F24` | Accent dolgunun üstündeki metin ve ikon |
| `AccentColor` (Color) | `#CF4518` | `#FF6A3D` | `Color` isteyen yerler |
| `Ring` | `#1ACF4518` | `#26FF6A3D` | Radar halkaları (≈ %10 / %15 alfa) |
| `Pulse` | `#33CF4518` | `#40FF6A3D` | Radar nabzı |
| `SelfAvatar` | düz `#1D1F24` | düz `#EEEFF1` | Kendi cihazının avatarı (mavi gradyanın yerine) |

## Kontrast (WCAG 2.x, hesaplandı)

| Çift | Oran | Sonuç |
|---|---|---|
| Açık: beyaz / Accent `#CF4518` | 4,65:1 | AA (düğme metni) |
| Açık: beyaz / Hover `#BC3E12` · Pressed `#A8360A` | 5,47 · 6,57 | AA |
| Açık: AccentTextSafe `#C2410C` / pencere `#F2F2F7` · kart `#FFFFFF` | 4,64 · 5,18 | AA |
| Açık: Accent / pencere (metin dışı öğe, 3:1 gerekir) | 4,17:1 | geçer |
| Koyu: Grafit `#1D1F24` / Accent `#FF6A3D` | 5,79:1 | AA |
| Koyu: Grafit / Hover `#FF8159` · Pressed `#F05F32` | 6,70 · 5,02 | AA |
| Koyu: AccentTextSafe `#FF7A50` / pencere `#1C1C1E` · kart `#2C2C2E` | 6,60 · 5,41 | AA |

Kurallar:
- **Koyu temada accent düğmenin yazısı beyaz değil, Grafit olmalı.** Beyaz `#FF6A3D` üzerinde yalnızca 2,9:1 verir.
- Parlak Sinyal (`#FF6A3D`, `#F0532A`) metin rengi olarak kullanılmaz. Turuncu metin için her zaman `AccentTextSafe` kullanılır.
- `#E30A17` (Türk kırmızısı) arayüz rengi değildir. Yalnızca Hakkında ekranındaki "Türkiye'de yapıldı" imzasındaki küçük çubukta geçer.
- `Trusted` yeşili (#34C759 / #30D158) durum rengi olarak kalabilir.
- Radar halkaları arayüz öğesidir, logo değildir. Turuncuya dönmeleri Apple'ın mavi halkalarıyla karışmayı da önler.

## XAML taslağı

```xml
<!-- Light.xaml -->
<Color x:Key="AccentColor">#CF4518</Color>
<SolidColorBrush x:Key="Accent" Color="#CF4518"/>
<SolidColorBrush x:Key="AccentHover" Color="#BC3E12"/>
<SolidColorBrush x:Key="AccentPressed" Color="#A8360A"/>
<SolidColorBrush x:Key="AccentTextSafe" Color="#C2410C"/>
<SolidColorBrush x:Key="OnAccent" Color="#FFFFFF"/>
<SolidColorBrush x:Key="Ring" Color="#1ACF4518"/>
<SolidColorBrush x:Key="Pulse" Color="#33CF4518"/>
<SolidColorBrush x:Key="SelfAvatar" Color="#1D1F24"/>

<!-- Dark.xaml -->
<Color x:Key="AccentColor">#FF6A3D</Color>
<SolidColorBrush x:Key="Accent" Color="#FF6A3D"/>
<SolidColorBrush x:Key="AccentHover" Color="#FF8159"/>
<SolidColorBrush x:Key="AccentPressed" Color="#F05F32"/>
<SolidColorBrush x:Key="AccentTextSafe" Color="#FF7A50"/>
<SolidColorBrush x:Key="OnAccent" Color="#1D1F24"/>
<SolidColorBrush x:Key="Ring" Color="#26FF6A3D"/>
<SolidColorBrush x:Key="Pulse" Color="#40FF6A3D"/>
<SolidColorBrush x:Key="SelfAvatar" Color="#EEEFF1"/>
```

Not: `SelfAvatar` bugün `LinearGradientBrush`. Türü değişirse onu kullanan stillerin de güncellenmesi gerekir. `AccentHover`, `AccentTextSafe` ve `OnAccent` yeni anahtarlar; mevcut düğme stillerinin (`Foreground="White"` gibi sabit değerler) `OnAccent`'e bağlanması gerekir.

## Simge ve manifest
- Pencere/uygulama simgesi: `app/NitroDrop.ico`
- Tepsi: açık temada `tray-light.ico`, koyu temada `tray-dark.ico`. `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme` izlenip değiştiğinde simge yenilenir.
- MSIX: `store/` klasörü. `Package.appxmanifest` → `uap:VisualElements BackgroundColor="#1D1F24"`, `uap:SplashScreen BackgroundColor="#1D1F24"`. Görev çubuğu için `Square44x44Logo.targetsize-*_altform-unplated` (koyu tema) ve `_altform-lightunplated` (açık tema) dosyaları hazır.
