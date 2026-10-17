#!/usr/bin/env python3
"""Build the entry revision separately, without replacing working-tree files."""
import hashlib, io, json, os, subprocess, tarfile
from pathlib import Path
out = Path(__file__).resolve().parent
root = out.parents[2]
os.chdir(root)
revision = json.loads((out / 'source-manifest.json').read_text())['base']
baseline = Path('/tmp/p708-entry-baseline')
baseline.mkdir(exist_ok=True)
paths = ['Directory.Build.props', 'src/Allegiance.Core', 'src/Allegiance.Server', 'src/Allegiance.ClientCore',
         'src/Allegiance.Bot', 'tools/allegiance-mcp', 'tests/Allegiance.ClientCore.Tests', 'tests/_shared', 'data']
archive = subprocess.check_output(['git', 'archive', revision, *paths])
with tarfile.open(fileobj=io.BytesIO(archive)) as tar: tar.extractall(baseline, filter='data')
os.environ['NUGET_HTTP_CACHE_PATH'] = '/tmp/p708-nuget-http'
with (out / 'baseline-build.log').open('w') as log:
    subprocess.run(['dotnet', 'build', str(baseline / 'tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj'),
        '-m:8', '-v:q'], stdout=log, stderr=subprocess.STDOUT, check=True)
files = ['src/Allegiance.Core/Sim/Ship.cs', 'src/Allegiance.Core/Sim/ShipControlState.cs', 'src/Allegiance.Server/Server.ShipCommands.cs']
manifest = {'revision': revision, 'root': str(baseline), 'source': {p: hashlib.sha256((baseline / p).read_bytes()).hexdigest() for p in files}}
manifest['matches_entry_revision'] = all(hashlib.sha256(subprocess.check_output(['git', 'show', revision + ':' + p])).hexdigest() == h for p,h in manifest['source'].items())
assert manifest['matches_entry_revision']
(out / 'baseline-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
