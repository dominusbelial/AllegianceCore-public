#!/usr/bin/env python3
"""Refresh P701's independent MDLX snapshot using the original Python reader."""
import argparse
import csv
import json
import os
from pathlib import Path
import struct
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--reference-reader', required=True, help='directory containing mdl_parse.py')
parser.add_argument('--artwork', default=os.environ.get('ALLEGIANCE_ARTWORK_PATH'),
                    required=not os.environ.get('ALLEGIANCE_ARTWORK_PATH'))
args = parser.parse_args()
sys.path.insert(0, args.reference_reader)
from mdl_parse import MdlFile  # original independent MDLX stack reader

repo = Path(__file__).resolve().parents[2]


class BinaryAudit(MdlFile):
    def _read_binary(self, function, stack):
        if self._fn_name(function) == 'LightsGeo':
            count = self.r.dword()
            lights = [struct.unpack('<12f', self.r.bytes_(48)) for _ in range(count)]
            return {'type': 'LightsGeo', 'lights': lights}
        return super()._read_binary(function, stack)

    def _apply(self, function, stack):
        if self._fn_name(function) == 'LODGeo':
            base, levels = stack.pop(), stack.pop()
            return {'type': 'LODGeo', 'base': base, 'levels': levels}
        return super()._apply(function, stack)

    def _collect_meshes(self, node):
        # Walk scopes ourselves after the original reader resolves the graph.
        pass


def groups_in(node, texture=None):
    if isinstance(node, list):
        for child in node:
            yield from groups_in(child, texture)
    elif isinstance(node, dict):
        kind = node['type']
        if kind == 'TextureGeo':
            yield from groups_in(node['geo'], node['image'][1])
        elif kind == 'LODGeo':
            yield from groups_in(node['base'], texture)
        elif kind == 'GroupGeo':
            yield from groups_in(node['children'], texture)
        elif kind == 'MeshGeo':
            yield {'texture': texture, 'vertices': len(node['vertices']),
                   'triangles': len(node['indices']) // 3}
        else:
            raise ValueError(f'Unrecognized drawable node: {kind}')


def positions_in(node):
    if isinstance(node, (list, tuple)):
        for child in node:
            yield from positions_in(child)
    elif isinstance(node, dict):
        if node['type'] == 'MeshGeo':
            for vertex in node['vertices']:
                yield vertex[:3]
        elif node['type'] == 'LODGeo':
            yield from positions_in(node['base'])
            yield from positions_in(node['levels'])
        elif node['type'] == 'GroupGeo':
            yield from positions_in(node['children'])
        elif node['type'] == 'TextureGeo':
            yield from positions_in(node['geo'])


with (repo / 'docs/reports/P615_assets/stations.csv').open() as source:
    stems = sorted({row['model'] for row in csv.DictReader(source)})
models = {}
for stem in stems:
    mdl = BinaryAudit(str(Path(args.artwork) / (stem + '.mdl')))
    groups = list(groups_in(mdl.objects[mdl.export_names.index('object')]))
    lights = mdl.objects[mdl.export_names.index('lights')]['lights'] if 'lights' in mdl.export_names else []
    points = positions_in(mdl.objects[mdl.export_names.index('object')])
    radius = max(sum(v * v for v in point) for point in points) ** 0.5
    models[stem] = {'groups': groups, 'lights': lights, 'frames': getattr(mdl, 'frames', []), 'radius': radius}

snapshot = repo / 'tests/Allegiance.ClientCore.MonoGame.Tests/Fixtures/P701StationModels.json'
snapshot.write_text('{\n' + ',\n'.join(
    '  ' + json.dumps(stem) + ': ' + json.dumps(model, separators=(',', ':'))
    for stem, model in models.items()) + '\n}\n')
with (repo / 'docs/reports/P701_assets/station-groups.csv').open('w', newline='') as output:
    writer = csv.writer(output)
    writer.writerow(['model', 'group', 'texture', 'vertices', 'triangles', 'lights'])
    for stem, model in models.items():
        for number, group in enumerate(model['groups']):
            writer.writerow([stem, number, group['texture'] or '(inherit)',
                             group['vertices'], group['triangles'], len(model['lights'])])
print(f'{len(models)} models, {sum(len(m["groups"]) for m in models.values())} groups, '
      f'{sum(len(m["lights"]) for m in models.values())} native lights')

with (repo / 'docs/reports/P701_assets/station-radii.csv').open('w', newline='') as output:
    writer = csv.writer(output)
    writer.writerow(['model', 'native_origin_radius_including_lods'])
    writer.writerows((stem, model['radius']) for stem, model in models.items())
