import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('player_package', Path(__file__).with_name('package.py'))
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class PackagingTests(unittest.TestCase):
    def test_manifest_rejects_unsafe_duplicate_and_empty_entries(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest = Path(temp) / 'manifest.txt'
            for text in ('../escape', '/absolute', 'C:/windows', 'folder\\file', 'x\nx', '# only comment'):
                with self.subTest(text=text):
                    manifest.write_text(text)
                    with self.assertRaises(ValueError):
                        package.paths(manifest)
            manifest.write_text('# comment\n\nCaseSensitive.mdl\nfolder/asset.png\n')
            self.assertEqual(['CaseSensitive.mdl', 'folder/asset.png'], package.paths(manifest))

    def test_copy_never_includes_untracked_or_missing_assets(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'local.png').write_text('private')
            for name, tracked in [('local.png', set()), ('missing.png', {'missing.png'})]:
                with self.assertRaises(ValueError):
                    package.copy(root, name, root / 'bundle', tracked)
            package.copy(root, 'local.png', root / 'bundle', {'local.png'})
            self.assertEqual('private', (root / 'bundle/local.png').read_text())

    def test_local_override_must_match_pin(self):
        with patch.object(package.subprocess, 'check_output', return_value='wrong\n'):
            with self.assertRaisesRegex(ValueError, 'must be at'):
                package.source('artwork', Path('/tmp'), Path('/tmp/cache'),
                               {'artwork': {'commit': 'expected'}})



if __name__ == '__main__':
    unittest.main()
