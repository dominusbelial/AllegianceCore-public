#!/usr/bin/env python3
"""Build atlas-keyed HD packs; generated maps remain external to this MIT repo.

Requires Pillow. See README.md for input colour-space and provenance contracts.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile
from concurrent.futures import ThreadPoolExecutor
import sys
import bc_maps
from PIL import Image

MAP_SPACES = {"basecolor": "sRGB", "normal": "linear", "metallic": "linear",
              "roughness": "linear", "emission": "sRGB"}
STEM = re.compile(r"[A-Za-z0-9_-]+\Z")


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def release_zip(stage, archive, manifest=None, bc3=False):
    """One complete codec family per download; BC3 is a standalone alternative."""
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(stage.rglob("*")):
            if not path.is_file():
                continue
            fallback = path.name.endswith(".fallback.dds")
            if fallback and not bc3:
                continue
            if bc3 and path.suffix == ".dds" and path.stem in ("basecolor", "emission"):
                continue
            relative = path.relative_to(stage).as_posix().replace(".fallback.dds", ".dds")
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            data = path.read_bytes()
            if relative == "manifest.json" and manifest is not None:
                alternate = copy.deepcopy(manifest)
                for entry in alternate["textures"].values():
                    for name in ("basecolor", "emission"):
                        meta = entry["maps"][name]
                        meta["format"] = "BC3"
                        meta["sha256"] = meta.pop("fallbackSha256")
                data = (json.dumps(alternate, indent=2) + "\n").encode()
            z.writestr(info, data)


def build(source, artwork, output, catalog, mapping=None, expected_hashes=None, format="png", policy=None, workers=2):
    """Validate everything before publishing either the directory or zip."""
    source, artwork, output = map(Path, (source, artwork, output))
    if format not in ("png", "bc"):
        raise ValueError("unsupported format")
    if not 1 <= workers <= 4:
        raise ValueError("workers must be between 1 and 4")
    policy = dict(bc_maps.DEFAULT_POLICY, **(policy or {}))
    if set(policy) != set(bc_maps.DEFAULT_POLICY) or any(type(v) is not int or v < 4 or v > 4096 or v & (v-1) for v in policy.values()):
        raise ValueError("resolution policy must contain power-of-two limits from 4 to 4096")
    archive = output.with_suffix(".zip")
    fallback_archive = output.with_suffix(".bc3.zip")
    if output.exists() or archive.exists() or format == "bc" and fallback_archive.exists():
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
                if format == "png":
                    im.load()  # Authoring copies must also decode successfully.
                else:
                    im.verify()  # CRCs now; full decode happens in the staged encoder.
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
    manifest = {"schema": 3 if format == "bc" else 2, "colorSpaces": MAP_SPACES, "textures": textures, "coverage": coverage}
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="hd-pack-", dir=output.parent) as scratch:
        stage = Path(scratch) / "pack"
        stage.mkdir()
        sizes = {}
        def encode_atlas(item):
            stem, folder_path = item
            target = stage / "textures" / stem
            target.mkdir(parents=True)
            cls = bc_maps.atlas_class(stem, catalog, policy) if format == "bc" else "small"
            resident = 0
            for name, space in MAP_SPACES.items():
                if format == "png":
                    shutil.copyfile(folder_path / (name + ".png"), target / (name + ".png"))
                    continue
                limit = policy[cls] if name == "basecolor" else max(4, policy[cls] // 2)
                bc = 7 if space == "sRGB" else 5 if name == "normal" else 4
                path = target / (name + ".dds")
                with Image.open(folder_path / (name + ".png")) as image:
                    meta = bc_maps.write_dds(image, path, name, limit, bc)
                    if bc == 7:
                        fallback = target / (name + ".fallback.dds")
                        bc_maps.write_dds(image, fallback, name, limit, 3)
                        meta["fallbackSha256"] = sha(fallback)
                meta.update(colorSpace=space, sha256=sha(path))
                textures[stem]["maps"][name] = meta
                resident += meta["residentBytes"]
            if format == "bc":
                textures[stem].update(modelClass=cls, residentBytes=resident)
                print("encoded " + stem + " (" + cls + ")", file=sys.stderr, flush=True)
            return cls, resident, sum(p.stat().st_size for p in target.iterdir())
        with ThreadPoolExecutor(max_workers=workers if format == "bc" else 1) as pool:
            for cls, resident, file_bytes in pool.map(encode_atlas, inputs.items()):
                row = sizes.setdefault(cls, {"materials": 0, "residentBytes": 0, "fileBytes": 0})
                row["materials"] += 1; row["residentBytes"] += resident
                row["fileBytes"] += file_bytes
        if format == "bc":
            manifest["resolutionPolicy"] = policy
            manifest["sizesByClass"] = sizes
        (stage / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        (stage / "coverage.json").write_text(json.dumps(coverage, indent=2) + "\n")
        staged_zip = Path(scratch) / "release.zip"
        release_zip(stage, staged_zip)
        if format == "bc":
            release_zip(stage, Path(scratch) / "bc3.zip", manifest, bc3=True)
        stage.rename(output)
        staged_zip.rename(archive)
        if format == "bc":
            (Path(scratch) / "bc3.zip").rename(fallback_archive)
    if format == "bc":
        coverage["sizeReport"] = {"zipBytes": archive.stat().st_size, "bc3ZipBytes": fallback_archive.stat().st_size, "classes": sizes,
                                  "maxResidentBytes": max(v["residentBytes"] for v in textures.values())}
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
    parser.add_argument("--format", choices=("png", "bc"), default="png")
    parser.add_argument("--resolution-policy", type=Path, help="JSON capital/station/small/projectile maximum dimensions")
    parser.add_argument("--workers", type=int, default=2, help="Concurrent atlas encodes (1..4, default 2)")
    args = parser.parse_args()
    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="hd-catalog-", dir=args.output.parent) as scratch:
            catalog_path = args.catalog or Path(scratch) / "catalog.json"
            if args.core:
                project = Path(__file__).parent / "catalog" / "catalog.csproj"
                subprocess.run(["dotnet", "run", "--project", str(project), "--", str(args.core), str(args.artwork), str(catalog_path)], check=True)
            coverage = build(args.input, args.artwork, args.output, json.loads(catalog_path.read_text()),
                             json.loads(args.mapping.read_text()) if args.mapping else None,
                             json.loads(args.expected_hashes.read_text()) if args.expected_hashes else None, args.format,
                             json.loads(args.resolution_policy.read_text()) if args.resolution_policy else None, args.workers)
        print(json.dumps({"sizes": coverage.get("sizeReport"), "hd": len(coverage["hdStems"]), "covered": len(coverage["coveredStems"]),
                          "referenced": len(coverage["referencedStems"]), "output": str(args.output), "zip": str(args.output.with_suffix('.zip'))}))
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + "\n")


if __name__ == "__main__":
    main()
