"""Обновляет встроенный справочник из IANA и Unicode CLDR. Запускать вручную при обновлении данных."""
import io
import json
import tarfile
from pathlib import Path
from urllib.request import urlopen

TZDB = "2026f"
CLDR = "49.0.0"
CLDR_ROOT = f"https://raw.githubusercontent.com/unicode-org/cldr-json/{CLDR}"
ASSETS = Path(__file__).resolve().parents[1] / "Assets"

EXTRA_CITIES = {
    "Beijing": ("Asia/Shanghai", "CN", "Пекин", "Beijing"),
    "Delhi": ("Asia/Kolkata", "IN", "Нью-Дели", "New Delhi"),
    "Munich": ("Europe/Berlin", "DE", "Мюнхен", "Munich"),
    "Saint_Petersburg": ("Europe/Moscow", "RU", "Санкт-Петербург", "Saint Petersburg"),
    "San_Francisco": ("America/Los_Angeles", "US", "Сан-Франциско", "San Francisco"),
    "Seattle": ("America/Los_Angeles", "US", "Сиэтл", "Seattle"),
    "Vancouver": ("America/Vancouver", "CA", "Ванкувер", "Vancouver"),
}

def fetch(url):
    with urlopen(url, timeout=30) as response:
        return response.read()

def cldr(package, language, file):
    url = f"{CLDR_ROOT}/cldr-json/cldr-{package}-full/main/{language}/{file}.json"
    return json.loads(fetch(url))["main"][language]

def city_names(tree, prefix=""):
    for key, child in tree.items():
        if not isinstance(child, dict):
            continue
        zone = prefix + key
        if "exemplarCity" in child:
            yield zone, child["exemplarCity"]
        yield from city_names(child, zone + "/")

def main():
    archive = fetch(f"https://data.iana.org/time-zones/releases/tzdata{TZDB}.tar.gz")
    with tarfile.open(fileobj=io.BytesIO(archive)) as data:
        zones = data.extractfile("zone.tab").read().decode("utf-8").splitlines()
        links = {}
        for file in ("backward", "africa", "antarctica", "asia", "australasia", "europe", "northamerica", "southamerica"):
            for line in data.extractfile(file).read().decode("utf-8").splitlines():
                parts = line.split("#", 1)[0].split()
                if parts and parts[0] == "Link":
                    links[parts[2]] = parts[1]
    countries, names = {}, {}
    for language in ("ru", "en"):
        countries[language] = cldr("localenames", language, "territories")["localeDisplayNames"]["territories"]
        tree = cldr("dates", language, "timeZoneNames")["dates"]["timeZoneNames"]["zone"]
        names[language] = dict(city_names(tree))
        for alias, target in links.items():
            if alias in names[language]:
                names[language].setdefault(target, names[language][alias])
    places = {}
    territories = {}
    for line in zones:
        if not line or line.startswith("#"):
            continue
        country, _, zone, *_ = line.split("\t")
        default = zone.rsplit("/", 1)[1].replace("_", " ")
        places[zone] = {"Zone": zone, "Country": country, "Ru": names["ru"].get(zone, default),
                        "En": names["en"].get(zone, default)}
        labels = [countries[language][country] for language in ("ru", "en")]
        short = [countries[language].get(f"{country}-alt-short", labels[i]) for i, language in enumerate(("ru", "en"))]
        aliases = " ".join(dict.fromkeys(labels + short))
        territories[country] = {"Ru": short[0], "En": labels[1], "Aliases": aliases}
    for key, (zone, country, ru, en) in EXTRA_CITIES.items():
        if not any(row["Country"] == country and row["En"] == en for row in places.values()):
            places[key] = {"Zone": zone, "Country": country, "Ru": ru, "En": en}
    with (ASSETS / "time-zone-places.json").open("w", encoding="utf-8", newline="\n") as file:
        file.write("{\n")
        file.write(f' "Source": "IANA {TZDB}, Unicode CLDR {CLDR}",\n')
        for title, table in (("Countries", territories), ("Places", places)):
            file.write(f' "{title}": {{\n')
            file.write(",\n".join(f"  {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}"
                                 for key, value in sorted(table.items())))
            file.write("\n }" + ("," if title == "Countries" else "") + "\n")
        file.write("}\n")
    (ASSETS / "time-zone-places.LICENSE.txt").write_bytes(fetch(f"{CLDR_ROOT}/LICENSE"))
    print(f"{len(places)} places, {len(territories)} territories")

if __name__ == "__main__":
    main()
