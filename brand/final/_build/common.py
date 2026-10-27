"""NitroDrop — ortak marka geometrisi, renkler, SVG yardımcıları ve Chrome headless render."""
import os, subprocess, base64, tempfile
from textpath import text_path, metrics

HERE = os.path.dirname(os.path.abspath(__file__))
FINAL = os.path.dirname(HERE)
BRAND = os.path.dirname(FINAL)
FONTS = os.path.join(HERE, "fonts").replace("\\", "/")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

# ------------------------------------------------------------------ renkler
C = dict(
    graphite="#1D1F24", ink="#111216", paper="#EEEFF1", steel="#9EA3AC", steel_dark="#5A5F69",
    signal="#FF6A3D", signal_light="#F0532A", signal_text="#C2410C", signal_text_dark="#FF7A50",
    smoke="#5B606A", fog="#A0A5AE", bg_light="#F4F4F5", tr_red="#E30A17", white="#FFFFFF", black="#000000",
)
WORD_FONT = ("SchibstedGrotesk-VF.ttf", {"wght": 700})
WORD_TRACK = -14
MONO = ("JetBrainsMono-VF.ttf", {"wght": 500})

def _lin(c):
    c /= 255; return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
def rgb(h): h = h.lstrip("#"); return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))
def lum(h): r, g, b = rgb(h); return 0.2126*_lin(r) + 0.7152*_lin(g) + 0.0722*_lin(b)
def contrast(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True); return (la + .05) / (lb + .05)
def grade(r): return "AAA" if r >= 7 else "AA" if r >= 4.5 else "AA büyük" if r >= 3 else "yalnız grafik"
def cmyk(h):
    r, g, b = (v / 255 for v in rgb(h)); k = 1 - max(r, g, b)
    if k >= 1: return (0, 0, 0, 100)
    return tuple(round(100 * v) for v in ((1-r-k)/(1-k), (1-g-k)/(1-k), (1-b-k)/(1-k), k))

# ------------------------------------------------------------------ geometri
def rr(x, y, w, h, r):
    """Yuvarlak köşeli dikdörtgen yolu (saat yönü)."""
    r = min(r, w / 2, h / 2); X, Y = x + w, y + h
    f = lambda v: f"{v:.3f}".rstrip("0").rstrip(".")
    return (f"M{f(x+r)} {f(y)}H{f(X-r)}A{f(r)} {f(r)} 0 0 1 {f(X)} {f(y+r)}V{f(Y-r)}"
            f"A{f(r)} {f(r)} 0 0 1 {f(X-r)} {f(Y)}H{f(x+r)}A{f(r)} {f(r)} 0 0 1 {f(x)} {f(Y-r)}"
            f"V{f(y+r)}A{f(r)} {f(r)} 0 0 1 {f(x+r)} {f(y)}Z")

# Varyantlar. phone/screen = (x,y,w,h,r); island = (x,y,w,h) (telefonun üstünde delik); base = laptop tabanı
SPECS = {
  # Faz 1 hali (önce)
  "before": {48: dict(phone=(6, 4, 18, 32, 5), screen=(14, 20, 30, 22, 4.5))},
  # R1: gerçek cihaz oranları (telefon ~2.1:1 büyük köşe, ekran 16:10 dar köşe) + ada (dynamic island) kesiği
  "r1": {48: dict(phone=(7, 3, 16, 34, 6), island=(12.5, 6, 5, 2), screen=(13, 19, 32, 20, 2.5))},
  # R2: R1 + dizüstü tabanı
  "r2": {48: dict(phone=(6, 3, 16, 34, 6), island=(11.5, 6, 5, 2), screen=(12, 19, 30, 19, 2.5),
                  base=(9, 39.5, 36, 2.2, 1.1))},
}
# Piksel ızgarasına oturtulmuş küçük boylar (karo içi koordinatlar, karo = size×size)
PIXEL = {
  "r1": {
    32: dict(phone=(6, 7, 8, 17, 3), island=(8.5, 9, 3, 1.2), screen=(10, 15, 16, 10, 1.5)),
    24: dict(phone=(4, 5, 6, 13, 2.2), island=(6, 7, 2, 1), screen=(7, 11, 13, 8, 1.2)),
    20: dict(phone=(3, 4, 5, 11, 1.8), screen=(6, 9, 11, 7, 1)),
    16: dict(phone=(2, 2, 5, 10, 1.5), screen=(5, 8, 9, 6, 1)),
  },
  "before": {
    32: dict(phone=(5, 4, 11, 20, 3.4), screen=(10, 13, 19, 15, 3)),
    16: dict(phone=(2, 2, 6, 10, 1.8), screen=(5, 7, 9, 7, 1.5)),
  },
  "r2": {
    32: dict(phone=(6, 5, 8, 17, 3), island=(8.5, 7, 3, 1.2), screen=(10, 13, 15, 10, 1.5), base=(8, 24.5, 19, 1.5, .75)),
    16: dict(phone=(2, 1, 5, 10, 1.5), screen=(5, 6, 9, 6, 1), base=(4, 13, 11, 1, .5)),
  },
}
def bbox(s):
    xs, ys, xe, ye = [], [], [], []
    for k in ("phone", "screen", "base"):
        if k in s:
            x, y, w, h = s[k][:4]; xs.append(x); ys.append(y); xe.append(x + w); ye.append(y + h)
    return min(xs), min(ys), max(xe), max(ye)

_uid = [0]
def uid(): _uid[0] += 1; return _uid[0]

def mark_color(s, col):
    """col: phone, screen, share. Telefon önde; kesişim = paylaşılan alan (turuncu)."""
    px, py, pw, ph, pr = s["phone"]; sx, sy, sw, sh, sr = s["screen"]; i = uid()
    phone_d = rr(px, py, pw, ph, pr)
    if "island" in s:
        ix, iy, iw, ih = s["island"]; phone_d += rr(ix, iy, iw, ih, ih / 2)
    o = f'<defs><clipPath id="p{i}"><path d="{rr(px,py,pw,ph,pr)}"/></clipPath></defs>'
    o += f'<path fill="{col["screen"]}" d="{rr(sx,sy,sw,sh,sr)}"/>'
    if "base" in s:
        bx, by, bw, bh, br = s["base"]; o += f'<path fill="{col["screen"]}" d="{rr(bx,by,bw,bh,br)}"/>'
    o += f'<path fill="{col["phone"]}" fill-rule="evenodd" d="{phone_d}"/>'
    o += f'<path fill="{col["share"]}" clip-path="url(#p{i})" d="{rr(sx,sy,sw,sh,sr)}"/>'
    return o

def mark_mono(s, color):
    """Tek renk: telefon XOR ekran — kesişim boşluk olarak okunur (baskı, gravür, tepsi)."""
    px, py, pw, ph, pr = s["phone"]; sx, sy, sw, sh, sr = s["screen"]
    d = rr(px, py, pw, ph, pr) + rr(sx, sy, sw, sh, sr)
    if "island" in s:
        ix, iy, iw, ih = s["island"]; d += rr(ix, iy, iw, ih, ih / 2)
    o = f'<path fill="{color}" fill-rule="evenodd" d="{d}"/>'
    if "base" in s:
        bx, by, bw, bh, br = s["base"]; o += f'<path fill="{color}" d="{rr(bx,by,bw,bh,br)}"/>'
    return o

# Renk setleri
COL_LIGHT = dict(phone=C["graphite"], screen=C["steel"], share=C["signal_light"])
COL_DARK = dict(phone=C["paper"], screen="#4A4E57", share=C["signal"])
COL_TILE = dict(phone=C["paper"], screen=C["steel_dark"], share=C["signal"])
TILE = dict(bg=C["graphite"], hi="#2B2E35")

FINAL_VARIANT = "r1"   # rafine edilmiş işaret (refinement.png'de karşılaştırma)
def master(v=None): return SPECS[v or FINAL_VARIANT][48]

# ------------------------------------------------------------------ SVG
def svg(w, h, inner, vb=None, size=True):
    vb = vb or f"0 0 {w} {h}"
    sz = f' width="{w}" height="{h}"' if size else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{vb}"{sz}>{inner}</svg>'

def tile_bg(size, r, flat=False):
    i = uid()
    if flat:
        return f'<rect width="{size}" height="{size}" rx="{r}" fill="{TILE["bg"]}"/>'
    sw = max(size / 48, .5) if size >= 32 else 0
    o = (f'<defs><linearGradient id="t{i}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TILE["hi"]}"/>'
         f'<stop offset=".55" stop-color="{TILE["bg"]}"/></linearGradient></defs>'
         f'<rect width="{size}" height="{size}" rx="{r}" fill="url(#t{i})"/>')
    if sw:
        o += (f'<rect x="{sw/2}" y="{sw/2}" width="{size-sw}" height="{size-sw}" rx="{r-sw/2}" fill="none" '
              f'stroke="#fff" stroke-opacity=".10" stroke-width="{sw}"/>')
    return o

def tile_radius(size): return {16: 3, 20: 4, 24: 4.5, 32: 6}.get(size, round(size * 0.205, 2))
def mark_ratio(size): return {16: .72, 20: .68, 24: .64, 32: .60, 40: .55}.get(size, .52)

def place(s, box_w, box_h, target, cx=None, cy=None):
    """İşareti (target = uzun kenar) verilen kutuda ortalayan transform."""
    x0, y0, x1, y1 = bbox(s); k = target / max(x1 - x0, y1 - y0)
    cx = box_w / 2 if cx is None else cx; cy = box_h / 2 if cy is None else cy
    return f'translate({cx:.3f} {cy:.3f}) scale({k:.5f}) translate({-(x0+x1)/2:.3f} {-(y0+y1)/2:.3f})'

def app_icon(size, v=None, flat=False):
    """Uygulama ikonu (karo). 16/20/24/32 piksel ızgarasında elle oturtulmuş çizim."""
    v = v or FINAL_VARIANT
    if size in PIXEL.get(v, {}):
        inner = mark_color(PIXEL[v][size], COL_TILE)
    else:
        inner = f'<g transform="{place(master(v), size, size, size * mark_ratio(size))}">{mark_color(master(v), COL_TILE)}</g>'
    return svg(size, size, tile_bg(size, tile_radius(size), flat) + inner)

def unplated(size, col, mono=None):
    """Karosuz işaret (görev çubuğu / tepsi). Küçük boylarda bir büyük boyun piksel çizimi kaydırılarak kullanılır."""
    src = {16: (20, -2, -2), 20: (24, -2, -2), 24: (32, -4, -4)}
    if size in src and src[size][0] in PIXEL[FINAL_VARIANT]:
        n, dx, dy = src[size]; s = PIXEL[FINAL_VARIANT][n]
        inner = mono and mark_mono(s, mono) or mark_color(s, col)
        return svg(size, size, f'<g transform="translate({dx} {dy})">{inner}</g>')
    s = master(); inner = mono and mark_mono(s, mono) or mark_color(s, col)
    return svg(size, size, f'<g transform="{place(s, size, size, size * .88)}">{inner}</g>')

# ------------------------------------------------------------------ kelime işareti ve imza
def word(x, baseline, size, color, text="NitroDrop"):
    d, w = text_path(text, *WORD_FONT, size, x, baseline, tracking=WORD_TRACK)
    return f'<path fill="{color}" d="{d}"/>', w

def cap(size): return metrics(*WORD_FONT, size)["cap"]

def endorsement(x, y, size, sub, center=False, tr=True, text1="BY NITROBYTE", text2="MADE IN TÜRKİYE"):
    """İmza satırı: BY NITROBYTE  |  ▍MADE IN TÜRKİYE — kırmızı (#E30A17) yalnızca küçük çubukta."""
    d1, w1 = text_path(text1, *MONO, size, 0, 0, tracking=60)
    d2, w2 = text_path(text2, *MONO, size, 0, 0, tracking=60)
    sep = size * 1.6; bar = size * .3; gap = size * .55
    tw = w1 + sep + (bar + gap if tr else 0) + w2
    if center: x -= tw / 2
    capm = size * .73
    o = (f'<path fill="{sub}" transform="translate({x:.2f} {y:.2f})" d="{d1}"/>'
         f'<rect x="{x + w1 + sep/2 - size*.06:.2f}" y="{y - capm:.2f}" width="{size*.12:.2f}" height="{capm:.2f}" fill="{sub}" opacity=".45"/>')
    x2 = x + w1 + sep
    if tr:
        o += f'<rect x="{x2:.2f}" y="{y - capm:.2f}" width="{bar:.2f}" height="{capm:.2f}" rx="{size*.04:.2f}" fill="{C["tr_red"]}"/>'
        x2 += bar + gap
    o += f'<path fill="{sub}" transform="translate({x2:.2f} {y:.2f})" d="{d2}"/>'
    return o, tw

def lockup_h(col, wordc, sub=None, size=100, pad=0, mono=None, v=None):
    """Yatay logo. İşaret yüksekliği = 1,62 × büyük harf; boşluk = 0,62 × büyük harf. sub verilirse imza satırı eklenir."""
    s = master(v); ch = cap(size); x0, y0, x1, y1 = bbox(s)
    mh = ch * 1.7; k = mh / (y1 - y0); mw = (x1 - x0) * k
    base = pad + mh / 2 + ch / 2 + (0 if not sub else 0)
    top = base - ch / 2 - mh / 2
    g = mono and mark_mono(s, mono) or mark_color(s, col)
    o = f'<g transform="translate({pad - x0*k:.3f} {top - y0*k:.3f}) scale({k:.5f})">{g}</g>'
    tx = pad + mw + ch * .62
    wd, ww = word(tx, base, size, wordc); o += wd
    W = tx + ww + pad; H = top + mh + pad
    if sub:
        e, _ = endorsement(tx, base + size * .52, size * .22, sub); o += e; H = base + size * .52 + pad + 4
    return o, W, H

def lockup_v(col, wordc, sub=None, size=100, pad=0, mono=None, v=None):
    s = master(v); ch = cap(size); x0, y0, x1, y1 = bbox(s)
    _, ww = word(0, 0, size, wordc)
    mh = ch * 2.6; k = mh / (y1 - y0); mw = (x1 - x0) * k
    ew = endorsement(0, 0, size * .2, "#000")[1] if sub else 0
    W = max(ww, ew, mw) + 2 * pad
    g = mono and mark_mono(s, mono) or mark_color(s, col)
    o = f'<g transform="translate({W/2 - mw/2 - x0*k:.3f} {pad - y0*k:.3f}) scale({k:.5f})">{g}</g>'
    base = pad + mh + ch * .8 + ch
    wd, _ = word(W / 2 - ww / 2, base, size, wordc); o += wd
    H = base + ch * .34 + pad  # p'nin inen çizgisi
    if sub:
        e, _ = endorsement(W / 2, base + size * .62, size * .2, sub, center=True); o += e; H = base + size * .62 + pad + 4
    return o, W, H

def data_uri(s, mime="image/svg+xml"):
    return f"data:{mime};base64," + base64.b64encode(s.encode() if isinstance(s, str) else s).decode()

FONTFACE = f"""
@font-face{{font-family:Mono;src:url('file:///{FONTS}/JetBrainsMono-VF.ttf');font-weight:100 900}}
@font-face{{font-family:Schibsted;src:url('file:///{FONTS}/SchibstedGrotesk-VF.ttf');font-weight:100 900}}
"""

# ------------------------------------------------------------------ render
def _run(args):
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                    "--allow-file-access-from-files", "--virtual-time-budget=5000", "--no-first-run",
                    "--no-default-browser-check"] + args, check=True, capture_output=True, timeout=180)

def screenshot(html_path, png_path, w, h, transparent=False):
    url = "file:///" + html_path.replace("\\", "/")
    extra = ["--default-background-color=00000000"] if transparent else []
    _run(extra + [f"--window-size={w},{h}", f"--screenshot={png_path}", url])

def print_pdf(html_path, pdf_path):
    url = "file:///" + html_path.replace("\\", "/")
    _run(["--no-pdf-header-footer", "--print-to-pdf-no-header", f"--print-to-pdf={pdf_path}", url])

def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f: f.write(text)

def svg_to_pdf(svg_text, pdf_path, w, h):
    """SVG'yi vektör olarak, sayfa boyutu = SVG boyutu (px→pt) olan tek sayfalık PDF'e basar."""
    html = (f'<!doctype html><html><head><meta charset="utf-8"><style>@page{{size:{w}px {h}px;margin:0}}'
            f'html,body{{margin:0;padding:0}} svg{{display:block;width:{w}px;height:{h}px}}</style></head>'
            f'<body>{svg_text}</body></html>')
    tmp = os.path.join(HERE, "_tmp_pdf.html"); write(tmp, html); print_pdf(tmp, pdf_path); os.remove(tmp)

def raster_atlas(items, out_dir):
    """items: [(name, svg_text, w, h)] → tek bir şeffaf atlas render edilir, PIL ile kesilir. {name: PIL.Image}"""
    from PIL import Image
    W = 2600; x = y = rowh = 0; pos = []
    for name, s, w, h in items:
        if x + w > W: x = 0; y += rowh + 4; rowh = 0
        pos.append((name, s, w, h, x, y)); x += w + 4; rowh = max(rowh, h)
    H = y + rowh + 4
    body = "".join(f'<img style="position:absolute;left:{px}px;top:{py}px;width:{w}px;height:{h}px" src="{data_uri(s)}"/>'
                   for name, s, w, h, px, py in pos)
    html = (f'<!doctype html><html><head><style>html,body{{margin:0;background:transparent}}</style></head>'
            f'<body style="width:{W}px;height:{H}px;position:relative">{body}</body></html>')
    hp = os.path.join(HERE, "_atlas.html"); pp = os.path.join(HERE, "_atlas.png")
    write(hp, html); screenshot(hp, pp, W, H, transparent=True)
    im = Image.open(pp).convert("RGBA"); out = {}
    for name, s, w, h, px, py in pos:
        out[name] = im.crop((px, py, px + w, py + h))
    os.remove(hp); os.remove(pp)
    return out
