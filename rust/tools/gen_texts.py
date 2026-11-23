"""Extracts the Russian UI string table from InterfaceText.cs into fd-core/src/texts_ru.json (ordered pairs).
Run from source-v3:  python rust/tools/gen_texts.py"""
import json
import pathlib
import re

src = pathlib.Path("src/FloatingDock.Core/Services/InterfaceText.cs").read_text(encoding="utf-8-sig")
start = src.index("Russian = new")
end = src.index("English = Russian")
body = src[start:end]
rx = re.compile(r'\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=\s*"((?:[^"\\]|\\.)*)"')


def unescape(s):
    try:
        return json.loads('"' + s + '"')
    except Exception:
        return s.replace('\\"', '"').replace("\\\\", "\\")


pairs = {}
for m in rx.finditer(body):
    # dictionary semantics of an indexer initializer: first position, last value wins
    pairs[unescape(m.group(1))] = unescape(m.group(2))
out = pathlib.Path("rust/crates/fd-core/src/texts_ru.json")
out.write_text(json.dumps([[k, v] for k, v in pairs.items()], ensure_ascii=False, indent=0), encoding="utf-8")
print(len(pairs), "entries")
