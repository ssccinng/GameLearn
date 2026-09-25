"""Build the bundled offline dictionary. Runtime never downloads vocabulary."""
import csv
import hashlib
import json
import pathlib
import sqlite3
import urllib.request
import argparse
import gzip
import time

root = pathlib.Path(__file__).resolve().parents[1]
cache = root / "artifacts" / "dictionary"
assets = root / "src" / "GameLearn" / "Assets"
cache.mkdir(parents=True, exist_ok=True)
assets.mkdir(parents=True, exist_ok=True)
commit = "bc015ed2e24a"
base = f"https://raw.githubusercontent.com/skywind3000/ECDICT/{commit}/"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--proxy", help="Optional HTTP proxy for downloading the dictionary")
args = parser.parse_args()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({"https": args.proxy, "http": args.proxy})) if args.proxy else urllib.request.build_opener()
for name in ("ecdict.csv", "LICENSE"):
    target = cache / name
    if not target.exists():
        print("Downloading", name, flush=True)
        for attempt in range(3):
            try:
                request = urllib.request.Request(base + name, headers={"Accept-Encoding": "gzip", "User-Agent": "GameLearn-dictionary-builder"})
                with opener.open(request, timeout=120) as response:
                    data = response.read()
                    if response.headers.get("Content-Encoding") == "gzip":
                        data = gzip.decompress(data)
                target.write_bytes(data)
                break
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(attempt + 1)
expected_sha = "1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf"
if hashlib.sha256((cache / "ecdict.csv").read_bytes()).hexdigest() != expected_sha:
    raise RuntimeError("Dictionary checksum mismatch; remove the cached CSV and download again.")
destination = assets / "ecdict.sqlite"
temp = assets / "ecdict.building"
if temp.exists():
    temp.unlink()
with sqlite3.connect(temp) as db:
    db.executescript("PRAGMA journal_mode=OFF; CREATE TABLE entries(word TEXT PRIMARY KEY COLLATE NOCASE, phonetic TEXT, translation TEXT, lemma TEXT); CREATE TABLE forms(form TEXT PRIMARY KEY COLLATE NOCASE, lemma TEXT);")
    count = 0
    with (cache / "ecdict.csv").open(encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            word = row["word"].strip().lower()
            if not word:
                continue
            exchange = dict(part.split(":", 1) for part in row.get("exchange", "").split("/") if ":" in part)
            lemma = exchange.get("0", "").split("/")[0]
            db.execute("INSERT OR IGNORE INTO entries VALUES(?,?,?,?)", (word, row.get("phonetic", ""), row.get("translation", "").replace("\\n", "\n"), lemma))
            for key, forms in exchange.items():
                if key in ("p", "d", "i", "3", "r", "t", "s"):
                    for form in forms.split(","):
                        if form and form != word:
                            db.execute("INSERT OR IGNORE INTO forms VALUES(?,?)", (form.lower(), word))
            count += 1
    db.commit()
    db.execute("VACUUM")
db.close()
temp.replace(destination)
(assets / "ECDICT-LICENSE.txt").write_bytes((cache / "LICENSE").read_bytes())
(assets / "dictionary-source.json").write_text(json.dumps({"source": base + "ecdict.csv", "entries": count, "sha256": hashlib.sha256((cache / "ecdict.csv").read_bytes()).hexdigest()}, indent=2), encoding="utf-8")
print(f"Dictionary ready: {count:,} entries, {destination.stat().st_size:,} bytes", flush=True)
