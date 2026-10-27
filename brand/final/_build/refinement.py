"""Önce/sonra panosu: Faz 1 Kesişim → rafine varyantlar. Çıktı: brand/final/refinement.png"""
import os
from common import *

LABELS = {
  "before": ("Önce · Faz 1", "Eşit ağırlıklı iki yuvarlak dikdörtgen. Doğru fikir ama “kopyala / pencereler” ikonlarına yakın."),
  "r1": ("Sonra · R1 (seçilen)", "Gerçek oranlar: telefon 16×34 ve büyük köşe (r6), ekran 16:10 ve dar köşe (r2,5). Üstte ada kesiği. "
         "Turuncu alan iki ayrı köşe karakteri taşıyor: sol üst dar, sağ alt geniş."),
  "r2": ("Alternatif · R2", "R1 artı dizüstü tabanı. Daha betimleyici ama 16 px'te taban tek piksel çizgiye iniyor ve işaret kalabalıklaşıyor."),
}

def grid_svg(v):
    s = master(v)
    g = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49, 2))
    g8 = "".join(f'<line x1="{i}" y1="0" x2="{i}" y2="48"/><line x1="0" y1="{i}" x2="48" y2="{i}"/>' for i in range(0, 49, 8))
    return svg(48, 48, f'<rect width="48" height="48" fill="#fff"/><g stroke="#000" stroke-opacity=".06" stroke-width=".1">{g}</g>'
               f'<g stroke="#000" stroke-opacity=".14" stroke-width=".14">{g8}</g>{mark_color(s, COL_LIGHT)}', size=False)

def icon_any(v, size):
    if v != FINAL_VARIANT and size not in PIXEL.get(v, {}):
        s = master(v)
        inner = f'<g transform="{place(s, size, size, size * mark_ratio(size))}">{mark_color(s, COL_TILE)}</g>'
        return svg(size, size, tile_bg(size, tile_radius(size)) + inner)
    from common import FINAL_VARIANT as FV
    import common
    old = common.FINAL_VARIANT; common.FINAL_VARIANT = v
    try: return app_icon(size, v)
    finally: common.FINAL_VARIANT = old

def col(v):
    t, d = LABELS[v]
    lh, W, H = lockup_h(COL_LIGHT, C["graphite"], v=v)
    ld, W2, H2 = lockup_h(COL_DARK, C["paper"], v=v)
    mono, W3, H3 = lockup_h(None, C["graphite"], mono=C["graphite"], v=v)
    i256 = icon_any(v, 256); i48 = icon_any(v, 48); i32 = icon_any(v, 32); i16 = icon_any(v, 16)
    sel = ' sel' if v == FINAL_VARIANT else ''
    return f"""<section class="{sel}"><h2>{t}</h2><p>{d}</p>
<div class="row"><img class="grid" src="{data_uri(grid_svg(v))}"/><img src="{data_uri(i256)}" width="180" height="180"/></div>
<div class="sizes"><img src="{data_uri(i48)}" width="48"/><img src="{data_uri(i32)}" width="32"/><img src="{data_uri(i16)}" width="16"/>
<canvas data-n="16" data-src="{data_uri(i16)}" width="16" height="16" style="width:96px;height:96px"></canvas>
<canvas data-n="32" data-src="{data_uri(i32)}" width="32" height="32" style="width:96px;height:96px"></canvas></div>
<div class="lk"><img src="{data_uri(svg(round(W), round(H), lh))}"/></div>
<div class="lk dk"><img src="{data_uri(svg(round(W2), round(H2), ld))}"/></div>
<div class="lk"><img src="{data_uri(svg(round(W3), round(H3), mono))}"/><span>tek renk</span></div></section>"""

html = f"""<!doctype html><html><head><meta charset="utf-8"><style>{FONTFACE}
*{{box-sizing:border-box;margin:0}} body{{width:1800px;height:1060px;background:#ECEAE6;font-family:Schibsted;padding:40px 50px;color:#1a1a1a}}
.mono{{font-family:Mono;font-size:12px;letter-spacing:.1em;text-transform:uppercase;color:#77736c}}
header{{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:1px solid #0002;padding-bottom:14px}}
h1{{font-size:38px;letter-spacing:-.02em}}
main{{display:grid;grid-template-columns:repeat(3,1fr);gap:24px;margin-top:22px}}
section{{background:#fff;border-radius:16px;padding:22px;outline:2px solid transparent}}
section.sel{{outline:2px solid {C["signal_light"]}}}
h2{{font-size:24px;letter-spacing:-.01em}} p{{font-size:14px;line-height:1.4;color:#45413b;height:60px;margin-top:6px}}
.row{{display:flex;gap:18px;align-items:center;margin-top:10px}} .grid{{width:300px;height:300px;border-radius:8px;box-shadow:inset 0 0 0 1px #0001}}
.sizes{{display:flex;gap:18px;align-items:center;margin:16px 0 6px}} canvas{{image-rendering:pixelated;border-radius:2px}}
.lk{{height:96px;border-radius:10px;background:{C["bg_light"]};display:flex;align-items:center;justify-content:center;margin-top:10px;position:relative}}
.lk img{{height:46px}} .lk.dk{{background:{C["ink"]}}} .lk span{{position:absolute;right:10px;bottom:6px;font-family:Mono;font-size:10px;color:#888}}
</style></head><body>
<header><div><div class="mono">NitroDrop · Kesişim · işaret rafinesi</div><h1>Önce / sonra</h1></div>
<div class="mono" style="text-align:right">Ölçütler: sahiplenilebilirlik · gerçek cihaz oranları · 16 px okunurluk</div></header>
<main>{col("before")}{col("r1")}{col("r2")}</main>
<script>document.querySelectorAll('canvas[data-src]').forEach(cv=>{{const n=+cv.dataset.n,i=new Image();
i.onload=()=>cv.getContext('2d').drawImage(i,0,0,n,n);i.src=cv.dataset.src;}});</script></body></html>"""

if __name__ == "__main__":
    p = os.path.join(HERE, "refinement.html"); write(p, html)
    screenshot(p, os.path.join(FINAL, "refinement.png"), 1800, 1060)
    print("ok")
