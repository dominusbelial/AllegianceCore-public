"""P826 evidence integrity: lifecycle, phase, cutoff and worker alignment."""
import json
from pathlib import Path
import struct
import tempfile
import unittest

from audit import log_lines, position_audit, position_workers
from summarize import position_geometry, position_summary


class PositionAuditTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[2]/'.scratch')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def log(self, name, lines):
        p = self.root/name
        p.write_text(''.join(f'2026-10-25T00:00:{i:02d}.000000000Z {l}\n' for i,l in enumerate(lines)))
        return p

    def test_scoring_ejection_pod_death_miner_and_finale_reconcile(self):
        p = self.log('server.log', [
            "[initial-miner] side 1 spawned initial miner #1/1 ship 3 (hull 'Miner') docked at station 11 'Garrison' in 'Organia'",
            "[spawn] ship 1 seated on side 0 (Alpha) in its home sector 'Ganymede' (id 1050)",
            "[spawn] ship 2 seated on side 1 (Bravo) in its home sector 'Organia' (id 1051)",
            '[start] countdown begun',
            "[loadout] peer ship 1: swapping base hull 0 ('Old') -> 410 ('Scout')",
            "[loadout] peer ship 2: swapping base hull 0 ('Old') -> 124 ('Bomber')",
            '[scoring] killer ship 1 kill +1',
            '[combat] ship 2 — KILL_SHIP(105) bKillCredit=True bDeathCredit=False',
            '[pod-trip] eject ship=2 side=1 sector=1052 at=10.0',
            '[pod-trip] '+json.dumps(dict(Ship=2,Side=1,Outcome='death',EndSector=1061,Seconds=1,EjectSector=1052,EndAt=11)),
            '[combat] ship 2 — KILL_SHIP(105) bKillCredit=False bDeathCredit=True',
            '[combat] ship 2 killed by ship 1 at sim-t 11.0',
            '[finale] research armedBy=0 deadline=930; timeout=900',
            '[scoring] killer ship 1 kill +1',
            '[combat] ship 3 — KILL_SHIP(105) bKillCredit=True bDeathCredit=True',
            '[combat] ship 3 killed by ship 1 at sim-t 31.0',
            '[finale] deadline winner side=0 values=0:kills=2/deaths=0,1:kills=0/deaths=2',
            '[gameover] mission #0 -> Stage Over',
            '[gameover-timing] mission #-1',
            '[start] countdown begun',
        ])
        d = position_audit(p, stop_after=1)
        self.assertEqual(1,len(d['matches']))
        m=d['matches'][0]
        self.assertEqual('deadline',m['status'])
        self.assertTrue(m['reconciled']['matchesDeadline'])
        self.assertEqual(1,m['miners'][1]['lost'])
        self.assertIn([1,'pre','1061','Bomber','pod',False,True,1],m['events'])
        self.assertIn([1,'pre','1052','Bomber','bomber',True,False,1],m['events'])
        self.assertIn([0,'finale','Scout',False,'miner',1],m['attackers'])

    def test_eof_and_admin_end_are_not_deadline_results(self):
        p=self.log('server.log',['[start] countdown begun','[gameover] mission #0 -> Stage Over','[start] countdown begun'])
        d=position_audit(p)
        self.assertEqual(['ended-without-deadline','censored'],[m['status'] for m in d['matches']])
        p=self.log('bare.log',['hello'])
        p.write_text('[start] countdown begun\n')
        self.assertEqual([(None,'[start] countdown begun\n')],list(log_lines(p)))

    def test_interleaved_worker_purchase_belongs_to_own_next_episode(self):
        p=self.log('worker.log',[
            '[loadout] a initial: role=Fighter',
            "[p598] re-readied bot 'a' ship 1 after rotation",
            '[loadout] b initial: role=Scout',
            "[p598] re-readied bot 'b' ship 2 after rotation",
            # One bot has not re-readied yet: its purchase is episode two.
            '[loadout] b initial: role=Fighter',
            "[p598] re-readied bot 'b' ship 2 after rotation",
            '[loadout] a initial: role=Interceptor',
            "[p598] re-readied bot 'a' ship 1 after rotation",
        ])
        ms=position_workers([p],{1:dict(side=0),2:dict(side=1)},[]) [0]['matches']
        self.assertEqual([dict(Fighter=1),dict(Scout=1)],ms[0]['initialRoles'])
        self.assertEqual([dict(Interceptor=1),dict(Fighter=1)],ms[1]['initialRoles'])

    def test_summary_preserves_shutdown_and_censoring(self):
        p=self.log('server.log',['[start] countdown begun'])
        d=position_audit(p)
        out=self.root/'audit.json';out.write_text(json.dumps(d))
        m=position_summary([out])['matches'][0]
        self.assertEqual('censored',m['status'])
        self.assertEqual([0,0],m['phases']['pre']['kills'])

    def test_bad_map_wrapper_fails(self):
        p=self.root/'map.igc';p.write_bytes(struct.pack('<i',100)+b'short')
        with self.assertRaises(ValueError):position_geometry(p)

    def test_map_routes_are_reciprocal_but_clash_commutes_need_not_be(self):
        records=[]
        def add(kind,payload):
            records.append(struct.pack('<hi',kind,len(payload))+payload)
        for sector in (1050,1051,1052,1054,1055,1059,1061):
            b=bytearray(92);struct.pack_into('<h',b,28,sector);add(13,b)
        for side,sector in enumerate((1050,1051)):
            b=bytearray(88)
            struct.pack_into('<fff',b,0,-10*side,0,0)
            struct.pack_into('<hhh',b,52,sector,side,side+10);add(1,b)
        wid=0
        for a,b in ((1050,1054),(1054,1055),(1055,1051),(1050,1059),(1059,1052),(1051,1061),(1061,1052)):
            for sector,x,partner in ((a,10,wid+1),(b,20,wid)):
                payload=bytearray(108)
                struct.pack_into('<hh',payload,0,wid if sector==a else wid+1,partner)
                struct.pack_into('<fff',payload,60,x,0,0)
                struct.pack_into('<h',payload,104,sector);add(7,payload)
            wid+=2
        body=b''.join(records);p=self.root/'map.igc';p.write_bytes(struct.pack('<i',len(body))+body)
        d=position_geometry(p)
        self.assertEqual([60,60],[r['distance'] for r in d['siege']])
        self.assertEqual([20,30],[s['routes'][0]['distance'] for s in d['clash']])


if __name__ == '__main__':
    unittest.main()
