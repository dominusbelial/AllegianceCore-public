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
