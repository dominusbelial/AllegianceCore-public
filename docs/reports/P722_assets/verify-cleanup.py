#!/usr/bin/env python3
"""P722 cleanup-entry compatibility and boot/self-quit exact-method triplets."""
import importlib.util
import sys
from pathlib import Path
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('verify', Path(__file__).with_name('verify.py'))
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
v.LEDGER = Path(__file__).with_name('cleanup-results.json')
filters = {
 'Core': ['FullyQualifiedName~P655DuplicateIdentityTests.Disconnect_releases_pending_or_seated_name',
          'FullyQualifiedName~ConnectionCacheCleanupTests', 'FullyQualifiedName~LiveLogoffTests'],
 'ClientCore': ['FullyQualifiedName~P722InterfaceLoopbackTests.Leader_boot',
                'FullyQualifiedName~P722InterfaceLoopbackTests.Self_quit'],
}
import subprocess
index = 0
for project, filters_ in filters.items():
 listing = subprocess.check_output(['dotnet', 'test', str(v.projects[project]), '--no-build', '--nologo', '--list-tests', '--filter', '|'.join(filters_)], text=True, cwd=v.ROOT)
 methods = sorted({s.strip().split('(')[0] for s in listing.splitlines() if s.strip().startswith('Allegiance.')})
 for method in methods:
  index += 1
  for repeat in (1, 2, 3):
   v.run(f'cleanup-{index:02d}-{repeat}', v.projects[project], 'FullyQualifiedName=' + method)
