"""NitroDrop Faz 2: logo varyantları, uygulama/Store varlıkları, sosyal görseller, ICO dosyaları.
Çalıştır: python build_final.py   (sonra: python guide.py)
"""
import os, io, struct, json
from common import *

APP = os.path.join(FINAL, "app")
PNGDIR = os.path.join(APP, "png")
STORE = os.path.join(APP, "store")
EK = os.path.join(FINAL, "ek")

def clear_pad(size=100):
    """Güvenli alan: x = büyük harf yüksekliğinin 0,7'si (≈ telefon genişliği)."""
    return cap(size) * .7

# ------------------------------------------------------------------ logo varyantları
def logo_variants():
    out = {}
    p = clear_pad()
    def emit(name, inner, W, H):
        s = svg(round(W, 2), round(H, 2), inner)
        base = os.path.join(FINAL, f"NitroDrop Logo - {name} [VEKTÖR]")
        write(base + ".svg", s); svg_to_pdf(s, base + ".pdf", round(W), round(H)); out[name] = s
    o, W, H = lockup_h(COL_LIGHT, C["graphite"], pad=p); emit("Ana (Renkli)", o, W, H)
    o, W, H = lockup_h(None, C["signal_text"], pad=p, mono=C["signal_text"]); emit("Ana (Tek Renk)", o, W, H)
    o, W, H = lockup_h(None, C["black"], pad=p, mono=C["black"]); emit("Siyah-Beyaz", o, W, H)
    s = master(); x0, y0, x1, y1 = bbox(s); pm = 16 * .7  # işaret güvenli alanı = telefon genişliğinin 0,7'si
    W, H = x1 - x0 + 2 * pm, y1 - y0 + 2 * pm
    emit("İkon", f'<g transform="translate({pm - x0} {pm - y0})">{mark_color(s, COL_LIGHT)}</g>', W, H)
    icon = app_icon(256).replace('width="256" height="256"', 'width="1024" height="1024"')
    base = os.path.join(FINAL, "NitroDrop Logo - Uygulama İkonu [VEKTÖR]")
    write(base + ".svg", icon); svg_to_pdf(icon, base + ".pdf", 1024, 1024); out["Uygulama İkonu"] = icon

    # ek: negatif, dikey, imzalı düzenler
    extras = {
        "yatay-negatif": lockup_h(COL_DARK, C["paper"], pad=p),
        "yatay-imzali-acik": lockup_h(COL_LIGHT, C["graphite"], sub=C["smoke"], pad=p),
        "yatay-imzali-koyu": lockup_h(COL_DARK, C["paper"], sub=C["fog"], pad=p),
        "dikey-acik": lockup_v(COL_LIGHT, C["graphite"], pad=p),
        "dikey-koyu": lockup_v(COL_DARK, C["paper"], pad=p),
        "dikey-imzali-acik": lockup_v(COL_LIGHT, C["graphite"], sub=C["smoke"], pad=p),
        "dikey-imzali-koyu": lockup_v(COL_DARK, C["paper"], sub=C["fog"], pad=p),
        "beyaz-tek-renk": lockup_h(None, C["white"], pad=p, mono=C["white"]),
    }
    for n, (o, W, H) in extras.items():
        write(os.path.join(EK, f"NitroDrop-{n}.svg"), svg(round(W, 2), round(H, 2), o)); out[n] = svg(round(W, 2), round(H, 2), o)
    write(os.path.join(EK, "NitroDrop-isaret-negatif.svg"),
          svg(round(W := x1 - x0 + 2 * pm, 2), round(y1 - y0 + 2 * pm, 2), f'<g transform="translate({pm - x0} {pm - y0})">{mark_color(s, COL_DARK)}</g>'))
    return out

# ------------------------------------------------------------------ store / tile görselleri
def sc(v, k): return int(v * k + .5)
SCALES = [(100, 1), (125, 1.25), (150, 1.5), (200, 2), (400, 4)]

def tile_square(w, mark_frac=.46):
    s = master()
    return svg(w, w, f'<rect width="{w}" height="{w}" fill="{C["graphite"]}"/>'
               f'<g transform="{place(s, w, w, w * mark_frac)}">{mark_color(s, COL_TILE)}</g>')

def tile_lockup(w, h, frac, vertical=False, bg=True, sub=False):
    o, W, H = (lockup_v if vertical else lockup_h)(COL_TILE if bg else COL_DARK, C["paper"], sub=C["fog"] if sub else None)
    k = min(w * frac / W, h * .7 / H)
    g = f'<g transform="translate({(w - W*k)/2:.2f} {(h - H*k)/2:.2f}) scale({k:.5f})">{o}</g>'
    back = f'<rect width="{w}" height="{h}" fill="{C["graphite"]}"/>' if bg else ""
    return svg(w, h, back + g)

def store_items():
    it = []
    for pct, k in SCALES:
        it.append((f"Square44x44Logo.scale-{pct}.png", app_icon(sc(44, k)), sc(44, k), sc(44, k)))
        it.append((f"Square150x150Logo.scale-{pct}.png", tile_square(sc(150, k)), sc(150, k), sc(150, k)))
        w, h = sc(310, k), sc(150, k); it.append((f"Wide310x150Logo.scale-{pct}.png", tile_lockup(w, h, .62), w, h))
        w = sc(310, k); it.append((f"Square310x310Logo.scale-{pct}.png", tile_lockup(w, w, .56, vertical=True), w, w))
        w = sc(50, k); it.append((f"StoreLogo.scale-{pct}.png", app_icon(w), w, w))
        w, h = sc(620, k), sc(300, k); it.append((f"SplashScreen.scale-{pct}.png", tile_lockup(w, h, .5, bg=False), w, h))
    for t in (16, 24, 32, 48, 256):
        it.append((f"Square44x44Logo.targetsize-{t}.png", app_icon(t), t, t))
        it.append((f"Square44x44Logo.targetsize-{t}_altform-unplated.png", unplated(t, COL_DARK), t, t))
        it.append((f"Square44x44Logo.targetsize-{t}_altform-lightunplated.png", unplated(t, COL_LIGHT), t, t))
    return it

# ------------------------------------------------------------------ sosyal görseller
def social_card():
    W, H = 1280, 640
    o, lw, lh = lockup_h(COL_DARK, C["paper"], size=100)
    k = 560 / lw
    s = master()
    big = f'<g opacity="1" transform="{place(s, W, H, 470, cx=1010, cy=300)}">{mark_color(s, dict(phone="#2A2D34", screen="#24272D", share=C["signal"]))}</g>'
    t1, w1 = text_path("Share files with iPhone & Mac —", "SchibstedGrotesk-VF.ttf", {"wght": 500}, 34, 96, 382)
    t2, w2 = text_path("right from Windows.", "SchibstedGrotesk-VF.ttf", {"wght": 500}, 34, 96, 426)
    t3, _ = text_path("FREE · OPEN SOURCE · NITROBYTE.COM.TR", *MONO, 17, 96, 488, tracking=60)
    e, _ = endorsement(96, 566, 17, C["fog"])
    inner = (f'<rect width="{W}" height="{H}" fill="{C["ink"]}"/>{big}'
             f'<g transform="translate(90 190) scale({k:.4f})">{o}</g>'
             f'<path fill="{C["paper"]}" d="{t1}"/><path fill="{C["paper"]}" d="{t2}"/>'
             f'<path fill="{C["signal_text_dark"]}" d="{t3}"/>'
             f'<rect x="96" y="526" width="{1280-192}" height="1" fill="#fff" opacity=".10"/>{e}')
    return svg(W, H, inner)

def readme_banner(dark):
    W, H = 1280, 320
    bg = C["ink"] if dark else "#FAFAFA"; fg = C["paper"] if dark else C["graphite"]; sub = C["fog"] if dark else C["smoke"]
    o, lw, lh = lockup_h(COL_DARK if dark else COL_LIGHT, fg, size=100)
    k = 470 / lw
    F = ("SchibstedGrotesk-VF.ttf", {"wght": 500})
    t1, _ = text_path("iPhone ve Mac ile dosya paylaşımı,", *F, 26, 690, 128)
    t2, _ = text_path("doğrudan Windows'tan.", *F, 26, 690, 162)
    t3, _ = text_path("Ücretsiz ve açık kaynak.", *F, 20, 690, 200)
    e, _ = endorsement(690, 246, 14, sub)
    t1 = t1 + t2
    t2 = t3
    inner = (f'<rect width="{W}" height="{H}" fill="{bg}"/>'
             f'<g transform="translate(80 {H/2 - lh*k/2:.1f}) scale({k:.4f})">{o}</g>'
             f'<rect x="640" y="96" width="1" height="{H-192}" fill="{fg}" opacity=".14"/>'
             f'<path fill="{fg}" d="{t1}"/><path fill="{sub}" d="{t2}"/>{e}')
    return svg(W, H, inner)

# ------------------------------------------------------------------ ICO
def write_ico(path, images):
    """images: PIL RGBA listesi. <256 → 32-bit BMP (alfa + AND maskesi), 256 → PNG. Windows/WPF uyumlu."""
    entries = []
    for im in sorted(images, key=lambda i: i.width):
        w, h = im.size
        if w >= 256:
            b = io.BytesIO(); im.save(b, "PNG"); data = b.getvalue()
        else:
            hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
            px = im.tobytes("raw", "BGRA"); row = w * 4
            xor = b"".join(px[(h - 1 - y) * row:(h - y) * row] for y in range(h))
            mrow = ((w + 31) // 32) * 4; a = im.getchannel("A").tobytes(); mask = bytearray()
            for y in range(h - 1, -1, -1):
                bits = bytearray(mrow)
                for x in range(w):
                    if a[y * w + x] == 0: bits[x >> 3] |= 0x80 >> (x & 7)
                mask += bits
            data = hdr + xor + bytes(mask)
        entries.append((w, h, data))
    off = 6 + 16 * len(entries); head = struct.pack("<HHH", 0, 1, len(entries)); dirs = b""; blob = b""
    for w, h, data in entries:
        dirs += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), off + len(blob)); blob += data
    with open(path, "wb") as f: f.write(head + dirs + blob)

def tray(size, color):
    return unplated(size, None, mono=color)

# ------------------------------------------------------------------ ana akış
def main():
    os.makedirs(PNGDIR, exist_ok=True); os.makedirs(STORE, exist_ok=True)
    logos = logo_variants(); print("logolar ok")
    items = []
    ICO = [16, 20, 24, 32, 40, 48, 64, 256]
    for n in ICO: items.append((f"ico-{n}", app_icon(n), n, n))
    for n in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024):
        items.append((f"png/NitroDrop-{n}.png", app_icon(n), n, n))
    for n in (16, 20, 24, 32):
        items.append((f"tray-light-{n}", tray(n, "#1C1C1C"), n, n))
        items.append((f"tray-dark-{n}", tray(n, "#FFFFFF"), n, n))
        items.append((f"png/tray-light-{n}.png", tray(n, "#1C1C1C"), n, n))
        items.append((f"png/tray-dark-{n}.png", tray(n, "#FFFFFF"), n, n))
    items += [(f"store/{n}", s, w, h) for n, s, w, h in store_items()]
    soc = social_card(); items.append(("github-social-preview.png", soc, 1280, 640))
    bl, bd = readme_banner(False), readme_banner(True)
    items.append(("readme-banner-light.png", bl, 1280, 320)); items.append(("readme-banner-dark.png", bd, 1280, 320))
    write(os.path.join(APP, "github-social-preview.svg"), soc)
    write(os.path.join(APP, "readme-banner-light.svg"), bl); write(os.path.join(APP, "readme-banner-dark.svg"), bd)
    for n in (16, 20, 24, 32):
        write(os.path.join(APP, "svg", f"tray-light-{n}.svg"), tray(n, "#1C1C1C"))
        write(os.path.join(APP, "svg", f"tray-dark-{n}.svg"), tray(n, "#FFFFFF"))
    for n in (16, 20, 24, 32, 48):
        write(os.path.join(APP, "svg", f"NitroDrop-icon-{n}.svg"), app_icon(n))
    ims = raster_atlas(items, APP)
    for name, im in ims.items():
        if name.endswith(".png"):
            p = os.path.join(APP, name); os.makedirs(os.path.dirname(p), exist_ok=True)
            if "SplashScreen" in name or "unplated" in name or "tray" in name:
                im.save(p)
            elif name.startswith("github") or name.startswith("readme") or "Wide310" in name or "Square150" in name or "Square310" in name:
                im.convert("RGB").save(p)
            else:
                im.save(p)
    write_ico(os.path.join(APP, "NitroDrop.ico"), [ims[f"ico-{n}"] for n in ICO])
    write_ico(os.path.join(APP, "tray-light.ico"), [ims[f"tray-light-{n}"] for n in (16, 20, 24, 32)])
    write_ico(os.path.join(APP, "tray-dark.ico"), [ims[f"tray-dark-{n}"] for n in (16, 20, 24, 32)])
    print("varlıklar ok:", len(ims))

if __name__ == "__main__":
    main()
