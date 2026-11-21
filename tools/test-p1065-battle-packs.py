#!/usr/bin/env python3
"""Offline generator/deploy guard regressions, standard library only."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


packs = module('battle_packs', 'p1065-battle-packs.py')
deploy = module('deploy_check', 'p1061-deploy-check.py')


def core():
    hull = bytearray(540)
    hull[8:12] = b'hull'
    hull[496:499] = b'alt'
    struct.pack_into('<h', hull, 446, 7)
    missile = bytearray(524)
    missile[24:31] = b'missile'
    missile[444] = 33  # defenseType is NOT the missile ID
    struct.pack_into('<h', missile, 446, 19)
    body = struct.pack('<hi', 29, len(hull)) + hull + struct.pack('<hi', 23, len(missile)) + missile
    return struct.pack('<ii', 1337, len(body)) + body


def namespaces(*names):
    result = bytearray(struct.pack('<6I', 0xDEBADF00, 0, len(names), 0, 0, 0))
    for name in names:
        result.extend(name.encode() + b'\0')
        while len(result) % 4:
            result.append(0)
    return bytes(result)


class BattlePacksTests(unittest.TestCase):
    def test_core_offsets_and_truncation(self):
        roots = packs.core_roots(core())
        self.assertEqual(roots[0], dict(key='hull-7', model='hull', texture='alt'))
        self.assertEqual(roots[1]['key'], 'missile-19')
        with self.assertRaises(ValueError):
            packs.core_roots(core()[:-1])
        corrupt = bytearray(core())
        struct.pack_into('<i', corrupt, 10, 100000)
        with self.assertRaises(ValueError):
            packs.core_roots(corrupt)

    def test_complete_original_closures_and_join_stays_byte_identical(self):
        with tempfile.TemporaryDirectory(prefix='p1065-packs-') as directory:
            root = Path(directory) / 'generation'
            art = Path(directory) / 'art'
            root.mkdir(); art.mkdir(); (root / 'packs').mkdir()
            (art / 'PCore001.igc').write_bytes(core())
            (art / 'Hull.mdl').write_bytes(namespaces('sharedbmp'))
            (art / 'missile.mdl').write_bytes(namespaces('sharedbmp'))
            (art / 'sharedbmp.mdl').write_text('ImportImageFromFile("texture.png")')
            (art / 'texture.png').write_bytes(b'fixture image')
            (art / 'alt.mdl').write_bytes(b'fixture hull override')
            digest = hashlib.sha256(core()).hexdigest()
            asset = dict(source='pcore001.igc', path='packs/' + digest, sha256=digest, byteSize=len(core()), dependencies=[])
            (root / asset['path']).write_bytes(core())
            import gzip
            (root / (asset['path'] + '.gz')).write_bytes(gzip.compress(core(), 9, mtime=0))
            join = json.dumps(dict(version=2, assets=[asset]), indent=2).encode()
            (root / 'join-manifest.json').write_bytes(join)
            receipt = packs.build(root, art)
            self.assertTrue(receipt['joinUnchanged'])
            self.assertEqual(join, (root / 'join-manifest.json').read_bytes())
            manifest = json.loads((root / 'battle-manifest.json').read_text())
            hull = next(p for p in manifest['packs'] if p['key'] == 'hull-7')
            missile = next(p for p in manifest['packs'] if p['key'] == 'missile-19')
            self.assertEqual(set(hull['sources']), {'hull.mdl', 'sharedbmp.mdl', 'texture.png', 'alt.mdl'})
            self.assertEqual(set(missile['sources']), {'missile.mdl', 'sharedbmp.mdl', 'texture.png'})
            for entry in manifest['assets']:
                self.assertEqual(entry['sha256'], hashlib.sha256((root / entry['path']).read_bytes()).hexdigest())
                self.assertEqual(entry['byteSize'], (root / entry['path']).stat().st_size)
            original = (root / 'battle-manifest.json').read_bytes()
            packs.build(root, art)
            self.assertEqual(original, (root / 'battle-manifest.json').read_bytes())
            # A case alias may not silently replace different visual bytes.
            (art / 'hull.mdl').write_bytes(b'conflict')
            with self.assertRaises(ValueError):
                packs.build(root, art)

    def test_public_deployment_rejects_every_fixture_entry_point(self):
        for text in ('--bots 10', '--bots=10', '--LOGON-FILE private.json', '{"BrokerDirectory":"tmp"}', 'p1051-browser-host', 'identity-broker'):
            self.assertTrue(deploy.has_proof_fixture(text), text)
        self.assertFalse(deploy.has_proof_fixture('{"identity":{"allowAnonymous":false}}'))


if __name__ == '__main__':
    unittest.main()
