"""NitroDrop Marka Kimliği Kılavuzu — HTML → PDF (Chrome print-to-pdf) + sayfa önizlemeleri (PNG)."""
import os, base64
from common import *
from build_final import social_card, readme_banner, tray, clear_pad

PW, PH = 1123, 794  # A4 yatay @96dpi
OUT_PDF = os.path.join(FINAL, "NitroDrop Marka Kimliği Kılavuzu.pdf")

def img(svg_text, style=""): return f'<img src="{data_uri(svg_text)}" style="{style}"/>'
def png_uri(path):
    with open(path, "rb") as f: return "data:image/png;base64," + base64.b64encode(f.read()).decode()

def L(o_w_h, style=""):
    o, W, H = o_w_h; return img(svg(round(W, 2), round(H, 2), o), style)

def page(n, title, body, dark=False, kicker=""):
    cls = "page dark" if dark else "page"
    return f"""<section class="{cls}"><div class="kick">{kicker}</div><h2>{title}</h2>{body}
<footer><span>NITRODROP · MARKA KİMLİĞİ KILAVUZU · V1.0 · 2026-10</span><span>{n:02d}</span></footer></section>"""

def swatch(name, hexv, role, text_on="#fff", big=False):
    r, g, b = rgb(hexv); c, m, y, k = cmyk(hexv)
    return (f'<div class="sw{" big" if big else ""}"><div class="chip" style="background:{hexv};color:{text_on}"><b>{name}</b><span>{role}</span></div>'
            f'<dl><dt>HEX</dt><dd>{hexv}</dd><dt>RGB</dt><dd>{r} {g} {b}</dd><dt>CMYK</dt><dd>{c} {m} {y} {k}</dd></dl></div>')

def crow(fg, bg, label):
    r = contrast(fg, bg)
    return f'<tr><td><i style="background:{bg};color:{fg}">Aa</i></td><td>{label}</td><td class="r">{r:.2f}:1</td><td>{grade(r)}</td></tr>'

def construction():
    s = master()
    g = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49))
    g4 = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49, 4))
    S = 'fill="none" stroke="#E30A17" stroke-width=".12"'
    px, py, pw, ph, pr = s["phone"]; sx, sy, sw, sh, sr = s["screen"]; ix, iy, iw, ih = s["island"]
    guides = (f'<circle cx="{px+pr}" cy="{py+pr}" r="{pr}" {S}/><circle cx="{px+pw-pr}" cy="{py+ph-pr}" r="{pr}" {S}/>'
              f'<circle cx="{sx+sr}" cy="{sy+sr}" r="{sr}" {S}/><circle cx="{sx+sw-sr}" cy="{sy+sh-sr}" r="{sr}" {S}/>'
              f'<line x1="{sx}" y1="0" x2="{sx}" y2="48" {S} stroke-dasharray=".5 .4"/><line x1="{px+pw}" y1="0" x2="{px+pw}" y2="48" {S} stroke-dasharray=".5 .4"/>'
              f'<line x1="0" y1="{sy}" x2="48" y2="{sy}" {S} stroke-dasharray=".5 .4"/><line x1="0" y1="{py+ph}" x2="48" y2="{py+ph}" {S} stroke-dasharray=".5 .4"/>')
    lab = lambda x, y, t: f'<text x="{x}" y="{y}" font-family="Mono" font-size="1.15" fill="#E30A17">{t}</text>'
    labels = (lab(px + pw + 1, 4.6, "TELEFON 16×34 · r6") + lab(sx + 7, sy + sh + 2.8, "EKRAN 32×20 · 16:10 · r2,5")
              + lab(sx + .4, 47.3, "x13") + lab(px + pw + .4, 47.3, "x23") + lab(.4, sy - .5, "y19") + lab(.4, py + ph - .5, "y37")
              + lab(px + pw + 1, 7.6, "ADA 5×2 · tam yuvarlak"))
    inner = (f'<rect width="48" height="48" fill="#fff"/><g stroke="#000" stroke-opacity=".05" stroke-width=".06">{g}</g>'
             f'<g stroke="#000" stroke-opacity=".13" stroke-width=".1">{g4}</g><g opacity=".9">{mark_color(s, COL_LIGHT)}</g>{guides}{labels}')
    return svg(48, 48, inner, size=False)

def clearspace():
    p = clear_pad(); o, W, H = lockup_h(COL_LIGHT, C["graphite"], pad=p)
    ch = cap(100)
    box = (f'<rect x="0" y="0" width="{W}" height="{H}" fill="none" stroke="#E30A17" stroke-width="1.2" stroke-dasharray="5 4"/>'
           f'<rect x="{p}" y="{p}" width="{W-2*p}" height="{H-2*p}" fill="none" stroke="#E30A17" stroke-opacity=".5" stroke-width="1"/>')
    xs = "".join(f'<rect x="{x}" y="{y}" width="{p}" height="{p}" fill="#E30A17" fill-opacity=".10" stroke="#E30A17" stroke-width=".8"/>'
                 f'<text x="{x+p/2}" y="{y+p/2+6}" text-anchor="middle" font-family="Mono" font-size="16" fill="#E30A17">x</text>'
                 for x, y in [(0, 0), (W - p, 0), (0, H - p), (W - p, H - p)])
    return svg(round(W, 1), round(H, 1), o + box + xs)

def misuse_cards():
    s = master(); o, W, H = lockup_h(COL_LIGHT, C["graphite"])
    good = svg(round(W), round(H), o)
    def mk(inner, w=W, h=H, extra_bg=None, p=0):
        bg = f'<rect x="{-p-40}" y="{-p-40}" width="{w+2*p+80}" height="{h+2*p+80}" fill="{extra_bg}"/>' if extra_bg else ""
        return svg(round(w + 2*p), round(h + 2*p), bg + inner, vb=f"{-p} {-p} {w+2*p:.1f} {h+2*p:.1f}")
    blue = dict(phone="#1D1F24", screen="#9EA3AC", share="#007AFF")
    o_blue, *_ = lockup_h(blue, C["graphite"])
    rings = ('<g fill="none" stroke="#0A84FF" stroke-width="5"><circle cx="70" cy="80" r="22"/><circle cx="70" cy="80" r="40"/><circle cx="70" cy="80" r="58"/></g>')
    wd, ww = word(160, 115, 100, C["graphite"], "AirDrop")
    items = [
        (mk(f'<g transform="translate({W/2} {H/2}) scale(1 .62) translate({-W/2} {-H/2})">{o}</g>'), "Esnetme, sıkıştırma"),
        (mk(f'<g transform="rotate(-10 {W/2} {H/2})">{o}</g>', p=60), "Döndürme, eğme"),
        (mk(o_blue), "Renkleri değiştirme (özellikle mavi)"),
        (svg(560, 160, rings + wd), "Asla: mavi iç içe halkalar, “AirDrop” sözcüğü"),
        (mk(f'<g filter="url(#sh)">{o}</g><defs><filter id="sh"><feDropShadow dx="6" dy="8" stdDeviation="6" flood-opacity=".55"/></filter></defs>'), "Gölge, parıltı, gradyan efekti"),
        (mk(o.replace(C["graphite"], "#6B7078"), extra_bg="#9AA0A8"), "Düşük kontrastlı zemin"),
        (mk(lockup_h(COL_LIGHT, C["graphite"])[0].replace('translate(', 'translate(60 0) translate(', 1), w=W + 60), "İşaret–yazı aralığını değiştirme"),
        (mk(f'<g transform="translate(0 0)">{mark_color(s, dict(phone="#E30A17", screen="#E30A17", share="#fff"))}</g>', 48, 48), "Bayrak/ay-yıldız ya da kırmızı işaret"),
    ]
    cards = "".join(f'<div class="mis"><div class="mi">{img(sv)}</div><p><b>✕</b> {t}</p></div>' for sv, t in items)
    return good, cards

def taskbar(dark):
    bg = "#1F1F1F" if dark else "#EEF0F3"; ic = app_icon(24); tr = tray(16, "#FFFFFF" if dark else "#1C1C1C")
    o = "".join(f'<span class="tb-o" style="background:{"#3a3a3a" if dark else "#d6d9de"}"></span>' for _ in range(3))
    return (f'<div class="taskbar" style="background:{bg}">{o}<span class="tb-a">{img(ic, "width:24px;height:24px")}'
            f'<em style="background:{"#9a9a9a" if dark else "#8a8f98"}"></em></span>{o[:len(o)//3]}'
            f'<span class="tray">{img(tr, "width:16px;height:16px")}<small style="color:{"#ddd" if dark else "#333"}">14:05<br>28.10.2026</small></span></div>')

def build():
    ch = cap(100)
    soc_png = os.path.join(FINAL, "app", "github-social-preview.png")
    good, mis = misuse_cards()
    pages = []
    # 1 kapak
    o, W, H = lockup_v(COL_DARK, C["paper"], sub=C["fog"])
    pages.append(f"""<section class="page cover"><div class="cv">{L((o, W, H), "height:330px")}</div>
<div class="cvf"><span>MARKA KİMLİĞİ KILAVUZU · v1.0</span><span>EKİM 2026</span></div></section>""")
    # 2 hikâye
    s = master()
    pages.append(page(2, "Hikâye", f"""<div class="two"><div>
<p class="lead">NitroDrop, Windows bilgisayarı iPhone ve Mac ile aynı masaya oturtur. Dosya, pano, bildirim, sekme: hepsi iki cihazın <em>ortak alanında</em> buluşur.</p>
<p>İşaret iki gerçek cihazdan oluşuyor. Dik duran bir telefon, büyük köşeleri ve ada kesiğiyle. Yatay bir ekran, 16:10 oranı ve dar köşeleriyle.
İkisi köşeden üst üste biniyor. Kesiştikleri alan turuncu: iki cihazın paylaştığı alan, yani NitroDrop'un kendisi.</p>
<p>Turuncu alanın iki köşesi farklı: sol üst ekrandan gelen dar köşe, sağ alt telefondan gelen geniş köşe.
Bu ayrıntı, işareti sıradan “üst üste iki pencere” ikonlarından ayırır. Paylaşılan alan iki tarafın da karakterini taşır.</p>
<p class="small">İsim: <b>Nitro</b> (Nitrobyte'tan, hız) + <b>Drop</b> (bırak, gönder). Logoda Apple'a ait hiçbir işaret, renk ya da sözcük kullanılmaz.</p>
</div><div class="hero">{img(svg(48, 48, f'<g transform="{place(s, 48, 48, 40)}">{mark_color(s, COL_LIGHT)}</g>', size=False), "width:360px")}</div></div>""", kicker="01 · Anlam"))
    # 3 yapı
    pages.append(page(3, "Yapı ızgarası", f"""<div class="two"><div class="gridimg">{construction().replace("<svg ", "<svg width=\"470\" height=\"470\" ", 1)}</div><div>
<p>İşaret 48 birimlik bir ızgara üzerinde çizilir. Bütün ölçüler tam ya da yarım birimdir.</p>
<table class="spec"><tr><td>Telefon</td><td>16 × 34 birim, köşe r6 (oran ≈ 2,1:1)</td></tr>
<tr><td>Ada kesiği</td><td>5 × 2 birim, tam yuvarlak, üstten 3 birim</td></tr>
<tr><td>Ekran</td><td>32 × 20 birim (16:10), köşe r2,5</td></tr>
<tr><td>Kesişim</td><td>x 13–23 · y 19–37 (10 × 18). Sol üst r2,5, sağ alt r6</td></tr>
<tr><td>Katman sırası</td><td>Ekran → telefon → kesişim (turuncu)</td></tr></table>
<p class="small">Küçük boylar (32, 24, 20, 16 px) piksel ızgarasına ayrıca çizilmiştir: kenarlar tam piksele oturur, ada kesiği 20 px ve altında kaldırılır. Bu dosyalar <code>app/svg/</code> klasöründedir. Ana çizimi küçültüp kullanmayın.</p>
</div></div>""", kicker="02 · Geometri"))
    # 4 güvenli alan ve min boy
    pages.append(page(4, "Güvenli alan ve en küçük boyut", f"""<div class="cs">{clearspace().replace("<svg ", "<svg style=\"width:720px;height:auto\" ", 1)}</div>
<div class="three">
<div><h4>Güvenli alan</h4><p><b>x</b> = “N” büyük harf yüksekliğinin 0,7'si (yaklaşık telefonun genişliği). Logonun dört yanında en az x kadar boşluk bırakılır. Vektör dosyaların sınır kutusu bu boşluğu zaten içerir.</p></div>
<div><h4>En küçük boyut: ekran</h4><p>Yatay logo en az <b>96 px</b> genişlikte kullanılır. İşaret tek başına en az <b>16 px</b> olur; 16–32 px arası için piksel çizimleri kullanılır.</p></div>
<div><h4>En küçük boyut: baskı</h4><p>Yatay logo en az <b>25 mm</b>, işaret en az <b>6 mm</b>. Daha küçük kullanımlarda tek renk (XOR) sürümü tercih edilir.</p></div></div>""", kicker="03 · Kullanım alanı"))
    # 5 düzenler
    pages.append(page(5, "Logo düzenleri", f"""<div class="lk4">
<div class="cell">{L(lockup_h(COL_LIGHT, C["graphite"]), "height:66px")}<span>Yatay · ana kullanım</span></div>
<div class="cell dk">{L(lockup_h(COL_DARK, C["paper"]), "height:66px")}<span>Yatay · koyu zemin</span></div>
<div class="cell">{L(lockup_v(COL_LIGHT, C["graphite"]), "height:150px")}<span>Dikey · kare alanlar</span></div>
<div class="cell dk">{L(lockup_v(COL_DARK, C["paper"]), "height:150px")}<span>Dikey · koyu zemin</span></div>
<div class="cell">{L(lockup_h(None, C["signal_text"], mono=C["signal_text"]), "height:50px")}<span>Tek renk · Koyu Sinyal</span></div>
<div class="cell">{L(lockup_h(None, C["black"], mono=C["black"]), "height:50px")}<span>Siyah-beyaz</span></div>
</div><p class="small">Tek renkli sürümlerde telefon ile ekran arasındaki kesişim boşluk olarak bırakılır (XOR). Bu yüzden işaret tek mürekkeple de okunur. Dosyalar: <code>NitroDrop Logo - … [VEKTÖR].svg/.pdf</code>, ek düzenler <code>ek/</code> klasöründe.</p>""", kicker="04 · Düzenler"))
    # 6 renk
    sw1 = (swatch("Grafit", C["graphite"], "ana renk · metin") + swatch("Sinyal", C["signal"], "yalnız grafik · koyu zemin", "#1D1F24")
           + swatch("Sinyal (açık)", C["signal_light"], "yalnız grafik · açık zemin", "#fff") + swatch("Koyu Sinyal", C["signal_text"], "turuncu metin · açık zemin")
           + swatch("Sinyal Metin", C["signal_text_dark"], "turuncu metin · koyu zemin", "#1D1F24"))
    sw2 = (swatch("Kâğıt", C["paper"], "açık yüzey · koyu zeminde metin", "#1D1F24") + swatch("Çelik", C["steel"], "ekran · açık zemin", "#1D1F24")
           + swatch("Duman", C["smoke"], "ikincil metin · açık") + swatch("Sis", C["fog"], "ikincil metin · koyu", "#1D1F24")
           + swatch("Mürekkep", C["ink"], "koyu zemin"))
    rows = (crow(C["graphite"], "#FFFFFF", "Grafit / beyaz") + crow(C["signal_text"], "#FFFFFF", "Koyu Sinyal / beyaz")
            + crow(C["signal_text"], C["bg_light"], "Koyu Sinyal / açık gri") + crow(C["smoke"], "#FFFFFF", "Duman / beyaz")
            + crow(C["paper"], C["ink"], "Kâğıt / Mürekkep") + crow(C["signal_text_dark"], C["ink"], "Sinyal Metin / Mürekkep")
            + crow(C["fog"], C["ink"], "Sis / Mürekkep") + crow(C["signal_light"], "#FFFFFF", "Sinyal (açık) / beyaz")
            + crow("#FFFFFF", C["signal"], "Beyaz / Sinyal") + crow(C["graphite"], C["signal"], "Grafit / Sinyal"))
    pages.append(page(6, "Renk sistemi", f"""<div class="sws">{sw1}</div><div class="sws">{sw2}</div>
<div class="two" style="margin-top:14px;gap:28px;grid-template-columns:1.1fr 1fr"><table class="ck">{rows}</table>
<div class="note"><h4>Kurallar</h4><p><b>Parlak Sinyal turuncusu (#FF6A3D / #F0532A) metin rengi değildir.</b> Yalnızca işaretin kesişim alanında, ikonlarda, çizgilerde ve büyük grafik alanlarda kullanılır.
Açık zeminde turuncu metin gerekiyorsa <b>Koyu Sinyal #C2410C</b> (5,2:1), koyu zeminde <b>Sinyal Metin #FF7A50</b> (6,9:1) kullanılır.</p>
<p>Sinyal dolgulu düğmede yazı <b>Grafit</b> olur (5,8:1); beyaz yazı yalnızca 2,9:1 verir. Türk kırmızısı (#E30A17) paletin parçası değildir, yalnızca imzadaki küçük çubukta geçer.</p>
<p class="small">CMYK değerleri RGB'den hesaplanmış yaklaşık değerlerdir; baskı öncesi prova alınmalıdır. Kontrast: WCAG 2.x.</p></div></div>""", kicker="05 · Renk"))
    # 7 tipografi
    pages.append(page(7, "Tipografi", f"""<div class="two"><div>
<div class="tsample" style="font-family:Schibsted;font-weight:700;font-size:96px;letter-spacing:-.02em;line-height:1">Aa Ğğ Şş İı</div>
<p><b>Schibsted Grotesk</b>: kelime işareti, başlıklar, arayüz vurguları (SIL Open Font License). Kelime işaretinde Bold 700, harf aralığı −14/1000.
Başlıklarda Bold/SemiBold, metinde Regular/Medium.</p>
<div class="tsample" style="font-family:Schibsted;font-weight:500;font-size:22px;line-height:1.35;margin-top:10px">iPhone'dan gelen fotoğraf, Windows'ta tek dokunuşla açılır. Pano iki cihazda da aynı.</div>
</div><div>
<div class="tsample" style="font-family:Mono;font-weight:500;font-size:44px;letter-spacing:.06em;line-height:1.1">BY NITROBYTE<br>0123 · İĞŞ</div>
<p><b>JetBrains Mono</b>: imza satırı, teknik etiketler, sürüm ve kod (OFL). Medium 500, BÜYÜK HARF, harf aralığı +60/1000. Nitrobyte ana markasıyla ortak font.</p>
<p class="small">Arayüz metninde Windows'un sistem fontu (Segoe UI Variable) kullanılabilir. Marka yüzeylerinde (web, Store, sosyal) Schibsted Grotesk kullanılır. Kelime işareti her zaman vektör dosyadan alınır, yeniden dizilmez.</p>
</div></div>
<div class="scale"><div><span>Başlık 1 · Bold 40/44 · −2%</span><b style="font-size:40px;letter-spacing:-.02em">Paylaş, bırak, aç.</b></div>
<div><span>Başlık 2 · SemiBold 24/30</span><b style="font-size:24px;font-weight:600">Yakındaki cihazlar</b></div>
<div><span>Metin · Regular 15/22</span><b style="font-size:15px;font-weight:400">Fotoğraflar geldiği klasöre kaydedildi.</b></div>
<div><span>Etiket · JetBrains Mono 500 11 · +8%</span><b style="font-family:Mono;font-size:11px;font-weight:500;letter-spacing:.08em">IPHONE 15 · 3 DOSYA · 12,4 MB</b></div></div>
<div style="display:none"><div></div></div>""", kicker="06 · Yazı"))
    # 8 Made in Türkiye
    pages.append(page(8, "İmza: by Nitrobyte · Made in Türkiye", f"""
<div class="endo">{L(lockup_h(COL_LIGHT, C["graphite"], sub=C["smoke"]), "height:110px")}</div>
<div class="endo dk">{L(lockup_h(COL_DARK, C["paper"], sub=C["fog"]), "height:110px")}</div>
<div class="three"><div><h4>Yapı</h4><p>JetBrains Mono Medium, büyük harf. <b>BY NITROBYTE</b> + ince ayraç + <b>▍MADE IN TÜRKİYE</b>.
Satır kelime işaretinin sol kenarına hizalanır ve “N” yüksekliğinin %22'si boyutundadır. Türkçe metinlerde: <b>Türkiye'de yapıldı</b>.</p></div>
<div><h4>Kırmızı kuralı</h4><p>Türk kırmızısı <b>#E30A17</b> yalnızca “Türkiye” önündeki küçük dikey çubukta kullanılır. Bayrak, ay-yıldız ya da kırmızı bir işaret logoya <b>eklenmez</b>; bayrağın kullanımı yasal kurallara bağlıdır ve işaretin evrenselliğini bozar.</p></div>
<div><h4>Nerede</h4><p>Hakkında ekranı, açılış ekranı, web alt bilgisi, Store ve GitHub görselleri, basılı işler. Uygulama ikonu ve tepside <b>kullanılmaz</b>. İsteğe bağlı satır: <span class="mono">İSTANBUL · 41.01°N 28.98°E</span></p></div></div>
<div class="about"><span>NITRODROP 1.0 · NITROBYTE</span><span><i class="red"></i>TÜRKİYE'DE YAPILDI · İSTANBUL</span></div>""", kicker="07 · İmza"))
    # 9 yanlış kullanım
    pages.append(page(9, "Doğru ve yanlış kullanım", f"""<div class="ok">{img(good, "height:56px")}<p><b>✓</b> Yalnızca onaylı vektör dosyalar, onaylı renkler, güvenli alan bırakılarak.</p></div>
<div class="misg">{mis}</div>""", kicker="08 · Yasaklar"))
    # 10 ikon & tepsi
    ic_sizes = f'<figure>{img(app_icon(256), "width:150px;height:150px")}<figcaption>256 (küçültülmüş)</figcaption></figure>' + "".join(f'<figure>{img(app_icon(n), f"width:{n}px;height:{n}px")}<figcaption>{n}</figcaption></figure>' for n in (64, 48, 32, 24, 20, 16))
    tr_l = "".join(f'<figure>{img(tray(n, "#1C1C1C"), f"width:{n*3}px;height:{n*3}px;image-rendering:pixelated")}<figcaption>{n} ×3</figcaption></figure>' for n in (16, 20, 24, 32))
    tr_d = "".join(f'<figure>{img(tray(n, "#FFFFFF"), f"width:{n*3}px;height:{n*3}px;image-rendering:pixelated")}<figcaption>{n} ×3</figcaption></figure>' for n in (16, 20, 24, 32))
    pages.append(page(10, "Uygulama ikonu ve tepsi", f"""<div class="icons">{ic_sizes}</div>
<div class="two" style="margin-top:12px"><div><h4>Uygulama ikonu</h4><p>Grafit karo, hafif üst ışık, %10 beyaz iç kontur; gradyan blob ya da parıltı yok. Köşe ≈ boyun %20'si.
İşaret karonun %52'sinden (256–48 px) %72'sine (16 px) kadar büyür. <code>NitroDrop.ico</code>: 16–256, küçük boylar piksel ızgarasında.</p>
<h4 style="margin-top:10px">Tepsi (bildirim alanı)</h4><p>Tek renk XOR işaret: açık temada <code>tray-light.ico</code> (koyu), koyu temada <code>tray-dark.ico</code> (beyaz).
Uygulama <code>SystemUsesLightTheme</code> değerine göre geçiş yapar.</p></div>
<div><div class="trays"><div style="background:#F3F3F3">{tr_l}</div><div style="background:#202020">{tr_d}</div></div>
{taskbar(False)}{taskbar(True)}</div></div>""", kicker="09 · Windows"))
    # 11 mockups
    store_ic = app_icon(256)
    w_lock = L(lockup_h(COL_DARK, C["paper"]), "height:40px")
    pages.append(page(11, "Uygulamada", f"""<div class="mock3">
<div class="store"><div class="st-head">{img(store_ic, "width:92px;height:92px;border-radius:18px")}<div><h3>NitroDrop</h3><p>Nitrobyte</p>
<div class="stars">★★★★★ <span>Yardımcı programlar</span></div><button>Ücretsiz · Yükle</button></div></div>
<p class="st-desc">iPhone ve Mac ile dosya paylaşımı, ortak pano ve bildirimler, doğrudan Windows'ta.</p>
<div class="st-tiles"><div class="t150">{img(svg(150,150,f'<rect width="150" height="150" fill="{C["graphite"]}"/><g transform="{place(master(),150,150,69)}">{mark_color(master(), COL_TILE)}</g>'), "width:100%")}</div>
<div class="t310">{w_lock}</div></div><span class="cap">Microsoft Store sayfası ve kutucuklar</span></div>
<div class="gh"><img src="{png_uri(soc_png)}" style="width:100%;border-radius:10px;display:block"/><span class="cap">GitHub sosyal önizleme · 1280×640</span>
<div style="margin-top:16px">{taskbar(True)}</div><span class="cap">Windows 11 görev çubuğu · koyu tema</span></div></div>""", kicker="10 · Örnekler"))
    # 12 dosyalar
    pages.append(page(12, "Teslim dosyaları", f"""<div class="two"><div><table class="spec files">
<tr><td>Logo (SVG + PDF)</td><td>Ana (Renkli) · Ana (Tek Renk) · Siyah-Beyaz · İkon · Uygulama İkonu</td></tr>
<tr><td>ek/</td><td>Negatif, dikey ve imzalı düzenler, beyaz tek renk, negatif işaret (SVG)</td></tr>
<tr><td>app/NitroDrop.ico</td><td>16 · 20 · 24 · 32 · 40 · 48 · 64 · 256</td></tr>
<tr><td>app/tray-light.ico · tray-dark.ico</td><td>16 · 20 · 24 · 32 (tek renk)</td></tr>
<tr><td>app/png/</td><td>Uygulama ikonu 16–1024, tepsi PNG'leri</td></tr>
<tr><td>app/store/</td><td>MSIX: Square44 (scale + targetsize, unplated/lightunplated), Square150, Wide310, Square310, StoreLogo, SplashScreen · 100/125/150/200/400</td></tr>
<tr><td>app/</td><td>github-social-preview (1280×640), readme-banner açık/koyu (1280×320), SVG kaynakları</td></tr>
<tr><td>app/theme-tokens.md</td><td>WPF arayüzü için vurgu rengi önerisi (açık/koyu)</td></tr>
<tr><td>Fontlar</td><td>Schibsted Grotesk, JetBrains Mono (OFL) · <code>_build/fonts</code></td></tr></table></div>
<div class="endc">{L(lockup_v(COL_LIGHT, C["graphite"], sub=C["smoke"]), "height:230px")}<p class="small" style="text-align:center;margin-top:18px">nitrobyte.com.tr</p></div></div>""", kicker="11 · Teslim"))
    return "\n".join(pages)

CSS = f"""{FONTFACE}
@page{{size:{PW}px {PH}px;margin:0}}
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{background:#ddd}}
body{{font-family:Schibsted;color:#1D1F24;-webkit-print-color-adjust:exact;print-color-adjust:exact}}
.page{{width:{PW}px;height:{PH}px;background:#FAFAF9;position:relative;overflow:hidden;padding:54px 64px 60px;page-break-after:always;break-after:page}}
.kick{{font-family:Mono;font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:{C["signal_text"]}}}
h2{{font-size:38px;letter-spacing:-.02em;margin:6px 0 24px;font-weight:700}}
h4{{font-size:15px;margin-bottom:6px}} p{{font-size:13.5px;line-height:1.5;margin-bottom:10px;color:#2E3138}}
p.lead{{font-size:22px;line-height:1.35;color:#1D1F24;font-weight:500}} p.small{{font-size:11.5px;color:#5B606A}}
em{{font-style:normal;color:{C["signal_text"]}}} code{{font-family:Mono;font-size:11px;background:#0000000b;padding:1px 4px;border-radius:3px}}
.mono{{font-family:Mono;font-size:11px;letter-spacing:.06em}}
footer{{position:absolute;left:64px;right:64px;bottom:24px;display:flex;justify-content:space-between;font-family:Mono;font-size:10px;letter-spacing:.1em;color:#9A9EA6}}
.two{{display:grid;grid-template-columns:1fr 1fr;gap:40px;align-items:start}}
.three{{display:grid;grid-template-columns:repeat(3,1fr);gap:28px;margin-top:20px}}
.cover{{background:{C["ink"]};display:flex;align-items:center;justify-content:center;padding:0}}
.cv{{margin-top:-30px}} .cvf{{position:absolute;left:64px;right:64px;bottom:40px;display:flex;justify-content:space-between;font-family:Mono;font-size:12px;letter-spacing:.14em;color:#A0A5AE}}
.hero{{display:flex;justify-content:center;align-items:center;height:540px;background:#fff;border-radius:16px}}
.gridimg svg{{border-radius:8px;box-shadow:0 0 0 1px #0001;display:block}}
table.spec{{width:100%;border-collapse:collapse;margin:8px 0 16px;font-size:13px}} .spec td{{padding:8px 4px;border-top:1px solid #0000001a;vertical-align:top}} .spec td:first-child{{font-weight:700;width:34%}}
.files td:first-child{{font-family:Mono;font-size:11.5px;font-weight:500}}
.cs{{display:flex;justify-content:center;background:#fff;border-radius:14px;padding:30px}}
.lk4{{display:grid;grid-template-columns:1fr 1fr;gap:14px}} .cell{{background:#fff;border-radius:12px;height:150px;display:flex;align-items:center;justify-content:center;position:relative}}
.lk4 .cell:nth-child(3),.lk4 .cell:nth-child(4){{height:200px}}
.cell.dk{{background:{C["ink"]}}} .cell span{{position:absolute;left:14px;top:10px;font-family:Mono;font-size:10px;letter-spacing:.08em;color:#8A8F98;text-transform:uppercase}}
.sws{{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin-bottom:12px}}
.sw .chip{{height:74px;border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;justify-content:space-between;box-shadow:inset 0 0 0 1px #0000001a}}
.sw .chip b{{font-size:14px}} .sw .chip span{{font-family:Mono;font-size:9.5px;letter-spacing:.04em;opacity:.85}}
.sw dl{{display:grid;grid-template-columns:38px 1fr;font-family:Mono;font-size:10px;margin-top:5px;color:#4A4E57;row-gap:1px}} .sw dt{{color:#9A9EA6}}
table.ck{{width:100%;border-collapse:collapse;font-size:11.5px}} .ck td{{padding:3px 5px;border-top:1px solid #0000000f}} .ck td.r{{font-family:Mono;text-align:right}}
.ck i{{font-style:normal;font-weight:700;display:inline-block;width:34px;text-align:center;border-radius:4px;box-shadow:inset 0 0 0 1px #0000001a}}
.note{{background:#fff;border-radius:12px;padding:16px 18px}} .note p{{font-size:12px}}
.tsample{{margin-bottom:14px;color:#1D1F24}}
.scale{{display:grid;grid-template-columns:1.3fr 1fr 1fr 1fr;gap:18px;margin-top:26px;background:#fff;border-radius:12px;padding:20px 22px;align-items:end}}
.scale span{{display:block;font-family:Mono;font-size:9.5px;letter-spacing:.06em;color:#8A8F98;margin-bottom:8px}} .scale b{{display:block;color:#1D1F24;line-height:1.15}}
.endo{{background:#fff;border-radius:12px;height:170px;display:flex;align-items:center;justify-content:center;margin-bottom:12px}} .endo.dk{{background:{C["ink"]}}}
.about{{display:flex;justify-content:space-between;padding:12px 16px;border-radius:10px;background:#fff;font-family:Mono;font-size:11px;letter-spacing:.06em;color:#5B606A;margin-top:4px;box-shadow:inset 0 0 0 1px #0000000f}}
.red{{display:inline-block;width:4px;height:11px;background:{C["tr_red"]};border-radius:1px;margin-right:8px;vertical-align:-1px}}
.ok{{display:flex;align-items:center;gap:24px;background:#fff;border-radius:12px;padding:16px 22px;margin-bottom:14px}} .ok p{{margin:0}} .ok b{{color:#1a8f4a}}
.misg{{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}} .mis{{background:#fff;border-radius:12px;padding:12px}}
.mi{{height:118px;display:flex;align-items:center;justify-content:center;overflow:hidden;border-radius:8px;background:#F4F4F5}} .mi img{{max-width:88%;max-height:70px}}
.mis p{{font-size:11.5px;margin:8px 0 0}} .mis b{{color:{C["tr_red"]}}}
.icons{{display:flex;align-items:flex-end;gap:26px;background:#fff;border-radius:12px;padding:18px 22px}} figure{{text-align:center}} figcaption{{font-family:Mono;font-size:10px;color:#8A8F98;margin-top:6px}}
.trays{{display:grid;grid-template-columns:1fr 1fr;gap:10px}} .trays>div{{display:flex;gap:12px;align-items:flex-end;padding:12px;border-radius:10px}} .trays figcaption{{color:#888}}
.taskbar{{height:48px;border-radius:10px;display:flex;align-items:center;justify-content:center;gap:10px;position:relative;margin-top:10px}}
.tb-o{{width:24px;height:24px;border-radius:6px;display:inline-block}}
.tb-a{{position:relative;width:40px;height:40px;display:flex;align-items:center;justify-content:center;border-radius:6px;background:#ffffff14}}
.tb-a em{{position:absolute;bottom:2px;width:16px;height:3px;border-radius:2px}}
.tray{{position:absolute;right:14px;display:flex;align-items:center;gap:10px;font-family:"Segoe UI",sans-serif;font-size:10px;text-align:right;line-height:1.2}}
.mock3{{display:grid;grid-template-columns:1fr 1.15fr;gap:26px}}
.store{{background:#fff;border-radius:14px;padding:20px;box-shadow:inset 0 0 0 1px #0000000f}}
.st-head{{display:flex;gap:16px;align-items:center}} .st-head h3{{font-size:24px;letter-spacing:-.01em}} .st-head p{{margin:0;color:{C["signal_text"]};font-size:12px}}
.stars{{font-size:11px;color:#1D1F24;margin:4px 0 8px}} .stars span{{color:#8A8F98;margin-left:6px}}
.st-head button{{font-family:Schibsted;font-weight:600;font-size:12px;border:0;border-radius:6px;padding:7px 16px;background:{C["signal_text"]};color:#fff}}
.st-desc{{margin-top:14px}} .st-tiles{{display:grid;grid-template-columns:130px 1fr;gap:10px;margin-top:6px}}
.t150{{border-radius:6px;overflow:hidden;height:130px}} .t150 img{{display:block}} .t310{{background:{C["graphite"]};border-radius:6px;height:130px;display:flex;align-items:center;justify-content:center}}
.cap{{display:block;font-family:Mono;font-size:10px;letter-spacing:.08em;color:#8A8F98;margin-top:8px;text-transform:uppercase}}
.endc{{background:#fff;border-radius:14px;height:440px;display:flex;flex-direction:column;align-items:center;justify-content:center}}
@media screen{{.page{{margin:0}}}}
"""

def main():
    html = f'<!doctype html><html lang="tr"><head><meta charset="utf-8"><title>NitroDrop Marka Kimliği Kılavuzu</title><style>{CSS}</style></head><body>{build()}</body></html>'
    p = os.path.join(HERE, "guide.html"); write(p, html)
    print_pdf(p, OUT_PDF)
    # önizleme: tüm sayfaları tek ekran görüntüsünde al, sayfalara kes
    n = html.count('<section class="page')
    prev = os.path.join(HERE, "_guide_full.png"); screenshot(p, prev, PW, PH * n)
    from PIL import Image
    im = Image.open(prev); d = os.path.join(HERE, "preview"); os.makedirs(d, exist_ok=True)
    for i in range(n):
        im.crop((0, i * PH, PW, (i + 1) * PH)).save(os.path.join(d, f"sayfa-{i+1:02d}.png"))
    os.remove(prev)
    print("kılavuz ok:", n, "sayfa")

if __name__ == "__main__":
    main()
