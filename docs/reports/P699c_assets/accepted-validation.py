#!/usr/bin/env python3
"""Rebuild, then isolated triplets and full suites, sequentially, on final sources."""
import gzip, hashlib, json, os, pathlib, subprocess, sys, time
P = pathlib.Path(__file__).resolve().parent
ROOT = P.parents[2]
ENV = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/p699c-nuget')
started=time.time()
with (P/'accepted-build.log').open('w') as log:
    build=subprocess.run(['dotnet','build','AllegianceCore.slnx','--nologo'],cwd=ROOT,env=ENV,stdout=log,stderr=subprocess.STDOUT)
(P/'accepted-build.json').write_text(json.dumps({'exit':build.returncode,'started':started,'finished':time.time(),'command':'dotnet build AllegianceCore.slnx --nologo'},indent=2)+'\n')
assert build.returncode == 0
sources = subprocess.check_output(['git','diff','--name-only'],cwd=ROOT,text=True).splitlines()
sources += ['tests/Allegiance.Core.Tests/P699cStationCorpusTests.cs']
(P/'source-manifest.json').write_text(json.dumps([{'file':f,'sha256':hashlib.sha256((ROOT/f).read_bytes()).hexdigest()} for f in sorted(set(sources)) if f.endswith(('.cs','.csproj'))],indent=2)+'\n')
binaries = ['src/Allegiance.ClientCore.Explorer/bin/Debug/net10.0/Allegiance.ClientCore.Explorer.dll', 'src/Allegiance.ClientCore.Game/bin/Debug/net10.0/Allegiance.ClientCore.Game.dll', 'src/Allegiance.ClientCore/bin/Debug/net10.0/Allegiance.ClientCore.dll', 'src/Allegiance.Server/bin/Debug/net10.0/Allegiance.Server.dll', 'src/Allegiance.Core/bin/Debug/net10.0/Allegiance.Core.dll', 'src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.dll', 'src/Allegiance.Bot/bin/Debug/net10.0/Allegiance.Bot.dll', 'src/Allegiance.Lobby/bin/Debug/net10.0/Allegiance.Lobby.dll', 'tests/Allegiance.ClientCore.Tests/bin/Debug/net10.0/Allegiance.ClientCore.Tests.dll', 'tests/Allegiance.ClientCore.MonoGame.Tests/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.Tests.dll', 'tests/Allegiance.Lobby.Tests/bin/Debug/net10.0/Allegiance.Lobby.Tests.dll', 'tests/Allegiance.Core.Tests/bin/Debug/net10.0/Allegiance.Core.Tests.dll', 'tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp.dll']
(P/'accepted-binaries.json').write_text(json.dumps({f:hashlib.sha256((ROOT/f).read_bytes()).hexdigest() for f in binaries},indent=2)+'\n')
for phase,args in [('accepted-focused',[]),('isolate-accepted-extra',[str(P/'aligned-extra-methods.json')]),('accepted-suites',[])]:
    result=subprocess.run([sys.executable,str(P/'validate.py'),phase,*args],cwd=ROOT,env=ENV)
    if phase != 'accepted-suites': assert result.returncode == 0, phase+' failed'
suites=json.loads((P/'accepted-suites-results.json').read_text())
failures=sorted({(r['project'],f['method']) for r in suites for f in r['failed']})
if failures:
    (P/'accepted-suite-failure-methods.json').write_text(json.dumps(failures,indent=2)+'\n')
    subprocess.run([sys.executable,str(P/'validate.py'),'isolate-accepted-suite-failures',str(P/'accepted-suite-failure-methods.json')],cwd=ROOT,env=ENV,check=True)
subprocess.run([sys.executable,str(P/'verify.py')],cwd=ROOT,env=ENV,check=True)
subprocess.run([sys.executable,str(P/'summarize-flights.py')],cwd=ROOT,env=ENV,check=True)
print('Final source and binary audit passed.',flush=True)
