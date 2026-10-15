"""Refresh P679 measurement tables from completed final JSON artifacts."""
import json, pathlib, statistics as st
assets=pathlib.Path(__file__).resolve().parent
report=assets.parent/'P679_PIPELINE_COST.md'
M=1048576
before={n:json.loads((assets/f'before-{n}.json').read_text()) for n in [256,512]}
after={n:[json.loads((assets/f'after-{n}-{i}.json').read_text()) for i in [1,2,3]] for n in [256,512]}
def med(n,i,key): return st.median(r[i][key] for r in after[n])
labels=['off','P668 + P669','P668 + P669 + tiers']
lines=['| Peers | Flags | Producer before mean / p95 ms | Producer after mean / p95 ms | Publish before / after mean ms |',
       '| --- | --- | --- | --- | --- |']
for n in [256,512]:
 for i,label in enumerate(labels):
  b=before[n][i]
  lines.append(f'| {n} | {label} | {b["tickMeanMs"]:.2f} / {b["tickP95Ms"]:.2f} | {med(n,i,"tickMeanMs"):.2f} / {med(n,i,"tickP95Ms"):.2f} | {b["publishMeanMs"]:.2f} / {med(n,i,"publishMeanMs"):.2f} |')
lines+=['','| Peers | Flags | Producer before → after MiB/tick | Publish before → after MiB/tick |', '| --- | --- | --- | --- |']
for n in [256,512]:
 for i,label in enumerate(labels):
  b=before[n][i]
  lines.append(f'| {n} | {label} | {b["simAllocatedBytesPerTick"]/M:.4f} → {med(n,i,"simAllocatedBytesPerTick")/M:.4f} | {b["poolAllocatedBytesPerTick"]/M:.4f} → {med(n,i,"poolAllocatedBytesPerTick")/M:.4f} |')
lines+=['','Off-path improvements are shared: transport frame/node reuse, audience reuse,',
        'lazy cross-side status construction and the roster deadline check also reduce',
        'the final flags-off cost. Snapshot/encode-once without tiers retains the',
        'legacy per-viewer ranked/floor pose representation.','',
        '| Peers | TCP off / tiers before MiB | TCP off / tiers after MiB | After paired TCP reduction | Combined all-on allocation MiB/tick |',
        '| --- | --- | --- | --- | --- |']
for n in [256,512]:
 b=before[n]
 reduction=st.median(100*(1-r[2]['totalSocketPayloadBytes']/r[0]['totalSocketPayloadBytes']) for r in after[n])
 combined=st.median((r[2]['simAllocatedBytesPerTick']+r[2]['poolAllocatedBytesPerTick'])/M for r in after[n])
 lines.append(f'| {n} | {b[0]["totalSocketPayloadBytes"]/M:.2f} / {b[2]["totalSocketPayloadBytes"]/M:.2f} | {med(n,0,"totalSocketPayloadBytes")/M:.2f} / {med(n,2,"totalSocketPayloadBytes")/M:.2f} | {reduction:.1f}% | {combined:.4f} |')
lines+=['','All flags on is cheaper than the final flags-off producer baseline:','']
for n in [256,512]:
 cost=med(n,2,'tickMeanMs'); off=med(n,0,'tickMeanMs')
 lines.append(f'- {n} peers: **{cost:.2f} vs {off:.2f} ms**, a {100*(1-cost/off):.1f}% reduction.')
lines+=['','At 512 peers the mean is close to the 50 ms nominal tick budget and p95 exceeds it. These',
        'numbers establish lower broadcast cost; they do not establish 20 Hz full',
        'simulation capacity for 512 combatants.']
text=report.read_text()
start=text.find('<!-- FINAL_MEASUREMENT_TABLES -->')
if start < 0: start=text.index('| Peers | Flags | Producer before mean')
end=text.index('\n## Verification',start)
text=text[:start]+'\n'.join(lines)+"\n"+text[end:]
report.write_text(text)
print('\n'.join(lines))
