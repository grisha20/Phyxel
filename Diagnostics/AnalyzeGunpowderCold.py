"""Cold-front/cooling acceptance against criteria recorded before final runs."""
import argparse, csv, json, pathlib

p = argparse.ArgumentParser()
p.add_argument('root')
root = pathlib.Path(p.parse_args().root)
cases = {}
for path in (root / 'final').glob('*.csv'):
    with path.open(encoding='utf-8-sig', newline='') as f:
        cases[path.stem] = [{k: float(v) for k, v in row.items()} for row in csv.DictReader(f)]
checks = []

def check(name, ok, **values):
    checks.append(dict(check=name, passed=bool(ok), **values))

def at(rows, seconds):
    return next(r for r in rows if abs(r['seconds'] - seconds) < .0001)

for mode in ['Simulation', 'Sandbox']:
    for fps in [30, 60, 100]:
        def rows(scene): return cases[f'{mode}-{scene}-{fps}']
        name = f'{mode}-{fps}'
        large = rows('large-contact')
        consumed = next((r['seconds'] for r in large if r['powderMass'] <= 10.24), None)
        check(name+' large cold front', at(large, 5)['powderMass'] <= 10.24,
              consumed99PercentAt=consumed, massAt5Seconds=at(large, 5)['powderMass'])
        split = rows('separated')
        check(name+' G07', split[-1]['powderMass'] - split[-1]['isolatedPowderMass'] <= .64 and
              all(abs(r['isolatedPowderMass'] - 64) <= .064 for r in split),
              finalSourceMass=split[-1]['powderMass'] - split[-1]['isolatedPowderMass'],
              finalIsolatedMass=split[-1]['isolatedPowderMass'])
        cold = rows('cold-flame')
        check(name+' G08 cold flame', all(abs(r['powderMass']-64) <= .064 for r in cold), finalMass=cold[-1]['powderMass'])
        quenched = rows('quenched')
        tail = [r for r in quenched if r['seconds'] >= .5]
        check(name+' G08 quench', quenched[-1]['powderMass'] > .1 and
              max(r['powderMass'] for r in tail)-min(r['powderMass'] for r in tail) <= .1 and
              quenched[-1]['fireCells'] == 0,
              finalMass=quenched[-1]['powderMass'], massAtHalfSecond=at(quenched, .5)['powderMass'])
        water = rows('water-cooling'); dry = rows('dry-cooling')
        wt=at(water, 2)['powderMeanTemperature']; dt=at(dry, 2)['powderMeanTemperature']
        check(name+' G08 water', all(abs(r['powderMass']-64) <= .064 for r in water) and dt-wt >= 5,
              wetTemperatureAt2Seconds=wt, dryTemperatureAt2Seconds=dt, finalMass=water[-1]['powderMass'])
report = dict(passed=all(c['passed'] for c in checks), checks=checks)
(root / 'acceptance.json').write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
for c in checks:
    print(('PASS ' if c['passed'] else 'FAIL ')+c['check']+' '+json.dumps({k:v for k,v in c.items() if k not in ['check','passed']}))
raise SystemExit(0 if report['passed'] else 1)
