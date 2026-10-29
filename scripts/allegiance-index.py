#!/usr/bin/env python3
"""Allegiance Index, schema 2 rig cards (v1 cards converted on load).
Python stdlib only; see docs/PERFORMANCE.md and docs/reports/AIX2_REPORT.md."""
import argparse
import bisect
import csv
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import statistics
import sys

BUSY_MEAN_MS = 5.0
WINDOW_SECONDS = 5.0
GOOD_TICKS = 95
GOOD_P95_MS = 40.0
GOOD_DILATION = 0.95
HEADROOM_CAP = 4.0
DEFAULT_K = 0.5  # Conservative fallback; calibration evidence in AIX1_REPORT.md.
# AIX2 — whole-setup hardware. weighted cores = P + EFFICIENCY_WEIGHT * E; threads/SMT
# are reported but never counted. 0.5 is the conservative default: no measured P-vs-E
# throughput ratio for these workloads exists (AIX1 calibrated on P-cores only), so an
# efficiency core is credited half a performance core rather than a full one. Recalibrate
# only with a cited measurement (AIX2_REPORT.md).
EFFICIENCY_WEIGHT = 0.5
# AIX-S — a shared server+client machine is only good when the client keeps rendering:
# p95 frame time <= two 60 Hz frames (one dropped vsync), the 30 fps floor.
GOOD_CLIENT_P95_MS = 33.3
ROLES = ('server', 'bots', 'client')
REFERENCE = Path(__file__).resolve().parents[1] / 'docs/benchmarks/aix-b55-reference.json'
NUMBER = r'\d+(?:\.\d+)?'
TICK = re.compile(r'\[tickprof\] window (' + NUMBER + r')s ticks=(\d+).*?tick-wall mean=('
                  + NUMBER + r')ms p95=(' + NUMBER + r')ms')
FRAME = re.compile(r'\[frameprof\] window (' + NUMBER + r')s frames=(\d+) frame-wall mean=('
                   + NUMBER + r')ms p95=(' + NUMBER + r')ms')
STAMP = re.compile(r'\d{4}-\d\d-\d\d[T ]\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?')
FINAL = re.compile(r'\[gameover-timing\].*\bwinner=\S+ peers=(\d+)')


def timestamp(text):
    match = STAMP.search(text)
    if not match:
        return None
    value = datetime.fromisoformat(match[0].replace('Z', '+00:00'))
    return value.replace(tzinfo=value.tzinfo or timezone.utc).timestamp()


@dataclass
class Window:
    ticks: int
    mean: float
    p95: float
    time: float | None = None
    dilation: float | None = None

    @property
    def busy(self):
        return self.mean >= BUSY_MEAN_MS

    @property
    def good(self):
        return (self.ticks >= GOOD_TICKS and self.p95 <= GOOD_P95_MS
                and (self.dilation is None or self.dilation >= GOOD_DILATION))


@dataclass
class FrameWindow:
    """One [frameprof] client summary; the AIX-S client-side input."""
    frames: int
    mean: float
    p95: float
    time: float | None = None

    @property
    def good(self):
        return self.frames > 0 and self.p95 <= GOOD_CLIENT_P95_MS


def normalize_rig(card):
    """Return a schema-2 card: a list of machines with roles and P/E cores.

    Schema-1 cards (server dict + botHosts) convert on load: the server machine
    keeps its physicalCores as performance cores (no efficiency cores were
    reported), each bot host becomes a bots-role machine.
    """
    if not isinstance(card, dict):
        raise ValueError('rig card must be a JSON object')
    if 'machines' in card:
        machines = []
        for index, machine in enumerate(card['machines']):
            roles = machine.get('roles') or []
            if not roles or any(role not in ROLES for role in roles):
                raise ValueError(f'machine {index}: roles must be a nonempty subset of {ROLES}')
            performance = machine.get('performanceCores')
            efficiency = machine.get('efficiencyCores', 0)
            if not isinstance(performance, (int, float)) or not math.isfinite(performance) or performance <= 0:
                raise ValueError(f'machine {index}: performanceCores must be positive')
            if not isinstance(efficiency, (int, float)) or not math.isfinite(efficiency) or efficiency < 0:
                raise ValueError(f'machine {index}: efficiencyCores must be nonnegative')
            machines.append(dict(machine, roles=list(roles)))
    else:
        server = card.get('server')
        if not isinstance(server, dict):
            raise ValueError('rig card needs either machines[] or a server object')
        physical = server.get('physicalCores')
        if not isinstance(physical, (int, float)) or not math.isfinite(physical) or physical <= 0:
            raise ValueError('rig.server.physicalCores (performanceCores) must be positive')
        machines = [dict(server, name=card.get('name', 'server'), roles=['server'],
                         performanceCores=physical, efficiencyCores=0)]
        for index, host in enumerate(card.get('botHosts', [])):
            cores = host.get('cores')
            if not isinstance(cores, (int, float)) or not math.isfinite(cores) or cores <= 0:
                raise ValueError(f'botHost {index}: cores (performanceCores) must be positive')
            machines.append(dict(host, name=host.get('name', f"{card.get('name', 'rig')}-bot{index + 1}"),
                                 roles=['bots'], performanceCores=cores, efficiencyCores=0))
    servers = [machine for machine in machines if 'server' in machine['roles']]
    if len(servers) != 1:
        raise ValueError('a rig card must name exactly one server machine')
    return dict(schema=2, name=card.get('name'), machines=machines,
                scenario=card.get('scenario'), shared=card.get('shared', False))


def machine_cores(machine):
    """Weighted cores of one machine: P + EFFICIENCY_WEIGHT * E; SMT never counts."""
    weighted = machine['performanceCores'] + EFFICIENCY_WEIGHT * machine['efficiencyCores']
    if not math.isfinite(weighted) or weighted <= 0:
        raise ValueError('machine weighted cores must be positive')
    return weighted


def read_frames(paths):
    """Parse [frameprof] window lines (the P889 client frame-time log)."""
    windows, notes = [], []
    seen = {}
    for path in paths:
        for line in Path(path).read_text().splitlines():
            match = FRAME.search(line)
            if not match:
                continue
            seconds, frames, mean, p95 = map(float, match.groups())
            if seconds != WINDOW_SECONDS:
                notes.append('Ignored non-5s frameprof window')
                continue
            window = FrameWindow(int(frames), mean, p95, timestamp(line))
            if window.time is not None and window.time in seen:
                previous = seen[window.time]
                if (previous.frames, previous.mean, previous.p95) != (window.frames, window.mean, window.p95):
                    raise ValueError('Conflicting frameprof windows at the same timestamp')
                continue
            windows.append(window)
            if window.time is not None:
                seen[window.time] = window
    return windows, sorted(set(notes))


def client_good_by_window(busy, frame_windows, notes):
    """AIX-S window rule: server-good AND a client frame window within one
    window span whose p95 <= GOOD_CLIENT_P95_MS. Unmatched or timestampless
    server windows fail closed (not good) and are noted."""
    times = [window.time for window in frame_windows if window.time is not None]
    verdicts, matched = [], 0
    for window in busy:
        verdict = False
        if window.time is not None and times:
            nearest = min(times, key=lambda time: abs(time - window.time))
            if abs(nearest - window.time) <= WINDOW_SECONDS:
                candidate = next(item for item in frame_windows if item.time == nearest)
                verdict = candidate.good
                matched += 1
        verdicts.append(verdict)
    if len(busy) - matched > 0:
        notes.append(f'{len(busy) - matched} busy window(s) without an aligned client frame '
                     f'window (within {WINDOW_SECONDS:.0f}s); they count as not good (fail-closed)')
    return verdicts, matched


def read_servers(paths):
    windows, peers, notes = [], [], []
    finals, scores, score_stations, seen_times, seen_epochs = [], [], [], {}, set()
    last = None
    elapsed = match_start = 0.0
    has_battle = False
    for path in paths:
        # Keep file order: untimestamped midnight splits are consecutive pieces.
        for line in Path(path).read_text().splitlines():
            has_battle |= '[finale]' in line or '[scoring]' in line
            match = TICK.search(line)
            if match:
                seconds, ticks, mean, p95 = map(float, match.groups())
                if seconds != WINDOW_SECONDS:
                    notes.append('Ignored non-5s profiler window')
                    last = None
                    continue
                last = Window(int(ticks), mean, p95, timestamp(line))
                scale = re.search(r'\bsim.timeScale=(' + NUMBER + ')', line)
                if scale:
                    last.dilation = float(scale[1])
                if last.time is not None and last.time in seen_times:
                    previous = seen_times[last.time]
                    if (previous.ticks, previous.mean, previous.p95) != (last.ticks, last.mean, last.p95):
                        raise ValueError('Conflicting profiler windows at the same timestamp')
                    last = previous
                    continue
                windows.append(last)
                if last.time is not None:
                    seen_times[last.time] = last
                elapsed += WINDOW_SECONDS
            elif '[tidi]' in line and last is not None:
                scale = re.search(r'\bscale=(' + NUMBER + ')', line)
                if scale:
                    last.dilation = float(scale[1])
            if '[finale] deadline winner' in line:
                epoch = re.search(r'\bepoch=(\S+)', line)
                key = epoch[1] if epoch else line
                if key not in seen_epochs:
                    seen_epochs.add(key)
                    kills = sum(map(int, re.findall(r'\bkills=(\d+)', line)))
                    finals.append((kills, elapsed - match_start))
                    match_start = elapsed
            if '[scoring]' in line and 'final score' in line:
                # Per-player scoring rows: kills, never deaths or damage totals.
                scores.extend(map(int, re.findall(r'\bkills=(\d+)', line)))
                score_stations.extend(map(float, re.findall(r'\bbaseKills=(' + NUMBER + ')', line)))
            match = FINAL.search(line)
            if match:
                peers.append(int(match[1]))
                match_start = elapsed
    if any(w.time is None for w in windows):
        notes.append('Profiler timestamps missing; samples cannot be aligned to those windows')
    return windows, peers, finals, scores, score_stations, has_battle, sorted(set(notes))


def read_samples(paths):
    samples = {}
    for path in paths:
        with Path(path).open(newline='') as stream:
            for row in csv.DictReader(stream):
                time = timestamp(row['ts'])
                value = float(row['peers'])
                if time is None or not math.isfinite(value) or value < 0:
                    raise ValueError('Samples require finite nonnegative peers and ISO timestamps')
                if time in samples and samples[time] != value:
                    raise ValueError('Conflicting peers samples at the same timestamp')
                samples[time] = value
    return sorted(samples.items())


def player_count(busy, samples, final_peers, cli):
    times = [s[0] for s in samples]
    matched = []
    for window in busy:
        if window.time is None or not samples:
            break
        pos = bisect.bisect_left(times, window.time)
        candidates = samples[max(0, pos - 1):pos + 1]
        nearest = min(candidates, key=lambda item: abs(item[0] - window.time))
        if abs(nearest[0] - window.time) > 10:
            break
        matched.append(nearest[1])
    if len(matched) == len(busy):
        return statistics.mean(matched), 'samples (nearest within 10s, all busy windows)'
    if final_peers:
        return statistics.mean(final_peers), 'gameover-timing (final winner summaries only)'
    if cli is not None and math.isfinite(cli) and cli > 0:
        return cli, 'CLI --players'
    raise ValueError('No aligned peers samples or final gameover peers; supply --players')


def bot_metrics(paths):
    stalls = excluded = disconnects = 0
    stations, durations = set(), []
    for path in paths:
        # Stream large bot logs; retain only incident and rotation times.
        rotations, incidents, events = [], [], []
        clock = maximum_clock = 0.0
        with Path(path).open() as stream:
            for line in stream:
                tick = re.search(r'\[(?:opening|gallery|intel|scout)\].*?\bt=(' + NUMBER + r')s', line)
                if tick:
                    clock = float(tick[1])
                    maximum_clock = max(maximum_clock, clock)
                if '[p598]' in line and 'after rotation' in line:
                    rotations.append(clock)
                if '[stall]' in line:
                    incidents.append(clock)
                if re.search(r'\bdisconnected\b|\bdisconnect\b.*reason=', line, re.I):
                    disconnects += 1
                if 'outcome=station-killed' in line:
                    hits = re.search(r'hitsInWindow=(\d+)', line)
                    target = re.search(r'target=station#(\d+)', line)
                    # Missing targets at rotation also log "station-killed" with
                    # zero hits. Only count damage-backed observations.
                    if hits and int(hits[1]) > 0 and target:
                        events.append((clock, target[1]))
        durations.append(maximum_clock)
        rotation_times = sorted(set(rotations))
        for clock in incidents:
            index = bisect.bisect_left(rotation_times, clock)
            nearby = rotation_times[max(0, index - 1):index + 1]
            if any(abs(clock - time) <= 30 for time in nearby):
                excluded += 1
            else:
                stalls += 1
        # Deduplicate the two workers' observations by match ordinal + target.
        # Rotation emits many re-ready rows; collapse each burst to one boundary.
        boundaries = []
        for time in rotation_times:
            if not boundaries or time - boundaries[-1] > 60:
                boundaries.append(time)
        for clock, target in events:
            stations.add((bisect.bisect_right(boundaries, clock), target))
    return dict(stalls=stalls, rotation_stalls_excluded=excluded,
                disconnects=disconnects, station_kills=len(stations),
                mean_log_seconds=statistics.mean(durations) if durations else 0)


def measure_battle(windows, finals, scores, bots, players, rig, score_stations=None, has_battle=False):
    metrics = {'kill_rate': None, 'station_rate': None, 'disconnect_rate': None, 'stall_rate': None}
    details = {'mode': 'battle' if bots or finals or scores or has_battle else 'server-only'}
    hosts = [machine for machine in rig.get('machines', []) if 'bots' in machine.get('roles', [])]
    bot_count = sum(host.get('bots', 0) for host in hosts) or players
    total_minutes = len(windows) * WINDOW_SECONDS / 60 * bot_count
    if finals:
        kills = sum(item[0] for item in finals)
        minutes = sum(item[1] for item in finals) / 60 * bot_count
        metrics['kill_rate'] = kills / minutes if minutes > 0 else None
        details.update(kills=kills, scored_matches=len(finals), scored_bot_minutes=minutes,
                       kill_source='finale side totals over scored completed-match windows')
    elif scores:
        metrics['kill_rate'] = sum(scores) / total_minutes
        details.update(kills=sum(scores), kill_source='scoring final score rows / session bot-minutes')
    if bots:
        observed = bot_metrics(bots)
        # Bot capture lengths can differ from the server excerpt. Normalize
        # their incidents by their own observed uptime, assuming equal workers.
        bot_minutes = observed['mean_log_seconds'] / 60 * bot_count or total_minutes
        metrics.update(station_rate=observed['station_kills'] / bot_minutes,
                       disconnect_rate=observed['disconnects'] / bot_minutes,
                       stall_rate=observed['stalls'] / bot_minutes)
        details['incident_bot_minutes'] = bot_minutes
        details.update(observed)
    if score_stations:
        metrics['station_rate'] = sum(score_stations) / total_minutes
        details['station_source'] = 'scoring baseKills credits / session bot-minutes'
    details.update(metrics=metrics, bot_count=bot_count, session_bot_minutes=total_minutes)
    return details


def battle_factor(battle, reference):
    if battle['mode'] == 'server-only':
        return 1.0, {}
    components = {}
    for key, value in battle['metrics'].items():
        base = reference.get(key)
        if value is None or base is None:
            continue
        if not math.isfinite(base) or base < 0:
            raise ValueError('Battle reference rates must be finite and nonnegative')
        if key in ('kill_rate', 'station_rate'):
            if base > 0:
                components[key] = min(1.0, value / base)
            # No observed reference station kills: no defensible ratio.
        else:
            # Smooth zero incident rates: one pseudo-incident per 1000 bot-min.
            components[key] = min(1.0, (1 + 1000 * base) / (1 + 1000 * value))
    if not components:
        return 1.0, components
    return math.prod(components.values()) ** (1 / len(components)), components


def fit_exponent(points):
    """OLS log(cost) = intercept + a*log(peers); R² below .95 uses fallback."""
    if len(points) < 3 or any(not math.isfinite(n) or not math.isfinite(cost)
                              or n <= 0 or cost <= 0 for n, cost in points):
        raise ValueError('Calibration needs at least three positive peer/cost points')
    xs, ys = zip(*((math.log(n), math.log(cost)) for n, cost in points))
    xbar, ybar = statistics.mean(xs), statistics.mean(ys)
    variance = sum((x - xbar) ** 2 for x in xs)
    if variance == 0:
        raise ValueError('Calibration requires different peer counts')
    a = sum((x - xbar) * (y - ybar) for x, y in zip(xs, ys)) / variance
    intercept = ybar - a * xbar
    total = sum((y - ybar) ** 2 for y in ys)
    residual = sum((y - intercept - a * x) ** 2 for x, y in zip(xs, ys))
    r_squared = 1 - residual / total if total else 0
    accepted = a > 0 and r_squared >= .95
    return dict(a=a, r_squared=r_squared, coefficient=math.exp(intercept),
                k=1 / a if accepted else .5, accepted=accepted)


def compute(servers, samples, bots, rig, players=None, k=DEFAULT_K, reference=None, label='session',
            frames=None):
    if not math.isfinite(k) or k <= 0:
        raise ValueError('k must be finite and positive')
    rig = normalize_rig(rig)
    server_machine = next(machine for machine in rig['machines'] if 'server' in machine['roles'])
    cores = machine_cores(server_machine)
    total_cores = sum(machine_cores(machine) for machine in rig['machines'])
    windows, peers, finals, scores, score_stations, has_battle, notes = read_servers(servers)
    busy = [w for w in windows if w.busy]
    if not busy:
        raise ValueError('No busy 5s tickprof windows (mean >= 5ms)')
    count, source = player_count(busy, read_samples(samples), peers, players)
    quality = sum(w.good for w in busy) / len(busy)
    median = statistics.median(w.p95 for w in busy)
    if median <= 0:
        raise ValueError('Busy-window p95 must be positive')
    headroom = min(HEADROOM_CAP, GOOD_P95_MS / median)
    battle = measure_battle(windows, finals, scores, bots, count, rig, score_stations, has_battle)
    factor, components = battle_factor(battle, reference or {})
    battle.update(factor=factor, components=components,
                  missing_metrics=[key for key, value in battle['metrics'].items() if value is None],
                  unscored_metrics=[key for key in battle['metrics'] if key not in components])
    if battle['mode'] == 'battle' and not components:
        notes.append('Battle evidence present but no comparable metrics; Battle=1 is unverified')
    if battle['mode'] == 'battle' and battle['missing_metrics']:
        notes.append('Partial Battle coverage: ' + ', '.join(battle['missing_metrics']))
    if bots and battle['metrics']['station_rate'] == 0:
        notes.append('No damage-backed station kills observed (rotation disappearance is excluded)')
    if bots and abs(battle['mean_log_seconds'] - len(windows) * WINDOW_SECONDS) > len(windows) * WINDOW_SECONDS * .1:
        notes.append('Bot/server capture durations differ by >10%; bot incidents use their own uptime; scopes may differ')
    if battle['mode'] == 'battle' and 'station_rate' not in components:
        notes.append('Station metric unscored: absent evidence or zero reference rate')
    aix = count * quality * headroom ** k * factor
    machines = len(rig['machines'])
    shared = bool(rig.get('shared')) or any(
        {'server', 'client'} <= set(machine['roles']) for machine in rig['machines'])
    result = dict(schema=2, label=label, aix=aix, aix_per_core=aix / cores,
                  aix_per_total_core=aix / total_cores,
                  players=count, players_source=source, quality=quality, headroom=headroom, k=k,
                  median_p95_ms=median, windows=len(windows), busy_windows=len(busy),
                  good_windows=sum(w.good for w in busy),
                  dilation_windows=sum(w.dilation is not None for w in busy), battle=battle,
                  rig=rig['name'], server_physical_cores=server_machine['performanceCores'],
                  server_weighted_cores=cores, total_weighted_cores=total_cores,
                  efficiency_weight=EFFICIENCY_WEIGHT, machines=machines, shared=shared, notes=notes)
    if shared:
        if not frames:
            raise ValueError('shared server+client rig (AIX-S) requires --frames client frame logs')
        frame_windows, frame_notes = read_frames(frames)
        notes.extend(frame_notes)
        verdicts, matched = client_good_by_window(busy, frame_windows, notes)
        # AIX-S: a busy window is good only if the server window is good AND the
        # client frame time in that window is good (p95 ≤ GOOD_CLIENT_P95_MS).
        both = [window.good and verdict for window, verdict in zip(busy, verdicts)]
        quality_shared = sum(both) / len(both)
        aix_s = count * quality_shared * headroom ** k * factor
        result.update(aix_s=aix_s, aix_s_per_core=aix_s / cores,
                      aix_s_per_total_core=aix_s / total_cores, quality_shared=quality_shared,
                      client_windows=len(frame_windows),
                      client_matched_windows=matched,
                      client_good_windows=sum(both),
                      median_client_p95_ms=statistics.median(w.p95 for w in frame_windows)
                      if frame_windows else None,
                      client_p95_limit=GOOD_CLIENT_P95_MS)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', action='append', nargs='+', required=True, help='chronological log pieces')
    parser.add_argument('--samples', action='append', nargs='+', default=[])
    parser.add_argument('--bots', action='append', nargs='+', default=[])
    parser.add_argument('--frames', action='append', nargs='+', default=[],
                        help='client [frameprof] log pieces (AIX-S, shared server+client machine)')
    parser.add_argument('--rig', required=True)
    parser.add_argument('--label', default='session')
    parser.add_argument('--players', type=float, help='last-resort connected-peer count')
    parser.add_argument('--k', type=float, default=DEFAULT_K)
    parser.add_argument('--reference', type=Path, default=REFERENCE, help='Battle reference JSON (default b55)')
    parser.add_argument('--json', type=Path)
    args = parser.parse_args()
    flatten = lambda groups: [path for group in groups for path in group]
    try:
        reference = json.loads(args.reference.read_text())
        result = compute(flatten(args.server), flatten(args.samples), flatten(args.bots),
                         json.loads(Path(args.rig).read_text()), args.players, args.k,
                         reference['metrics'], args.label, flatten(args.frames))
        battle = result['battle']
        battle_term = f" × B {battle['factor']:.3f}" if battle['mode'] == 'battle' else ''
        print(f"{args.label}: AIX {result['aix']:.0f} "
              f"({result['aix_per_core']:.1f}/server-core, {result['aix_per_total_core']:.1f}/total-core) = "
              f"{result['players']:.1f} players × Q {result['quality']:.3f} × "
              f"H {result['headroom']:.2f}^{result['k']:.3g}{battle_term} "
              f"[{result['rig']}, {result['machines']} machines, "
              f"{result['total_weighted_cores']:.0f} weighted cores (w={result['efficiency_weight']}); "
              f"{battle['mode']}]")
        if result.get('shared'):
            print(f"{args.label}: AIX-S {result['aix_s']:.0f} "
                  f"({result['aix_s_per_core']:.1f}/server-core, {result['aix_s_per_total_core']:.1f}/total-core) = "
                  f"{result['players']:.1f} players × Q_S {result['quality_shared']:.3f} × "
                  f"H {result['headroom']:.2f}^{result['k']:.3g}{battle_term} "
                  f"[{result['rig']}, {result['machines']} machine(s), "
                  f"{result['total_weighted_cores']:.0f} weighted cores; shared server+client, "
                  f"client p95 ≤ {result['client_p95_limit']}ms; {battle['mode']}]")
            print('  AIX-S is the shared-machine variant; never compare it directly with dedicated AIX.')
        print(f"  {result['good_windows']}/{result['busy_windows']} busy windows good; "
              f"median p95 {result['median_p95_ms']:.2f}ms; players: {result['players_source']}; "
              f"dilation observed: {result['dilation_windows']}/{result['busy_windows']}")
        if battle['components']:
            print('  Battle/reference ' + reference.get('label', '?') + ': ' + ', '.join(
                f'{key}={value:.3f}' for key, value in battle['components'].items()))
        for note in result['notes']:
            print('  Note: ' + note)
        if args.json:
            args.json.write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(2, f'allegiance-index: {error}\n')
    return 0


if __name__ == '__main__':
    sys.exit(main())
