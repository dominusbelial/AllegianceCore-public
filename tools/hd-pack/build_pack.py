#!/usr/bin/env python3
"""Build atlas-keyed HD packs; generated maps remain external to this MIT repo.

Requires Pillow. See README.md for input colour-space and provenance contracts.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile
from PIL import Image

MAP_SPACES = {"basecolor": "sRGB", "normal": "linear", "metallic": "linear",
              "roughness": "linear", "emission": "sRGB"}
STEM = re.compile(r"[A-Za-z0-9_-]+\Z")


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def build(source, artwork, output, catalog, mapping=None, expected_hashes=None):
    """Validate everything before publishing either the directory or zip."""
    source, artwork, output = map(Path, (source, artwork, output))
    archive = output.with_suffix(".zip")
    if output.exists() or archive.exists():
        raise ValueError("output directory or release zip already exists")
    if not catalog.get("models"):
        raise ValueError("coverage catalogue must list the core's models")
    referenced = sorted({s.lower() for m in catalog["models"] for s in m["stems"]})
    disk = {p.stem.lower(): p for p in artwork.iterdir() if p.suffix.lower() == ".mdl"}
    folders = mapping or {p.name: p.name for p in source.iterdir() if p.is_dir()}
    if not folders:
        raise ValueError("no input map sets")
    textures, inputs = {}, {}
    for folder, stem in sorted(folders.items()):
        if not STEM.fullmatch(folder) or not STEM.fullmatch(stem):
            raise ValueError("unsafe folder or atlas stem")
        stem = stem.lower()
        if stem in textures:
            raise ValueError("duplicate atlas mapping: " + stem)
        if stem not in disk:
            raise ValueError("native atlas missing: " + stem)
        native_hash = sha(disk[stem])
        if expected_hashes is not None and expected_hashes.get(stem, "").lower() != native_hash:
            raise ValueError("native SHA256 mismatch: " + stem)
        folder_path = source / folder
        metadata_path = folder_path / "maps.json"
        metadata = json.loads(metadata_path.read_text()) if metadata_path.exists() else {}
        # Untagged PNGs follow the documented ESRGAN convention. Explicit tags
        # must agree: colour maps sRGB, data maps linear. Never gamma-convert data.
        if metadata.get("colorSpaces", MAP_SPACES) != MAP_SPACES:
            raise ValueError("incorrect map colour spaces: " + stem)
        if "nativeSha256" in metadata and metadata["nativeSha256"].lower() != native_hash:
            raise ValueError("stale source atlas: " + stem)
        maps = {}
        for name, space in MAP_SPACES.items():
            path = folder_path / (name + ".png")
            with Image.open(path) as im:
                if im.format != "PNG" or im.mode not in ("RGB", "RGBA", "L"):
                    raise ValueError("maps must be RGB/RGBA/L PNG: " + str(path))
                w, h = im.size
                if not (1 <= w <= 8192 and 1 <= h <= 8192):
                    raise ValueError("map size out of range: " + str(path))
                if im.info.get("icc_profile"):
                    raise ValueError("ICC profiles unsupported; export explicit sRGB/linear: " + str(path))
                gamma = im.info.get("gamma")
                if space == "linear" and ("srgb" in im.info or gamma is not None and abs(gamma - 1) > .01):
                    raise ValueError("data map tagged nonlinear: " + str(path))
                if space == "sRGB" and gamma is not None and abs(gamma - .45455) > .01:
                    raise ValueError("colour map not sRGB: " + str(path))
                im.load()  # Detect truncated/corrupt image data, not just headers.
            maps[name] = {"width": w, "height": h, "colorSpace": space, "sha256": sha(path)}
        bw, bh = maps["basecolor"]["width"], maps["basecolor"]["height"]
        for name, meta in maps.items():
            # ESRGAN exports base/normal/emission at 4K and metal/rough at 2K.
            allowed = [(bw, bh)] + ([(bw // 2, bh // 2)] if name in ("metallic", "roughness") and bw % 2 == bh % 2 == 0 else [])
            if (meta["width"], meta["height"]) not in allowed:
                raise ValueError("inconsistent map dimensions: " + stem + "/" + name)
        textures[stem] = {"native": {"TextureSha256": native_hash}, "maps": maps}
        inputs[stem] = folder_path
    coverage = {"coreSha256": catalog.get("coreSha256"), "hdStems": sorted(textures),
                "referencedStems": referenced, "coveredStems": sorted(set(textures) & set(referenced)),
                "missingStems": sorted(set(referenced) - set(textures)),
                "unreferencedHdStems": sorted(set(textures) - set(referenced)), "models": catalog["models"]}
    manifest = {"schema": 2, "colorSpaces": MAP_SPACES, "textures": textures, "coverage": coverage}
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="hd-pack-", dir=output.parent) as scratch:
        stage = Path(scratch) / "pack"
        stage.mkdir()
        for stem, folder_path in inputs.items():
            target = stage / "textures" / stem
            target.mkdir(parents=True)
            for name in MAP_SPACES:
                shutil.copyfile(folder_path / (name + ".png"), target / (name + ".png"))
        (stage / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        (stage / "coverage.json").write_text(json.dumps(coverage, indent=2) + "\n")
        staged_zip = Path(scratch) / "release.zip"
        with zipfile.ZipFile(staged_zip, "w", zipfile.ZIP_DEFLATED) as z:
            for path in sorted(stage.rglob("*")):
                if path.is_file():
                    # Fixed timestamps make release zips reproducible.
                    info = zipfile.ZipInfo(str(path.relative_to(stage)), date_time=(1980, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    z.writestr(info, path.read_bytes())
        stage.rename(output)
        staged_zip.rename(archive)
    return coverage


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--artwork", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--core", type=Path, help="Generate coverage using the repository's native parsers")
    group.add_argument("--catalog", type=Path, help="Previously exported core model/stem catalogue JSON")
    parser.add_argument("--mapping", type=Path, help='JSON: {"model-folder": "native-atlas-stem"}')
    parser.add_argument("--expected-hashes", type=Path, help="JSON: atlas stem to source artwork SHA256")
    args = parser.parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix="hd-catalog-") as scratch:
            catalog_path = args.catalog or Path(scratch) / "catalog.json"
            if args.core:
                project = Path(__file__).parent / "catalog" / "catalog.csproj"
                subprocess.run(["dotnet", "run", "--project", str(project), "--", str(args.core), str(args.artwork), str(catalog_path)], check=True)
            coverage = build(args.input, args.artwork, args.output, json.loads(catalog_path.read_text()),
                             json.loads(args.mapping.read_text()) if args.mapping else None,
                             json.loads(args.expected_hashes.read_text()) if args.expected_hashes else None)
        print(json.dumps({"hd": len(coverage["hdStems"]), "covered": len(coverage["coveredStems"]),
                          "referenced": len(coverage["referencedStems"]), "output": str(args.output), "zip": str(args.output.with_suffix('.zip'))}))
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + "\n")


if __name__ == "__main__":
    main()
