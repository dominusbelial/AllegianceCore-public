#!/usr/bin/env python3
"""Build the P589 sky-objects manifest (JSON) + copy provenance sources."""
import json, os, shutil

BASE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "reports", "P589_assets"))
SRC_DIR = "/tmp/spaceobj_src"
SRC_OUT = os.path.join(BASE, "sources")
os.makedirs(SRC_OUT, exist_ok=True)
os.makedirs(os.path.join(BASE, "licenses"), exist_ok=True)

MANIFEST = [
    dict(
        slug="helix-nebula", subject="Helix Nebula (NGC 7293)",
        source="NOIRLab release noao0307, image noao0307a",
        source_url="https://storage.noirlab.edu/media/archives/images/large/noao0307a.jpg",
        source_file="helix_noao0307a.jpg",
        licence="CC BY 4.0",
        credit="NASA, NOAO, ESA, the Hubble Helix Nebula Team, M. Meixner (STScI), and T.A. Rector (University of Alaska Anchorage/NSF NOIRLab)",
    ),
    dict(
        slug="whirlpool-m51", subject="Whirlpool Galaxy (M51 / NGC 5194)",
        source="ESA/Hubble release heic0506, image heic0506a",
        source_url="https://cdn.esahubble.org/archives/images/large/heic0506a.jpg",
        source_file="heic0506a.jpg",
        licence="CC BY 4.0",
        credit="NASA, ESA, S. Beckwith (STScI), and The Hubble Heritage Team (STScI/AURA)",
    ),
    dict(
        slug="ring-nebula-m57", subject="Ring Nebula (M57 / NGC 6720)",
        source="ESA/Hubble release heic1310, image heic1310a",
        source_url="https://cdn.esahubble.org/archives/images/large/heic1310a.jpg",
        source_file="heic1310a.jpg",
        licence="CC BY 4.0",
        credit="NASA, ESA, and C. Robert O'Dell (Vanderbilt University)",
    ),
    dict(
        slug="pinwheel-m101", subject="Pinwheel Galaxy (M101 / NGC 5457)",
        source="ESA/Hubble release heic0602, image heic0602a",
        source_url="https://cdn.esahubble.org/archives/images/large/heic0602a.jpg",
        source_file="heic0602a.jpg",
        licence="CC BY 4.0",
        credit="NASA, ESA, K. Kuntz (JHU), F. Bresolin (University of Hawaii), J. Trauger (Jet Propulsion Lab), J. Mould (National Australian Observatory), Y.-H. Chu (University of Illinois, Urbana), and STScI",
    ),
    dict(
        slug="lion-nebula-miri", subject="Lion/Eskimo Nebula (NGC 2392), JWST MIRI image",
        source="NASA Science asset 'Lion Nebula (MIRI Image)'",
        source_url="https://science.nasa.gov/asset/webb/lion-nebula-miri-image/",
        source_file="lion_webb_miri.png",
        licence="CC BY 4.0",
        credit="Image: NASA, ESA, CSA, STScI; Image Processing: Alyssa Pagan (STScI)",
    ),
    dict(
        slug="lion-nebula-nasa", subject="Lion/Eskimo Nebula (NGC 2392), Chandra X-ray + Hubble optical composite",
        source="NASA image 'A Beautiful End to a Star's Life'",
        source_url="https://www.nasa.gov/image-article/beautiful-end-stars-life/",
        source_file="lion_nasa_ngc2392.jpg",
        licence="Public Domain (US Government Work)",
        credit="X-ray: NASA/CXC/SAO; Optical: NASA/STScI",
    ),
]

with open(os.path.join(BASE, "sky-objects.json"), "w") as f:
    json.dump(MANIFEST, f, indent=2, ensure_ascii=False)
    f.write("\n")

for entry in MANIFEST:
    src = os.path.join(SRC_DIR, entry["source_file"])
    dst = os.path.join(SRC_OUT, entry["source_file"])
    if os.path.exists(src) and not os.path.exists(dst):
        shutil.copy2(src, dst)

print("manifest written:", len(MANIFEST), "entries")
