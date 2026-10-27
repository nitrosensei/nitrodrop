"""Metni gömülü vektör yola çevirir (uharfbuzz şekillendirme + kerning, değişken font örneği)."""
import uharfbuzz as hb, os
HERE = os.path.dirname(os.path.abspath(__file__))

class Pen:
    def __init__(s, ox, oy, k): s.d=[]; s.ox=ox; s.oy=oy; s.k=k
    def P(s, x, y): return f"{s.ox+x*s.k:.2f} {s.oy-y*s.k:.2f}"
    def moveTo(s,p): s.d.append("M"+s.P(*p))
    def lineTo(s,p): s.d.append("L"+s.P(*p))
    def qCurveTo(s,*ps):
        # hb gives single control point
        c,e = ps[-2], ps[-1]; s.d.append("Q"+s.P(*c)+" "+s.P(*e))
    def curveTo(s,a,b,c): s.d.append("C"+s.P(*a)+" "+s.P(*b)+" "+s.P(*c))
    def closePath(s): s.d.append("Z")
    def endPath(s): pass

_cache={}
def font(name, var):
    key=(name,tuple(sorted(var.items())))
    if key not in _cache:
        blob=hb.Blob.from_file_path(os.path.join(HERE,"fonts",name))
        f=hb.Font(hb.Face(blob)); f.set_variations(var); _cache[key]=f
    return _cache[key]

def text_path(text, name, var, size, x=0, y=0, tracking=0, kern_pairs=None):
    """Return (path_d, width). y = baseline. tracking in em/1000. kern_pairs: {(i):extra em/1000 after glyph i}"""
    f=font(name,var); upem=f.face.upem; k=size/upem
    buf=hb.Buffer(); buf.add_str(text); buf.guess_segment_properties()
    hb.shape(f,buf,{"kern":True,"liga":True})
    pen_x=0; d=[]
    for i,(inf,pos) in enumerate(zip(buf.glyph_infos,buf.glyph_positions)):
        p=Pen(x+(pen_x+pos.x_offset)*k, y-pos.y_offset*k, k)
        f.draw_glyph_with_pen(inf.codepoint,p); d+=p.d
        pen_x+=pos.x_advance+tracking*upem/1000
        if kern_pairs and i in kern_pairs: pen_x+=kern_pairs[i]*upem/1000
    pen_x-=tracking*upem/1000
    return "".join(d), pen_x*k

def metrics(name,var,size):
    f=font(name,var); upem=f.face.upem
    ext=f.get_font_extents("ltr")
    # cap height / x height from OS/2 via glyph extents
    def h(ch):
        g=f.get_nominal_glyph(ord(ch)); e=f.get_glyph_extents(g); return e.y_bearing*size/upem
    return {"cap":h("H"),"x":h("x")}
if __name__=="__main__":
    d,w=text_path("NitroDrop","Archivo-VF.ttf",{"wght":700,"wdth":112},100,0,100)
    print(w,len(d),metrics("Archivo-VF.ttf",{"wght":700,"wdth":112},100))
