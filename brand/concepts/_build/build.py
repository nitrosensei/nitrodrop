"""NitroDrop marka konseptleri — SVG + pano HTML üretir; render.ps1/ render() ile PNG'ye çevrilir.
Çalıştır:  python build.py   (Chrome headless ile PNG'leri de üretir)
"""
import os, subprocess, base64, json
from textpath import text_path, metrics

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)  # brand/concepts
FONTS = os.path.join(HERE, "fonts").replace("\\", "/")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
TR_RED = "#E30A17"
MONO = ("JetBrainsMono-VF.ttf", {"wght": 500})

# ---------------------------------------------------------------- renk/kontrast
def _lin(c):
    c = c / 255
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
def lum(h):
    h = h.lstrip("#"); r, g, b = (int(h[i:i+2], 16) for i in (0, 2, 4))
    return 0.2126*_lin(r) + 0.7152*_lin(g) + 0.0722*_lin(b)
def contrast(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True); return (la + 0.05) / (lb + 0.05)
def grade(r):
    return "AAA" if r >= 7 else "AA" if r >= 4.5 else "AA büyük" if r >= 3 else "yalnız grafik"

# ---------------------------------------------------------------- konseptler
# Her konsept: mark(colors) -> SVG iç içerik, 48 birimlik ızgarada.
# icon16 / icon32: piksel ızgarasına oturtulmuş elle çizilmiş küçük boy (hinting).

def arc_band(cx_o, r_o, cx_i, r_i, cy):
    """Üst yarım halka; dış ve iç daire merkezleri farklı → incelen bant. Uçlar cy'de yatay kesik."""
    return (f"M{cx_o-r_o} {cy} A{r_o} {r_o} 0 0 1 {cx_o+r_o} {cy} "
            f"L{cx_i+r_i} {cy} A{r_i} {r_i} 0 0 0 {cx_i-r_i} {cy} Z")

def rrect(x, y, w, h, r):
    return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}"/>'

# ---- A: Pas
def n_shape(c, L, R, cx, cy, ro, ci, ri, yi_join, gap_lines, uid):
    """Küçük harf n: sol gövde + omuz (incelen birleşim) + sağ ayak; sağ ayak eğik kesikle ayrılır.
    L=(x0,x1) sol gövde, R=(x0,x1) sağ ayak, alt=bottom. gap_lines = (y1@xa, y1@xb, y2@xa, y2@xb, xa, xb)"""
    (l0, l1), (r0, r1), bot = L, R, c["_bot"]
    outer = f"M{l0} {bot} L{l0} {cy} A{ro} {ro} 0 0 1 {r1} {cy} L{r1} {bot} Z"
    counter = f"M{l1} {bot} L{l1} {yi_join} A{ri} {ri} 0 0 1 {r0} {ci[1]} L{r0} {bot} Z"
    d = outer + " " + counter
    ya1, yb1, ya2, yb2, xa, xb = gap_lines
    k1 = (yb1 - ya1) / (xb - xa); k2 = (yb2 - ya2) / (xb - xa)
    mid = (l1 + r0) / 2; W = r1 + 10
    def y1(x): return ya1 + k1 * (x - xa)
    def y2(x): return ya2 + k2 * (x - xa)
    p1 = f"-10 -10 {W} -10 {W} {y1(W):.3f} {mid} {y1(mid):.3f} {mid} {bot+10} -10 {bot+10}"
    p2 = f"{mid} {y2(mid):.3f} {W} {y2(W):.3f} {W} {bot+10} {mid} {bot+10}"
    return (f'<defs><clipPath id="na{uid}"><polygon points="{p1}"/></clipPath><clipPath id="nb{uid}"><polygon points="{p2}"/></clipPath></defs>'
            f'<path fill="{c["arc"]}" fill-rule="evenodd" clip-path="url(#na{uid})" d="{d}"/>'
            f'<path fill="{c["bars"]}" fill-rule="evenodd" clip-path="url(#nb{uid})" d="{d}"/>')
def A_mark(c):
    c = dict(c, _bot=44)
    return n_shape(c, (6, 16), (32, 42), 24, 21, 18, (21, 19), 11, 9.2, (26, 22, 29.5, 25.5, 32, 42), _n())
def A_16(c):
    c = dict(c, _bot=14)
    return n_shape(c, (3, 6), (10, 13), 8, 7, 5, (7.5, 6.5), 2.5, 4.5, (8, 8, 9, 9, 10, 13), _n())
def A_32(c):
    c = dict(c, _bot=28)
    return n_shape(c, (6, 12), (20, 26), 16, 14, 10, (15, 13), 5, 9, (18, 16, 20, 18, 20, 26), _n())

# ---- B: Dosya Damlası
def drop_path(s, side, fold, gap, tip_r):
    """Kare (s..s+side); üç köşe tam yuvarlak (r=side/2) → 45° eğik damla. Sağ üst uç sivri kalır;
    bir kıvrım çizgisi (gap) ucu ayırır: ayrılan üçgen = sayfanın kıvrık köşesi."""
    r = side / 2; e = s + side; m = s + r; g = gap * 2 ** .5
    body = (f"M{m} {s} L{e-fold-g:.3f} {s} L{e} {s+fold+g:.3f} L{e} {m} "
            f"A{r} {r} 0 0 1 {m} {e} A{r} {r} 0 0 1 {s} {m} A{r} {r} 0 0 1 {m} {s} Z")
    corner = (f"M{e-fold} {s} L{e-tip_r} {s} Q{e} {s} {e} {s+tip_r} L{e} {s+fold} Z")
    return body, corner
def B_mark(c):
    b, f = drop_path(8, 32, 10.5, 1.7, 1.6)
    return f'<path fill="{c["body"]}" d="{b}"/><path fill="{c["flap"]}" d="{f}"/>'
def B_16(c):
    b, f = drop_path(3, 10, 4.2, .9, .5)
    return f'<path fill="{c["body"]}" d="{b}"/><path fill="{c["flap"]}" d="{f}"/>'
def B_32(c):
    b, f = drop_path(6, 20, 6.8, 1.2, 1)
    return f'<path fill="{c["body"]}" d="{b}"/><path fill="{c["flap"]}" d="{f}"/>'

# ---- C: Kesişim
def C_generic(c, ph, sc, uid):
    (px, py, pw, phh, pr), (sx, sy, sw, sh, sr) = ph, sc
    cid = f"clp{uid}"
    return (f'<defs><clipPath id="{cid}">{rrect(px,py,pw,phh,pr)}</clipPath></defs>'
            f'<g fill="{c["screen"]}">{rrect(sx,sy,sw,sh,sr)}</g>'
            f'<g fill="{c["phone"]}">{rrect(px,py,pw,phh,pr)}</g>'
            f'<g fill="{c["share"]}" clip-path="url(#{cid})">{rrect(sx,sy,sw,sh,sr)}</g>')
_uid = [0]
def _n():
    _uid[0] += 1; return _uid[0]
def C_mark(c): return C_generic(c, (6, 4, 18, 32, 5), (14, 20, 30, 22, 4.5), _n())
def C_16(c):   return C_generic(c, (2, 2, 6, 10, 1.8), (5, 7, 9, 7, 1.5), _n())
def C_32(c):   return C_generic(c, (5, 4, 11, 20, 3.4), (10, 13, 19, 15, 3), _n())

CONCEPTS = {
 "a": dict(
    name="Pas", tag="Konsept A",
    idea="Küçük harf “n”: omuz sol gövdeden incecik çıkar, kemer karşıya uzanır; sağ ayak eğik bir hız kesiğiyle ayrılıp altına döner — dosya karşı cihaza pas edildi.",
    font=("Archivo-VF.ttf", {"wght": 700, "wdth": 112}), font_label="Archivo SemiExpanded Bold (OFL) — Nitrobyte'ın ana yazı ailesi",
    track=-6, mark=A_mark, i16=A_16, i32=A_32,
    light=dict(arc="#17130E", bars="#8F5D00", word="#17130E", sub="#6E665B", bg="#FBF8F3"),
    dark=dict(arc="#F5EEE2", bars="#E8A93A", word="#F5EEE2", sub="#A79E91", bg="#0F0D0A"),
    tile=dict(bg="#17130E", hi="#2A241B", mark=dict(arc="#F5EEE2", bars="#E8A93A")),
    palette=[("Gece", "#17130E"), ("Altın", "#E8A93A"), ("Koyu Altın", "#8F5D00"), ("Krem", "#F5EEE2"),
             ("Taş", "#6E665B"), ("Kum", "#A79E91")],
    checks=[("Gece metin / Krem zemin", "#17130E", "#FBF8F3"), ("Koyu Altın metin / açık zemin", "#8F5D00", "#FBF8F3"),
            ("Taş ikincil / açık zemin", "#6E665B", "#FBF8F3"), ("Krem metin / Gece zemin", "#F5EEE2", "#0F0D0A"),
            ("Altın metin / koyu zemin", "#E8A93A", "#0F0D0A"), ("Kum ikincil / koyu zemin", "#A79E91", "#0F0D0A")],
 ),
 "b": dict(
    name="Dosya Damlası", tag="Konsept B",
    idea="Ucu sağ üste dönük bir damla; sivri ucu bir kıvrım çizgisiyle ayrılıp altın renge döner — sayfanın kıvrık köşesi. Aynı anda hem damla (drop) hem dosya.",
    font=("InstrumentSans-VF.ttf", {"wght": 600, "wdth": 100}), font_label="Instrument Sans SemiBold (OFL)",
    track=-12, mark=B_mark, i16=B_16, i32=B_32,
    light=dict(body="#0E6B52", flap="#C99A2E", word="#0F1A16", sub="#5E6B66", bg="#F8F5EE"),
    dark=dict(body="#4CC79A", flap="#E0B04F", word="#F3EFE6", sub="#9DABA5", bg="#0B1310"),
    tile=dict(bg="#0C5A45", hi="#11735A", mark=dict(body="#F6EFE2", flap="#E0B04F")),
    palette=[("Yeşim", "#0E6B52"), ("Derin Yeşim", "#0C5A45"), ("Taze Yeşim", "#4CC79A"), ("Kâğıt", "#F6EFE2"),
             ("Kulak Altını", "#E0B04F"), ("Mürekkep", "#0F1A16")],
    checks=[("Mürekkep metin / açık zemin", "#0F1A16", "#F8F5EE"), ("Yeşim metin / açık zemin", "#0E6B52", "#F8F5EE"),
            ("İkincil / açık zemin", "#5E6B66", "#F8F5EE"), ("Kâğıt metin / koyu zemin", "#F3EFE6", "#0B1310"),
            ("Taze Yeşim / koyu zemin", "#4CC79A", "#0B1310"), ("Kâğıt / Derin Yeşim karo", "#F6EFE2", "#0C5A45")],
 ),
 "c": dict(
    name="Kesişim", tag="Konsept C",
    idea="Dikey telefon ile yatay ekran köşeden üst üste biner; kesiştikleri alan ortak alan — dosya, pano, bildirim ve sekmeler orada buluşur.",
    font=("SchibstedGrotesk-VF.ttf", {"wght": 700}), font_label="Schibsted Grotesk Bold (OFL)",
    track=-14, mark=C_mark, i16=C_16, i32=C_32,
    light=dict(phone="#1D1F24", screen="#9EA3AC", share="#F0532A", word="#1D1F24", sub="#5B606A", bg="#F4F4F5"),
    dark=dict(phone="#EEEFF1", screen="#4A4E57", share="#FF6A3D", word="#EEEFF1", sub="#A0A5AE", bg="#111216"),
    tile=dict(bg="#1D1F24", hi="#2B2E35", mark=dict(phone="#EEEFF1", screen="#5A5F69", share="#FF6A3D")),
    palette=[("Grafit", "#1D1F24"), ("Sinyal", "#FF6A3D"), ("Koyu Sinyal", "#C2410C"), ("Çelik", "#B9BDC4"),
             ("Kâğıt", "#EEEFF1"), ("Duman", "#5B606A")],
    checks=[("Grafit metin / açık zemin", "#1D1F24", "#F4F4F5"), ("Koyu Sinyal metin / açık zemin", "#C2410C", "#F4F4F5"),
            ("Duman ikincil / açık zemin", "#5B606A", "#F4F4F5"), ("Kâğıt metin / koyu zemin", "#EEEFF1", "#111216"),
            ("Sinyal metin / koyu zemin", "#FF6A3D", "#111216"), ("Sinyal / açık zemin (grafik)", "#F0532A", "#F4F4F5")],
 ),
}

# ---------------------------------------------------------------- SVG yardımcıları
def svg(vb_w, vb_h, inner, w=None, h=None, extra=""):
    ws = f' width="{w}" height="{h}"' if w else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {vb_w} {vb_h}"{ws}{extra}>{inner}</svg>'

def tile(cfg, size_vb, r, inner_mark, pad_hi=True):
    """Windows 11 tarzı karo: düz renk + çok hafif üst ışık + 1px iç kontur. Gradyan blob yok."""
    t = cfg["tile"]; u = _n()
    return (f'<defs><linearGradient id="g{u}" x1="0" y1="0" x2="0" y2="1">'
            f'<stop offset="0" stop-color="{t["hi"]}"/><stop offset=".55" stop-color="{t["bg"]}"/></linearGradient></defs>'
            f'<rect width="{size_vb}" height="{size_vb}" rx="{r}" fill="url(#g{u})"/>'
            f'<rect x=".5" y=".5" width="{size_vb-1}" height="{size_vb-1}" rx="{r-.5}" fill="none" stroke="#fff" stroke-opacity=".10"/>'
            + inner_mark)

def icon_master(cfg, key):   # 48 ızgara → 256 / 48 için; optik boy: işaretin uzun kenarı ≈ 24 birim
    m = cfg["mark"](cfg["tile"]["mark"])
    x0, y0, x1, y1 = MARK_BOX[key]; k = {"a": 24.5, "b": 25, "c": 25}[key] / max(x1 - x0, y1 - y0)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return svg(48, 48, tile(cfg, 48, 10.5, f'<g transform="translate(24 24) scale({k:.4f}) translate({-cx} {-cy})">{m}</g>'))
def icon_32(cfg):
    return svg(32, 32, tile(cfg, 32, 7, cfg["i32"](cfg["tile"]["mark"])))
def icon_16(cfg):
    return svg(16, 16, tile(cfg, 16, 3.5, cfg["i16"](cfg["tile"]["mark"])))

MARK_BOX = {"a": (6, 3, 42, 44), "b": (8, 8, 40, 40), "c": (6, 4, 44, 42)}

def lockup_h(key, cfg, mode, endorse=True):
    """Yatay: işaret + kelime işareti (+ by Nitrobyte · Made in Türkiye)."""
    col = cfg[mode]; fname, var = cfg["font"]
    size = 100; met = metrics(fname, var, size); cap = met["cap"]
    x0, y0, x1, y1 = MARK_BOX[key]; mh = y1 - y0
    mark_h = cap * 1.62            # işaret yüksekliği ≈ 1.62 × büyük harf
    k = mark_h / mh
    base = 160  # işaretin dikey merkezi = büyük harf ortası
    mark_top = base - cap / 2 - mark_h / 2
    mark_w = (x1 - x0) * k
    gap = cap * 0.62
    tx = 20 + mark_w + gap
    d, w = text_path("NitroDrop", fname, var, size, tx, base, tracking=cfg["track"])
    inner = (f'<g transform="translate({20 - x0*k:.2f} {mark_top - y0*k:.2f}) scale({k:.4f})">{cfg["mark"](col)}</g>'
             f'<path fill="{col["word"]}" d="{d}"/>')
    W = tx + w + 20; H = base + (78 if endorse else 40)
    if endorse:
        inner += endorsement(tx, base + 54, col, size=24)
    return svg(round(W), round(H), inner)

def endorsement(x, y, col, size=17, center=False, total_w=None):
    """“by Nitrobyte · Made in Türkiye” — JetBrains Mono; Türkiye öncesinde küçük kırmızı çubuk (#E30A17)."""
    f, v = MONO
    d1, w1 = text_path("BY NITROBYTE", f, v, size, 0, 0, tracking=60)
    d2, w2 = text_path("MADE IN TÜRKIYE", f, v, size, 0, 0, tracking=60)
    sep = size * 1.6; bar = size * 0.9
    tw = w1 + sep + bar + size * 0.55 + w2
    if center: x = x - tw / 2
    capm = size * 0.73
    # sırayla: BY NITROBYTE  |  ▍ MADE IN TÜRKIYE
    o = (f'<path fill="{col["sub"]}" transform="translate({x:.2f} {y:.2f})" d="{d1}"/>'
         f'<rect x="{x + w1 + sep/2 - size*.06:.2f}" y="{y - capm:.2f}" width="{size*.12:.2f}" height="{capm:.2f}" fill="{col["sub"]}" opacity=".45"/>'
         f'<rect x="{x + w1 + sep:.2f}" y="{y - capm:.2f}" width="{bar*.34:.2f}" height="{capm:.2f}" rx="{size*.04:.2f}" fill="{TR_RED}"/>'
         f'<path fill="{col["sub"]}" transform="translate({x + w1 + sep + bar*.34 + size*.55:.2f} {y:.2f})" d="{d2}"/>')
    return o

def lockup_v(key, cfg, mode):
    col = cfg[mode]; fname, var = cfg["font"]
    size = 100; cap = metrics(fname, var, size)["cap"]
    d0, w = text_path("NitroDrop", fname, var, size, 0, 0, tracking=cfg["track"])
    W = max(w, 420) + 80
    x0, y0, x1, y1 = MARK_BOX[key]; mark_h = cap * 2.6; k = mark_h / (y1 - y0); mw = (x1 - x0) * k
    top = 40
    inner = f'<g transform="translate({W/2 - mw/2 - x0*k:.2f} {top - y0*k:.2f}) scale({k:.4f})">{cfg["mark"](col)}</g>'
    base = top + mark_h + cap * 0.75 + cap
    d, _ = text_path("NitroDrop", fname, var, size, W/2 - w/2, base, tracking=cfg["track"])
    inner += f'<path fill="{col["word"]}" d="{d}"/>'
    inner += endorsement(W/2, base + 62, col, size=21, center=True)
    return svg(round(W), round(base + 80), inner)

def mark_only(key, cfg, mode, pad=4):
    x0, y0, x1, y1 = MARK_BOX[key]
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{x0-pad} {y0-pad} {x1-x0+2*pad} {y1-y0+2*pad}">'
            f'{cfg["mark"](cfg[mode])}</svg>')

def construction(key, cfg):
    """48 ızgarada yapı çizimi: ızgara + anahtar geometri."""
    g = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49, 2))
    g4 = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49, 8))
    col = dict(cfg["light"]); guides = ""
    S = 'fill="none" stroke="#E30A17" stroke-width=".18"'
    if key == "a":
        guides = (f'<circle cx="24" cy="21" r="18" {S}/><circle cx="21" cy="19" r="11" {S} stroke-dasharray=".6 .5"/>'
                  f'<line x1="22" y1="30.8" x2="48" y2="20.4" {S}/><line x1="22" y1="34.3" x2="48" y2="23.9" {S}/>'
                  f'<circle cx="24" cy="21" r=".5" fill="#E30A17"/><circle cx="21" cy="19" r=".5" fill="#E30A17"/>')
    elif key == "b":
        guides = (f'<circle cx="24" cy="24" r="16" {S}/><rect x="8" y="8" width="32" height="32" {S}/>'
                  f'<line x1="26.5" y1="2" x2="46" y2="21.5" {S} stroke-dasharray=".6 .5"/><line x1="4" y1="44" x2="44" y2="4" {S} stroke-dasharray=".6 .5"/>')
    else:
        guides = (f'<rect x="6" y="4" width="18" height="32" rx="5" {S}/><rect x="14" y="20" width="30" height="22" rx="4.5" {S}/>'
                  f'<line x1="14" y1="0" x2="14" y2="48" {S} stroke-dasharray=".6 .5"/><line x1="24" y1="0" x2="24" y2="48" {S} stroke-dasharray=".6 .5"/>'
                  f'<line x1="0" y1="20" x2="48" y2="20" {S} stroke-dasharray=".6 .5"/><line x1="0" y1="36" x2="48" y2="36" {S} stroke-dasharray=".6 .5"/>')
    inner = (f'<rect width="48" height="48" fill="#fff"/>'
             f'<g stroke="#000" stroke-opacity=".06" stroke-width=".1">{g}</g>'
             f'<g stroke="#000" stroke-opacity=".14" stroke-width=".14">{g4}</g>'
             f'<g opacity=".92">{cfg["mark"](col)}</g>{guides}')
    return svg(48, 48, inner)

# ---------------------------------------------------------------- pano
FONTFACE = f"""
@font-face{{font-family:Mono;src:url('file:///{FONTS}/JetBrainsMono-VF.ttf');font-weight:100 900}}
@font-face{{font-family:Archivo;src:url('file:///{FONTS}/Archivo-VF.ttf');font-weight:100 900;font-stretch:62% 125%}}
@font-face{{font-family:Instrument;src:url('file:///{FONTS}/InstrumentSans-VF.ttf');font-weight:100 900}}
@font-face{{font-family:Schibsted;src:url('file:///{FONTS}/SchibstedGrotesk-VF.ttf');font-weight:100 900}}
"""
CSS_FAMILY = {"a": "Archivo", "b": "Instrument", "c": "Schibsted"}

def data_uri(s): return "data:image/svg+xml;base64," + base64.b64encode(s.encode()).decode()

PIXEL_JS = """
<script>
document.querySelectorAll('canvas[data-src]').forEach(cv=>{
  const n=+cv.dataset.n, img=new Image();
  img.onload=()=>{const x=cv.getContext('2d');x.clearRect(0,0,n,n);x.drawImage(img,0,0,n,n)};
  img.src=cv.dataset.src;
});
</script>"""

def board_html(key, cfg, files):
    fam = CSS_FAMILY[key]; L, D = cfg["light"], cfg["dark"]
    i256 = data_uri(files["icon"]); i32 = data_uri(files["icon32"]); i16 = data_uri(files["icon16"])
    sw = "".join(f'<div class="sw"><div class="chip" style="background:{h}"></div><b>{n}</b><span>{h}</span></div>' for n, h in cfg["palette"])
    cks = "".join(f'<tr><td><i style="background:{bg};color:{fg}">Aa</i></td><td>{lbl}</td><td class="r">{contrast(fg,bg):.1f}:1</td><td>{grade(contrast(fg,bg))}</td></tr>'
                  for lbl, fg, bg in cfg["checks"])
    def taskbar(dark):
        bg = "#202020" if dark else "#EEF0F3"; icon = i16 if False else data_uri(files["icon"])
        others = "".join(f'<span class="tb-o" style="background:{"#3a3a3a" if dark else "#d5d8dd"}"></span>' for _ in range(3))
        return (f'<div class="taskbar" style="background:{bg}">{others}<span class="tb-a"><img src="{icon}" width="24" height="24"/>'
                f'<em style="background:{"#9a9a9a" if dark else "#8a8f98"}"></em></span>{others[:len(others)//3]}'
                f'<span class="tray"><img src="{i16}" width="16" height="16"/><small style="color:{"#ddd" if dark else "#333"}">01:35</small></span></div>')
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>{FONTFACE}
*{{box-sizing:border-box;margin:0}}
body{{width:1800px;height:1270px;background:#ECEAE6;font-family:{fam};color:#1a1a1a;padding:44px 52px}}
.mono{{font-family:Mono;font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#77736c}}
header{{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:1px solid #00000018;padding-bottom:18px}}
h1{{font-size:44px;font-weight:700;letter-spacing:-.02em}}
h1 small{{font-family:Mono;font-size:13px;font-weight:500;letter-spacing:.1em;color:#77736c;display:block;margin-bottom:6px;text-transform:uppercase}}
.idea{{max-width:760px;font-size:19px;line-height:1.4;color:#33302b}}
.grid{{display:grid;grid-template-columns:420px 1fr 1fr;grid-template-rows:auto auto;gap:22px;margin-top:24px}}
.card{{background:#fff;border-radius:14px;padding:20px;position:relative}}
.card.dark{{background:{D["bg"]}}}
.card>.mono:first-child{{position:absolute;top:14px;left:18px}}
.card.dark>.mono{{color:#ffffff70}}
.cons img{{width:300px;display:block;margin:0 auto;border-radius:6px}}
.lk{{display:flex;align-items:center;justify-content:center;height:190px}}
.lk img{{max-width:88%;max-height:150px}}
.lkv{{display:flex;align-items:center;justify-content:center;height:330px}} .lkv img{{max-height:270px}}
.icons{{display:flex;align-items:flex-end;gap:28px;padding-top:30px}}
.icons figure{{text-align:center}} figcaption{{font-family:Mono;font-size:11px;color:#77736c;margin-top:8px}}
canvas{{image-rendering:pixelated;display:block;border-radius:2px}}
.sws{{display:grid;grid-template-columns:repeat(6,1fr);gap:10px;padding-top:26px}}
.sw .chip{{height:64px;border-radius:9px;box-shadow:inset 0 0 0 1px #0000001a}}
.sw b{{display:block;font-size:13px;margin-top:7px}} .sw span{{font-family:Mono;font-size:11px;color:#77736c}}
table{{width:100%;border-collapse:collapse;margin-top:12px;font-size:13px}}
td{{padding:4px 6px;border-top:1px solid #0000000d}} td.r{{font-family:Mono;text-align:right}}
td i{{font-style:normal;font-weight:700;display:inline-block;width:38px;text-align:center;border-radius:5px;padding:2px 0;box-shadow:inset 0 0 0 1px #0000001a}}
.taskbar{{height:48px;border-radius:10px;display:flex;align-items:center;justify-content:center;gap:10px;position:relative;margin-top:10px}}
.tb-o{{width:24px;height:24px;border-radius:6px;display:inline-block;opacity:.8}}
.tb-a{{position:relative;width:40px;height:40px;display:flex;align-items:center;justify-content:center;border-radius:6px;background:#ffffff14}}
.tb-a em{{position:absolute;bottom:2px;width:16px;height:3px;border-radius:2px}}
.tray{{position:absolute;right:14px;display:flex;align-items:center;gap:10px;font-family:Segoe UI, sans-serif}}
.about{{white-space:nowrap;display:flex;align-items:center;justify-content:space-between;padding:14px 18px;border-radius:10px;margin-top:12px;font-family:Mono;font-size:10.5px;letter-spacing:.04em;text-transform:uppercase}}
.about .red{{display:inline-block;width:4px;height:11px;background:{TR_RED};border-radius:1px;margin-right:8px;vertical-align:-1px}}
</style></head><body>
<header><div><h1><small>{cfg["tag"]} · NitroDrop marka konsepti</small>{cfg["name"]}</h1></div>
<p class="idea">{cfg["idea"]}</p></header>
<div class="grid">
  <div class="card cons" style="grid-row:span 2"><div class="mono">Yapı · 48 birim ızgara</div>
    <img style="margin-top:22px" src="{data_uri(files["construction"])}"/>
    <div class="mono" style="position:static;margin-top:18px">Yazı tipi</div>
    <p style="font-size:14px;margin-top:4px">{cfg["font_label"]}<br><span class="mono" style="font-size:10.5px">Kelime işareti yola çevrildi · imza satırı JetBrains Mono</span></p>
    <div style="font-size:64px;font-weight:{cfg["font"][1]["wght"]};font-stretch:{cfg["font"][1].get("wdth",100)}%;letter-spacing:-.02em;margin-top:6px;line-height:1">Aa Ğğ Şş İı</div>
    <div class="mono" style="position:static;margin-top:22px">Uygulama hakkında alt bilgisi</div>
    <div class="about" style="background:{L["bg"]};color:{L["sub"]};box-shadow:inset 0 0 0 1px #0000000f"><span>NitroDrop 1.0 · Nitrobyte</span><span><i class="red"></i>Türkiye'de yapıldı</span></div>
    <div class="about" style="background:{D["bg"]};color:{D["sub"]}"><span>nitrobyte.com.tr</span><span><i class="red"></i>Made in Türkiye · İstanbul</span></div>
  </div>
  <div class="card"><div class="mono">Yatay · açık</div><div class="lk"><img src="{data_uri(files["h_light"])}"/></div></div>
  <div class="card dark"><div class="mono">Yatay · koyu</div><div class="lk"><img src="{data_uri(files["h_dark"])}"/></div></div>
  <div class="card"><div class="mono">Dikey · açık</div><div class="lkv"><img src="{data_uri(files["v_light"])}"/></div></div>
  <div class="card dark"><div class="mono">Dikey · koyu</div><div class="lkv"><img src="{data_uri(files["v_dark"])}"/></div></div>
</div>
<div class="grid" style="grid-template-columns:1.25fr 1fr 1fr;margin-top:22px">
  <div class="card"><div class="mono">Uygulama ikonu · gerçek boyut + 16/32 piksel büyütme</div>
    <div class="icons">
      <figure><img src="{i256}" width="256" height="256"/><figcaption>256</figcaption></figure>
      <figure><img src="{i256}" width="48" height="48"/><figcaption>48</figcaption></figure>
      <figure><img src="{i32}" width="32" height="32"/><figcaption>32</figcaption></figure>
      <figure><img src="{i16}" width="16" height="16"/><figcaption>16</figcaption></figure>
      <figure><canvas data-n="32" data-src="{i32}" width="32" height="32" style="width:128px;height:128px"></canvas><figcaption>32 ×4</figcaption></figure>
      <figure><canvas data-n="16" data-src="{i16}" width="16" height="16" style="width:128px;height:128px"></canvas><figcaption>16 ×8</figcaption></figure>
    </div></div>
  <div class="card"><div class="mono">Renk paleti</div><div class="sws">{sw}</div>
    <div class="mono" style="position:static;margin-top:20px">Görev çubuğu · tepsi (24 / 16 px)</div>{taskbar(False)}{taskbar(True)}</div>
  <div class="card"><div class="mono">WCAG kontrast · metin kullanımları</div><table style="margin-top:22px">{cks}</table></div>
</div>
{PIXEL_JS}</body></html>"""

def overview_html(all_files):
    cols = ""
    for key, cfg in CONCEPTS.items():
        f = all_files[key]; fam = CSS_FAMILY[key]
        chips = "".join(f'<span style="background:{h}"></span>' for _, h in cfg["palette"])
        cols += f"""<section>
 <div class="mono">{cfg["tag"]}</div><h2 style="font-family:{fam}">{cfg["name"]}</h2><p>{cfg["idea"]}</p>
 <div class="hero" style="background:{cfg["light"]["bg"]}"><img src="{data_uri(f["icon"])}" width="200" height="200"/></div>
 <div class="row"><img src="{data_uri(f["icon"])}" width="48"/><img src="{data_uri(f["icon32"])}" width="32"/><img src="{data_uri(f["icon16"])}" width="16"/>
   <canvas data-n="16" data-src="{data_uri(f["icon16"])}" width="16" height="16" style="width:64px;height:64px"></canvas></div>
 <div class="lk" style="background:{cfg["light"]["bg"]}"><img src="{data_uri(f["h_light"])}"/></div>
 <div class="lk" style="background:{cfg["dark"]["bg"]}"><img src="{data_uri(f["h_dark"])}"/></div>
 <div class="chips">{chips}</div></section>"""
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>{FONTFACE}
*{{box-sizing:border-box;margin:0}} body{{width:1800px;height:1080px;background:#ECEAE6;font-family:Archivo;padding:44px 52px;color:#1a1a1a}}
.mono{{font-family:Mono;font-size:12px;letter-spacing:.1em;text-transform:uppercase;color:#77736c}}
header{{display:flex;justify-content:space-between;align-items:flex-end;padding-bottom:16px;border-bottom:1px solid #00000018}}
header h1{{font-size:40px;letter-spacing:-.02em;font-stretch:110%}}
main{{display:grid;grid-template-columns:repeat(3,1fr);gap:26px;margin-top:24px}}
section{{background:#fff;border-radius:16px;padding:24px}}
h2{{font-size:32px;margin:4px 0 6px;letter-spacing:-.02em}} p{{font-size:15px;line-height:1.4;color:#45413b;height:64px}}
.hero{{height:300px;border-radius:12px;display:flex;align-items:center;justify-content:center;margin-top:14px}}
.row{{display:flex;align-items:center;gap:22px;padding:16px 4px}} canvas{{image-rendering:pixelated}}
.lk{{height:116px;border-radius:12px;display:flex;align-items:center;justify-content:center;margin-top:10px}} .lk img{{max-width:86%;max-height:84px}}
.chips{{display:flex;margin-top:14px;border-radius:8px;overflow:hidden;height:30px}} .chips span{{flex:1}}
</style></head><body>
<header><div><div class="mono">NitroDrop · marka kimliği · Faz 1</div><h1>Üç konsept</h1></div>
<div class="mono" style="text-align:right">by Nitrobyte · <span style="display:inline-block;width:4px;height:11px;background:{TR_RED};vertical-align:-1px;margin:0 6px 0 2px"></span>Made in Türkiye<br>2026-10-28</div></header>
<main>{cols}</main>{PIXEL_JS}</body></html>"""

def render(html_path, png_path, w=1800, h=1240):
    url = "file:///" + html_path.replace("\\", "/")
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                    "--allow-file-access-from-files", "--virtual-time-budget=4000",
                    f"--window-size={w},{h}", f"--screenshot={png_path}", url],
                   check=True, capture_output=True, timeout=120)

def main():
    all_files = {}
    for key, cfg in CONCEPTS.items():
        out = os.path.join(ROOT, f"concept-{key}"); os.makedirs(out, exist_ok=True)
        files = {
            "mark_light": mark_only(key, cfg, "light"), "mark_dark": mark_only(key, cfg, "dark"),
            "h_light": lockup_h(key, cfg, "light"), "h_dark": lockup_h(key, cfg, "dark"),
            "v_light": lockup_v(key, cfg, "light"), "v_dark": lockup_v(key, cfg, "dark"),
            "icon": icon_master(cfg, key), "icon32": icon_32(cfg), "icon16": icon_16(cfg),
            "construction": construction(key, cfg),
        }
        names = {"mark_light": "isaret-acik", "mark_dark": "isaret-koyu", "h_light": "yatay-acik", "h_dark": "yatay-koyu",
                 "v_light": "dikey-acik", "v_dark": "dikey-koyu", "icon": "ikon-256", "icon32": "ikon-32",
                 "icon16": "ikon-16", "construction": "yapi"}
        for k, s in files.items():
            with open(os.path.join(out, names[k] + ".svg"), "w", encoding="utf-8") as fh: fh.write(s)
        html = os.path.join(HERE, f"board-{key}.html")
        with open(html, "w", encoding="utf-8") as fh: fh.write(board_html(key, cfg, files))
        render(html, os.path.join(out, "board.png"), 1800, 1270)
        all_files[key] = files
        print("ok", key)
    html = os.path.join(HERE, "overview.html")
    with open(html, "w", encoding="utf-8") as fh: fh.write(overview_html(all_files))
    render(html, os.path.join(ROOT, "overview.png"), 1800, 1080)
    print("ok overview")
    for key, cfg in CONCEPTS.items():
        for lbl, fg, bg in cfg["checks"]:
            print(key, f"{contrast(fg,bg):5.2f}", grade(contrast(fg, bg)), lbl)

if __name__ == "__main__":
    main()
