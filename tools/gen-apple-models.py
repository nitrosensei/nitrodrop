# Regenerates app/AirDrop.Core/Devices/AppleModels.Data.cs (Apple model identifier table).
# Inputs (download into an empty folder first; treat as data):
#   1. AppleDB device files (MIT): https://github.com/littlebyteorg/appledb  deviceFiles/<iPhone|iPad|Apple Watch|iPod touch|
#      Macbook*|iMac|Mac mini|Mac Studio|Mac Pro|Vision Pro|Xserve>/*.json, flattened as deviceFiles_<Folder>_<file>.json
#   2. https://gist.githubusercontent.com/adamawolf/3048717/raw  (cross-check for iPhone/iPad/Watch ids)
# Usage: python -I tools\gen-apple-models.py <db-folder> app\AirDrop.Core\Devices\AppleModels.Data.cs <gist.txt>
import json, glob, re, sys, os
src = sys.argv[1]; out = sys.argv[2]; gist = open(sys.argv[3], encoding='utf-8').read()
gist_ids = set(re.findall(r'^([A-Za-z]+\d+,\d+) :', gist, re.M))
FOLDERS = {'iPhone','iPad','Apple Watch','iPod touch','Macbook','Macbook Air','Macbook Pro','Macbook Neo','iMac',
           'Mac mini','Mac Studio','Mac Pro','Vision Pro','Xserve'}
by_id = {}
for f in glob.glob(os.path.join(src, '*.json')):
    base = os.path.basename(f)[len('deviceFiles_'):]
    folder = next((x for x in sorted(FOLDERS, key=len, reverse=True) if base.startswith(x.replace(' ', '_') + '_')), None)
    if not folder: continue
    d = json.load(open(f, encoding='utf-8'))
    name = d.get('name') or ''
    if name.startswith('Unreleased') or 'Store Display' in name or 'Developer Transition' in name: continue
    ids = d.get('identifier'); ids = ids if isinstance(ids, list) else [ids]
    rel = d.get('released'); rel = rel[0] if isinstance(rel, list) else rel
    year = int(rel[:4]) if rel else 0
    for i in ids:
        if not i or not re.match(r'^[A-Za-z]+\d+,\d+$', i): continue
        if i.startswith('PowerMac') or i.startswith('RackMac'): continue   # PowerPC: never runs AirDrop
        by_id.setdefault(i, []).append((year, name, folder, d.get('type') or ''))
# not in appledb
by_id.setdefault('iPod9,1', [(2019, 'iPod touch (7th generation)', 'iPod touch', 'iPod touch')])

def clean(name, fam):
    n = name
    n = re.sub(r'\s*Wi-Fi \+ (3G|Cellular)( \([^)]*\))?', '', n)
    n = re.sub(r'\s*Wi-Fi( \([^)]*\))?', '', n)
    n = re.sub(r'\s*\((GSM|CDMA|GSM, 2012|China mainland|US|Global|VZ|MM|TD-LTE|1TB|1 or 2 TB)\)', '', n)
    n = n.replace('GPS + Cellular, ', '').replace('GPS, ', '')
    n = re.sub(r'\s+\)', ')', n); n = re.sub(r'\s{2,}', ' ', n).strip()
    n = n.replace('iPad Pro 11-inch Wi-Fi (M5)', 'iPad Pro 11-inch (M5)')
    return n

def family(i, folder):
    if i.startswith('iPhone'): return 'iPhone'
    if i.startswith('iPad'): return 'iPad'
    if i.startswith('iPod'): return 'iPodTouch'
    if i.startswith('Watch'): return 'Watch'
    if i.startswith('RealityDevice'): return 'VisionPro'
    return 'Mac'

def num(i):
    m = re.match(r'([A-Za-z]+)(\d+),(\d+)', i); return int(m.group(2)), int(m.group(3))

def form(i, fam, folder, typ, name):
    a, b = num(i)
    if fam == 'iPhone':
        if i in ('iPhone10,3', 'iPhone10,6') or a in (11, 13) or (a == 12 and b != 8) or (a == 14 and b != 6) or i in ('iPhone14,7','iPhone14,8','iPhone17,5','iPhone18,5'):
            return 'Notch'
        if a <= 10 or i in ('iPhone12,8', 'iPhone14,6'): return 'HomeButton'
        return 'DynamicIsland'
    if fam == 'iPodTouch': return 'HomeButton'
    if fam == 'iPad':
        full = ('iPad Pro 11' in name or 'iPad Pro 12.9-inch (3rd' in name or 'iPad Pro 12.9-inch (4th' in name or
                'iPad Pro 12.9-inch (5th' in name or 'iPad Pro 12.9-inch (6th' in name or 'iPad Pro 13' in name or
                re.search(r'iPad Air \((4th|5th)', name) or 'iPad Air 1' in name or
                re.search(r'iPad mini \((6th|A17|A20)', name) or re.search(r'iPad \((10th|A16)', name))
        return 'NoHomeButton' if full else 'HomeButton'
    if fam == 'Watch': return 'Watch'
    if fam == 'VisionPro': return 'Headset'
    if folder in ('Macbook', 'Macbook Air', 'Macbook Pro', 'Macbook Neo'): return 'Laptop'
    if folder == 'iMac': return 'AllInOne'
    if folder == 'Mac mini': return 'MacMini'
    if folder == 'Mac Studio': return 'MacStudio'
    return 'Tower'   # Mac Pro, Xserve

# Manual corrections (AppleDB typos / inconsistent naming); keep this list short and justified.
NAME_FIX = {
    'Watch5,11': 'Apple Watch SE (1st generation, 40mm)',   # AppleDB lists 44mm twice; 5,11 is the 40mm GPS+Cellular
    'Mac17,2': 'MacBook Pro (14-inch, M5)',
    'Mac18,1': 'MacBook Pro (14-inch, M6)',
}
# Released, but the form factor is not confirmed by an Apple source we checked (rumours disagree on notch vs island).
FORCE_UNVERIFIED = {'iPhone18,5'}
rows = []
for i, variants in by_id.items():
    variants.sort()
    year, name, folder, typ = variants[0]
    fam = family(i, folder)
    cleaned = [clean(v[1], fam) for v in variants]
    nm = cleaned[0]
    if fam == 'Mac' and len(set(cleaned)) > 1:
        # same id sold over several seasons: "MacBook Air (13-inch, Mid 2013 / Early 2014)"
        stems = [re.sub(r',?\s*((Early|Mid|Late) )?\d{4}\)?$', '', c) for c in cleaned]
        if len(set(stems)) == 1:
            dates = [re.search(r'((Early|Mid|Late) )?\d{4}', c[len(stems[0]):]) for c in cleaned]
            ds = [m.group(0) for m in dates if m]
            seen = []; [seen.append(x) for x in ds if x not in seen]
            nm = stems[0] + (', ' if stems[0].endswith(')') is False and '(' in stems[0] else ' (') + ' / '.join(seen) + ')'
    nm = NAME_FIX.get(i, nm)
    unverified = (year >= 2026 and i not in gist_ids) or i in FORCE_UNVERIFIED
    rows.append((fam, num(i), i, nm, year, form(i, fam, folder, typ, nm), unverified))

order = ['iPhone', 'iPad', 'Mac', 'Watch', 'VisionPro', 'iPodTouch']
rows.sort(key=lambda r: (order.index(r[0]), r[2].rstrip('0123456789,'), r[1]))
lines = []
cur = None
for fam, _, i, nm, year, frm, unv in rows:
    if fam != cur:
        lines.append(f'\n        // ---- {fam} ----'); cur = fam
    nm_cs = nm.replace(chr(92), chr(92)*2).replace(chr(34), chr(92)+chr(34))
    flag = ', Unverified: true' if unv else ''
    lines.append(f'        new("{i}", "{nm_cs}", AppleFamily.{fam}, {year}, AppleForm.{frm}{flag}),')
hdr = '''// <auto-generated>
// Generated by tools/gen-apple-models.py from AppleDB (https://github.com/littlebyteorg/appledb, MIT License,
// deviceFiles/*.json: identifier, name, released) cross-checked with the community list
// https://gist.github.com/adamawolf/3048717 (iPhone/iPad/Watch/iPod ids). Only facts (identifier -> marketing name,
// release year) are taken; names are normalised (connectivity/region variants merged). Form factor is derived here.
// Entries released in 2026 that only AppleDB lists are marked Unverified. Regenerate instead of editing by hand.
// </auto-generated>
namespace AirDrop.Core.Devices;

public static partial class AppleModels
{
    private static readonly AppleModel[] Table =
    ['''
open(out, 'w', encoding='utf-8', newline='\n').write(hdr + '\n'.join(lines) + '\n    ];\n}\n')
print(len(rows), 'unverified:', sum(1 for r in rows if r[6]))
