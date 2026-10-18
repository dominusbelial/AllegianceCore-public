"""Read verification progress without inspecting other tool PID namespaces."""
import fcntl, json, re, time
from pathlib import Path
out = Path(__file__).resolve().parent
def read_json(name):
    for attempt in range(10):
        try: return json.loads((out / name).read_text())
        except json.JSONDecodeError:
            if attempt == 9: raise
            time.sleep(.1)
rows = read_json('verification.json')
active = read_json('active-run.json')
log = out / (active['label'] + '.log')
logged_failures = len(re.findall(r'^  Failed ', log.read_text(), re.M)) if log.exists() else 0
with (out / 'runner.lock').open('a') as lock:
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        running = False
    except BlockingIOError:
        running = True
print(json.dumps(dict(invocations=len(rows), passed=sum(int(r.get('counters', {}).get('passed', 0)) for r in rows),
                      failed=sum(int(r.get('counters', {}).get('failed', 0)) for r in rows),
                      active=active['label'], elapsed=round(time.time() - active.get('started', time.time())),
                      runner_locked=running, active_logged_failures=logged_failures)))
