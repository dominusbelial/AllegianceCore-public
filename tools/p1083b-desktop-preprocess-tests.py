#!/usr/bin/env python3
"""Lock the desktop-preprocessed shaders to the pre-P1083B sources."""
import hashlib
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = {
    'DisplacedSphere': '209e3ad19cff7020dcd2dc80588f7d2ef2b50fc8ba5026b85fbe9328ddbd40bb',
    'SoftParticle': '73571f8a83172d431a6e0ab142712704703dfb02a266c4991e2cc1fcaeca5f9c',
    'Volumetric': '0ae0bd724840fc024159867ed293752d680c0725978602cd6af52d9c468265f7',
}

class DesktopPreprocessing(unittest.TestCase):
    def test_desktop_sources_match_original_preprocessing(self):
        for name, digest in EXPECTED.items():
            with self.subTest(shader=name):
                source = (ROOT / 'src/Allegiance.ClientCore.Game/Shaders' / (name + '.fx')).read_bytes()
                output = subprocess.run(['cpp', '-P', '-DOPENGL=1', '-x', 'c', '-'],
                                        input=source, check=True, capture_output=True).stdout
                self.assertEqual(digest, hashlib.sha256(output).hexdigest())

if __name__ == '__main__':
    unittest.main()
