"""Aggregate the unchanged G09 criteria and related regression artifacts."""
import argparse
import csv
import json
import pathlib

p = argparse.ArgumentParser()
p.add_argument('root', type=pathlib.Path)
root = p.parse_args().root
checks = []


def check(name, passed, **values):
    checks.append(dict(check=name, passed=bool(passed), **values))


def rows(path):
    with path.open(encoding='utf-8-sig', newline='') as f:
        return [{k: float(v) for k, v in r.items()} for r in csv.DictReader(f)]


for name, path, count in [
    ('matrix', 'final/matrix-acceptance.json', 28),
    ('enclosures and matrix', 'final/acceptance.json', 34),
    ('cold front and cooling', 'cold/acceptance.json', 30),
]:
    report = json.loads((root / path).read_text(encoding='utf-8'))
    check(name, report['passed'] and len(report['checks']) == count, count=len(report['checks']))
coal = json.loads((root / 'coal/coal-analysis.json').read_text())
check('coal furnace', coal['passed'] and len(coal['results']) == 6,
      timing=coal['firstReductionTimingSpread'])

for stage in ['before', 'repeat']:
    for mode in ['Simulation', 'Sandbox']:
        cases = [rows(root / stage / f'{mode}-furnace-{fps}.csv') for fps in [30, 60, 100]]
        for metric, values in [
            ('pressureIntegral', [sum(r['pulsePressureStock'] * .1 for r in c) for c in cases]),
            ('speedPeak', [max(r['speedPeak'] for r in c) for c in cases]),
            ('speedIntegral', [sum(r['speedPeak'] * .1 for r in c) for c in cases]),
        ]:
            spread = max(values) / min(values) - 1
            if stage == 'repeat':
                check(f'{mode} repeat G09 {metric}', spread <= .2, values=values, spread=spread)
            elif mode == 'Simulation' and metric == 'pressureIntegral':
                check('baseline reproduces G09 defect', spread > .2, values=values, spread=spread)

unit = (root / 'unit/run.log').read_text(encoding='utf-8-sig')
check('source ledger and frame cadence', 'PHYXEL_REACTION_PULSE_SUCCESS' in unit and
      unit.count('PHYXEL_POWDER_CADENCE ') == 24, frameCases=unit.count('PHYXEL_POWDER_CADENCE '))
for flag in ['FURNACE_COMBUSTION', 'AIR_HEAT', 'AIR_INVENTORY', 'AIR_MODE_SWITCH',
             'GAS_TILES', 'TRANSIENT_PACE', 'WATER_CONTACT']:
    log = (root / 'gpu' / f'{flag}.log').read_text(encoding='utf-8-sig')
    check(flag, f'PHYXEL_{flag}_SUCCESS' in log)
for flag in ['GAS_BRUSH', 'WORLD_CODEC', 'THERMAL_MATERIALS', 'COMBUSTION_MATERIALS',
             'THERMAL_DIFFUSION', 'PHASE_MATERIALS', 'PHASE_RUNTIME']:
    log = (root / 'cpu' / f'{flag}.log').read_text(encoding='utf-8-sig')
    check(flag, '_SUCCESS' in log and 'FAILED' not in log)


def performance(stage):
    with (root / stage / 'summary.csv').open(encoding='utf-8-sig', newline='') as f:
        return {r['Mode']: r for r in csv.DictReader(f)}


before = performance('performance-before')
after = performance('performance-after')
for mode in ['Simulation', 'Sandbox']:
    b, a = before[mode], after[mode]
    ratio = float(a['RealFps']) / float(b['RealFps'])
    check(mode + ' performance and clock', ratio >= .9 and a['ClockPassed'] == 'True',
          beforeFps=float(b['RealFps']), afterFps=float(a['RealFps']), ratio=ratio,
          airHz=float(a['AirHz']), reactionHz=float(a['CombustionHz']))

report = dict(passed=all(c['passed'] for c in checks), checks=checks)
(root / 'acceptance.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
for c in checks:
    print(('PASS ' if c['passed'] else 'FAIL ') + c['check'] + ' ' +
          json.dumps({k: v for k, v in c.items() if k not in ['check', 'passed']}))
raise SystemExit(0 if report['passed'] else 1)
