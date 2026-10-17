#!/usr/bin/env python3
"""Audit file resolutions without consulting git."""
from pathlib import Path
import json,re,hashlib
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
conflicts=json.loads((OUT/'conflicts-before.json').read_text())
conflicts={p:b for p,b in conflicts.items() if b}
notes={
 'ClientSoundEvent.cs':'Keep all master P717/P719 event kinds.',
 'Game1.Audio.cs':'Keep all master P717/P719 playback cases, with current P732 voice handling.',
 'ClientWorldSoundEventTests.cs':'Keep all 202 lines of additional master sound regressions.',
 'SetWingId.cs':'Keep current commander authorization documentation; use existing shared WingNames table instead of adding another.',
 'ClientWorld.MissionEntry.cs':'Keep known same-side bounded authoritative wing updates; notify commanded self using launch or joined identity.',
 'Game1.Rendering.cs':'Keep entire roster-gathering gate lock, commander/AFK metadata, teammate-only wing data, compact layout and controls hints.',
 'TeamPaneLayout.cs':'Keep commander/ready/wing fields; show incoming unknown-wing marker; use shared WingNames.',
 'TeamPaneLayoutTests.cs':'Union commander/AFK tests, unknown-wing assertion and three named-wing cases with named WingId argument.',
 'P675CommandActionTests.cs':'Keep master same-side PlayerInfo/JoinSide fixture for current identity contract.',
 'parity-matrix.csv':'Resolve by row: seven newly done FEDMSG rows; union P713/P718 SET_WINGID evidence; preserve master P713 screen/lobby evidence and selection-pane exclusion.',
 'sim-rules-matrix.csv':'Keep completed master P717/P719/P732 sounds rows; no simulation regression.',
 'TEST_FLAKES.md':'Union master chain entries and incoming P718 entry.',
 'P716_PARITY_WAVE16.md':'Keep master report verbatim after removing markers.',
}
audit={'merge_target':'codex/p718','conflicted_files':len(conflicts),'resolutions':[{'file':p,'blocks':len(b),'resolution':notes[Path(p).name]} for p,b in conflicts.items()],
 'automatic_merge_repairs':[
 'ConnectionSession.cs: deduplicate AsteroidDestroyed dispatch (86).',
 'ClientWorld.cs: deduplicate AsteroidDestroyed switch; use gated P718 handler with sector validation and explode/conversion distinction.',
 'P703ResearchCommanderTests.cs: feed ENTER_GAME before live asteroid destruction.',
 'P718InterfaceMessageTests.cs: add prelaunch commanded-self wing-notice regression.'
 ],'excluded_suite_tests':{'reason':'Both invoke git ls-files; user prohibits git commands. No live or loopback exclusions.', 'methods':['MeshyGuardTests.NoTrackedMeshyOutputRemainsAnywhere','PrivatePathGuardTests.NoTrackedFileUnderSourceDirsContainsAnAbsoluteHomePath']}}
pattern=re.compile(r'<<<<<<<[^\n]*\n(.*?)=======\n(.*?)>>>>>>>[^\n]*\n',re.S)
for p in conflicts:
 assert not pattern.search((ROOT/p).read_text()),p
# The two sides of every original conflict are durably retained in conflicts-before.json.
for p,blocks in conflicts.items():
 if Path(p).name in ['ClientSoundEvent.cs','Game1.Audio.cs','ClientWorldSoundEventTests.cs','P716_PARITY_WAVE16.md']:
  final=(ROOT/p).read_text()
  assert all(b['ours'] in final for b in blocks),p
p=ROOT/'src/Allegiance.ClientCore/State/ClientSoundEvent.cs'
enum=p.read_text().split('public enum ClientSoundEventKind',1)[1].split('}',1)[0]
kinds=re.findall(r'^    ([A-Za-z]\w*)(?:\s*=\s*\d+)?,',enum,re.M)
a=(ROOT/'src/Allegiance.ClientCore.Game/Game1.Audio.cs').read_text();cases=re.findall(r'case ClientSoundEventKind\.(\w+)\s*:',a)
assert len(set(kinds))==len(kinds) and len(set(cases))==len(cases) and set(kinds)==set(cases)
(OUT/'cue-audit.json').write_text(json.dumps({'kinds':kinds,'playback_cases':cases,'one_case_per_kind':True},indent=2)+'\n')
files=sorted(p for root in ['src','tests'] for p in (ROOT/root).rglob('*') if p.is_file() and p.suffix in ['.cs','.csproj'] and not {'bin','obj'}&set(p.parts))
(OUT/'source-fingerprints.json').write_text(json.dumps({str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files},indent=2)+'\n')
for p in ['docs/parity/parity-matrix.csv','docs/parity/sim-rules-matrix.csv','docs/parity/fedmsg-wiring.csv']:
 assert b'\r' not in (ROOT/p).read_bytes(),p
(OUT/'resolution-audit.json').write_text(json.dumps(audit,indent=2)+'\n')
print('Audited',len(conflicts),'conflicted files and',len(kinds),'unique sound event/playback pairs; matrices use LF.')
