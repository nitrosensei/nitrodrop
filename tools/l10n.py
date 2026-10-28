"""NitroDrop UI strings: .resx <-> side-by-side JSON, and consistency checks.

The .resx files in app/AirDrop.App/Localization are the source of truth (Turkish neutral <Module>.resx +
<Module>.<lang>.resx). This tool only makes editing all six languages side by side convenient.

  python -I tools/l10n.py import <file.json>...     merge keys into the module's .resx files (adds or replaces)
  python -I tools/l10n.py export <Module> [out.json] all languages of a module side by side
  python -I tools/l10n.py remove <Module> <Key>...   delete keys from every language of a module
  python -I tools/l10n.py check                      missing/empty keys, placeholders, plural forms, duplicates
  python -I tools/l10n.py gen                        regenerate Localization/Generated/<Module>Strings.g.cs
                                                     (import/remove run it too)

Accessors: plain key -> `MainStrings.Key` (string property); key with {0}.. -> `MainStrings.Key(a, b)` (formatted in
the UI culture); key ending in _Plural -> `MainStrings.Key_Plural(n, extra...)` ({0} = n).

JSON format (import/export):
  {"module": "Main",
   "strings": {"Nearby_Searching": {"tr": "...", "en": "...", "de": "...", "fr": "...", "ru": "...", "pl": "...",
                                    "comment": "optional context for translators (stored in the neutral .resx)"}}}

Plural keys end with "_Plural"; forms are separated by "|": tr 1 form, en/de/fr 2 (one|other), ru/pl 3 (one|few|many).
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LOC = ROOT / "app" / "AirDrop.App" / "Localization"
LANGS = ["tr", "en", "de", "fr", "ru", "pl"]
FORMS = {"tr": 1, "en": 2, "de": 2, "fr": 2, "ru": 3, "pl": 3}

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def resx_path(module, lang):
    return LOC / (f"{module}.resx" if lang == "tr" else f"{module}.{lang}.resx")


def read_resx(path):
    """-> ordered dict key -> (value, comment)"""
    out = {}
    if not path.exists():
        return out
    tree = ET.parse(path)
    for d in tree.getroot().findall("data"):
        v = d.find("value")
        c = d.find("comment")
        out[d.get("name")] = ((v.text or "") if v is not None else "", c.text if c is not None else None)
    return out


def esc(s):
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def write_resx(path, entries):
    lines = [HEADER]
    for k, (v, c) in entries.items():
        lines.append(f'  <data name="{esc(k)}" xml:space="preserve">\n    <value>{esc(v)}</value>\n')
        if c:
            lines.append(f"    <comment>{esc(c)}</comment>\n")
        lines.append("  </data>\n")
    lines.append("</root>\n")
    path.write_text("".join(lines), encoding="utf-8", newline="\r\n")


def modules():
    return sorted(p.stem for p in LOC.glob("*.resx") if "." not in p.stem)


def cmd_import(files):
    for f in files:
        data = json.loads(Path(f).read_text(encoding="utf-8"))
        module = data["module"]
        strings = data["strings"]
        for lang in LANGS:
            path = resx_path(module, lang)
            entries = read_resx(path)
            for key, tr in strings.items():
                if lang not in tr:
                    raise SystemExit(f"{f}: {key} has no '{lang}'")
                comment = tr.get("comment") if lang == "tr" else None
                if comment is None and key in entries and lang == "tr":
                    comment = entries[key][1]
                entries[key] = (tr[lang], comment)
            write_resx(path, entries)
        print(f"{module}: {len(strings)} keys imported from {f}")
        gen_module(module)


def cmd_export(module, out=None):
    per = {lang: read_resx(resx_path(module, lang)) for lang in LANGS}
    strings = {}
    for key, (v, c) in per["tr"].items():
        e = {lang: per[lang].get(key, ("", None))[0] for lang in LANGS}
        if c:
            e["comment"] = c
        strings[key] = e
    text = json.dumps({"module": module, "strings": strings}, ensure_ascii=False, indent=1)
    if out:
        Path(out).write_text(text, encoding="utf-8")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(text)


def cmd_remove(module, keys):
    for lang in LANGS:
        path = resx_path(module, lang)
        entries = read_resx(path)
        for k in keys:
            entries.pop(k, None)
        write_resx(path, entries)
    gen_module(module)


PH = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")


def placeholders(s):
    return sorted(set(PH.findall(s)))


IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def xml_doc(s):
    s = s.replace(chr(13), " ").replace(chr(10), " ")
    if len(s) > 110:
        s = s[:107] + "..."
    return esc(s)


def gen_module(m):
    entries = read_resx(resx_path(m, "tr"))
    cls = f"{m}Strings"
    out = [
        "// <auto-generated>",
        f"//   tools/l10n.py gen from Localization/{m}.resx. Do not edit: change the .resx files and regenerate.",
        "// </auto-generated>",
        "#nullable enable",
        "namespace AirDrop.App.Localization;",
        "",
        f"/// <summary>Strings of the {m} module (Turkish source text in the summaries).</summary>",
        f"internal static class {cls}",
        "{",
        "    private static global::System.Resources.ResourceManager? _rm;",
        "",
        "    public static global::System.Resources.ResourceManager ResourceManager =>",
        f'        _rm ??= Loc.CreateManager("{m}", typeof({cls}).Assembly);',
        "",
        "    private static string S(string key) => ResourceManager.GetString(key, Loc.Culture) ?? key;",
    ]
    for key, (v, _) in entries.items():
        if not IDENT.match(key):
            raise SystemExit(f"{m}: key '{key}' is not a C# identifier")
        out.append("")
        out.append(f"    /// <summary>{xml_doc(v)}</summary>")
        ph = [int(x) for x in placeholders(v)]
        if key.endswith("_Plural"):
            extra = "".join(f", object? p{i}" for i in range(1, (max(ph) + 1) if ph else 1))
            args = "".join(f", p{i}" for i in range(1, (max(ph) + 1) if ph else 1))
            out.append(f'    public static string {key}(long n{extra}) => Loc.Plural(S("{key}"), n{args});')
        elif ph:
            n = max(ph) + 1
            params = ", ".join(f"object? p{i}" for i in range(n))
            args = ", ".join(f"p{i}" for i in range(n))
            out.append(f'    public static string {key}({params}) => Loc.Format(S("{key}"), {args});')
        else:
            out.append(f'    public static string {key} => S("{key}");')
    out.append("}")
    gen_dir = LOC / "Generated"
    gen_dir.mkdir(exist_ok=True)
    (gen_dir / f"{cls}.g.cs").write_text("".join(x + chr(13) + chr(10) for x in out), encoding="utf-8", newline="")


def cmd_gen():
    for m in modules():
        gen_module(m)


def cmd_check():
    errors = []
    seen = {}
    for m in modules():
        per = {lang: read_resx(resx_path(m, lang)) for lang in LANGS}
        for key, (src, _) in per["tr"].items():
            if key in seen:
                errors.append(f"{key}: duplicate in {seen[key]} and {m}")
            seen[key] = m
            for lang in LANGS:
                if key not in per[lang] or not per[lang][key][0].strip():
                    errors.append(f"{m}.{lang}: {key} missing/empty")
                    continue
                v = per[lang][key][0]
                if placeholders(v) != placeholders(src):
                    errors.append(f"{m}.{lang}: {key} placeholders {placeholders(v)} != tr {placeholders(src)}")
                if key.endswith("_Plural") and len(v.split("|")) != FORMS[lang]:
                    errors.append(f"{m}.{lang}: {key} has {len(v.split('|'))} plural forms, needs {FORMS[lang]}")
        for lang in LANGS[1:]:
            for key in per[lang]:
                if key not in per["tr"]:
                    errors.append(f"{m}.{lang}: {key} not in neutral resx")
    sys.stdout.reconfigure(encoding="utf-8")
    for e in errors:
        print(e)
    print(f"{len(seen)} keys in {len(modules())} modules, {len(errors)} problems")
    return 1 if errors else 0


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a:
        print(__doc__)
    elif a[0] == "import":
        cmd_import(a[1:])
    elif a[0] == "export":
        cmd_export(a[1], a[2] if len(a) > 2 else None)
    elif a[0] == "remove":
        cmd_remove(a[1], a[2:])
    elif a[0] == "gen":
        cmd_gen()
    elif a[0] == "check":
        sys.exit(cmd_check())
    else:
        print(__doc__)
