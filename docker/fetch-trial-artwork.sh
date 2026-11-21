#!/bin/sh
# Public native assets, pinned and checksum-verified; never download private art.
set -eu
mkdir -p /artwork
base=https://raw.githubusercontent.com/FreeAllegiance/Artwork/4a60aa636405de66bca6bf48bff99c25a16a2ca3
fetch() {
  name=$1
  digest=$2
  if [ -f "/artwork/$name" ] && echo "$digest  /artwork/$name" | sha256sum -c - >/dev/null 2>&1; then return; fi
  wget -q "$base/$name" -O "/artwork/$name.download"
  echo "$digest  /artwork/$name.download" | sha256sum -c -
  mv "/artwork/$name.download" "/artwork/$name"
}
fetch PCore001.igc f73aa7bbce7414140f6691366f8829ea58829727660a8a86c114b29a83a55de8
fetch XXV_simple.igc 84c06bc649682f8fd4a96aa7efa6ba49bcd408b75442e352a49c2922802c7b71
# The browser generation's asset-manifest.json (tools/p1047-browser-packs.py) is served
# straight from this corpus; the lobby refuses to start if these are missing.
fetch fig05.mdl 121004a915b39ec5fa076371d833d21b5444cbdb78877e777d37b50a4ca4f5f1
fetch fig05bmp.mdl 46144898ffaca2a30429284f64f7eedb906d3afcdaa45954552cac62e9411870
