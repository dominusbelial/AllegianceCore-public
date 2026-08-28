#!/usr/bin/env python3
"""Empirically verify computed IGC payload layouts against the corpus."""
import struct, os

def f32(b, off): return struct.unpack_from("<f", b, off)[0]
def i16(b, off): return struct.unpack_from("<h", b, off)[0]

def iter_records(raw, body_start):
    pos = body_start
    end = len(raw)
    while pos < end:
        typ, size = struct.unpack_from("<hi", raw, pos)
        if size < 0: raise ValueError(f"bad size {size}")
        yield typ, raw[pos+6 : pos+6+size]
        pos += 6 + size

TARGETS = [
    "/home/domingo/Development/Artwork/static_core.igc",
    "/home/domingo/Development/Artwork/racecore.igc",
    "/home/domingo/Development/Artwork/zone_core.igc",
    "/home/domingo/Development/Artwork/wc_01.igc",
    "/home/domingo/Development/Artwork/tcor_02.igc",
]

for p in TARGETS:
    if not os.path.exists(p): continue
    raw = open(p, "rb").read()
    ver, dsize = struct.unpack_from("<ii", raw, 0)
    body_start = 8 if dsize == len(raw)-8 else 4
    print(f"\n=== {os.path.basename(p)} ver={ver} body_start={body_start}")
    for typ, pay in iter_records(raw, body_start):
        if typ == 34:
            fc = [f32(pay, 4*i) for i in range(40)]
            dm = [[f32(pay, 160 + 4*(i*20+j)) for j in range(20)] for i in range(20)]
            badf = [(i,round(v,3)) for i,v in enumerate(fc) if not (-1e6 < v < 1e6)]
            badm = sum(1 for row in dm for v in row if not (-100 < v < 100))
            print(f"  [constants] size={len(pay)} fc[0..4]={[round(x,3) for x in fc[:5]]}")
            print(f"              fc[36..40]={[round(x,3) for x in fc[36:]]}  badFC={badf[:8]}")
            print(f"              dm[1][1..5]={[round(x,3) for x in dm[1][1:6]]} dm[2][1..5]={[round(x,3) for x in dm[2][1:6]]} badDM={badm}")
            print(f"              dm[0][0]={dm[0][0]:.3g} (expect ~0 asteroid default)")
        elif typ == 23:  # missile 524
            expid = i16(pay,446); accel=f32(pay,464); turn=f32(pay,468)
            init=f32(pay,472); lock=f32(pay,476); width=f32(pay,512)
            dmg=pay[516]; bdir=pay[517]; launch=i16(pay,518); amb=i16(pay,520)
            ld_sign=f32(pay,428); ld_mass=f32(pay,432); ld_ps=i16(pay,436); ld_es=i16(pay,438)
            print(f"  [missile]  expid={expid} accel={accel:.3g} turn={turn:.3g} init={init:.3g} lock={lock:.3g} width={width:.3g} dmg={dmg} bDir={bdir} launch={launch} amb={amb} | ld_sign={ld_sign:.3g} ld_mass={ld_mass:.3g} ld_partmask={ld_ps} ld_expsize={ld_es}")
        elif typ == 24:  # mine 480
            expid=i16(pay,446); rad=f32(pay,464); pow=f32(pay,468); endur=f32(pay,472); dmg=pay[476]
            print(f"  [mine]     expid={expid} radius={rad:.3g} power={pow:.3g} endurance={endur:.3g} dmg={dmg}")
        elif typ == 26:  # chaff 468
            expid=i16(pay,446); strength=f32(pay,464)
            print(f"  [chaff]    expid={expid} chaffStrength={strength:.3g}")
        elif typ == 25:  # probe 492
            expid=i16(pay,446); scan=f32(pay,464); burst=f32(pay,468); disp=f32(pay,472)
            acc=f32(pay,476); ammo=i16(pay,480); pid=i16(pay,482); amb=i16(pay,484); dt=f32(pay,488)
            print(f"  [probe]    expid={expid} scanner={scan:.3g} burst={burst:.3g} disp={disp:.3g} acc={acc:.3g} ammo={ammo} projid={pid} amb={amb} dtRip={dt:.3g}")
        elif typ == 31:  # station 524
            tex = pay[486:499].split(b"\0")[0]
            bld = pay[499:524].split(b"\0")[0]
            print("  [station]  sign@364=%g armor@368=%g shield@372=%g regen@376=%g scanner@384=%g income@388=%g radius@392=%g stid@446=%d succ@448=%d defArmor@450=%d defShield@451=%d sabm@452=%d aabm@454=%d cls@456=%d cDrone@458=%d tex=%r builder=%r" % (
                f32(pay,364), f32(pay,368), f32(pay,372), f32(pay,376), f32(pay,384), f32(pay,388), f32(pay,392),
                i16(pay,446), i16(pay,448), pay[450], pay[451], i16(pay,452), i16(pay,454), pay[456], i16(pay,458),
                tex, bld))
        elif typ == 33:  # drone 384
            print(f"  [drone]    shoot@364={f32(pay,364):.3g} move@368={f32(pay,368):.3g} brave@372={f32(pay,372):.3g} pilot@376={pay[376]} hullType@378={i16(pay,378)} droneType@380={i16(pay,380)} etidLaid@382={i16(pay,382)}")
