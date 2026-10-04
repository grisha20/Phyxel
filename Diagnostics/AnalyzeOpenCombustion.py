"""Scoped OC acceptance; reuse the existing G03/G08 observable criteria."""
import argparse
import csv
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('root')
root = Path(parser.parse_args().root)
checks = []


def csv_rows(path):
    with path.open(encoding='utf-8-sig', newline='') as stream:
        return list(csv.DictReader(stream))


def check(name, passed, **values):
    checks.append(dict(check=name, passed=bool(passed), **values))


matrix = csv_rows(root / 'open-accepted/summary.csv')
assert len(matrix) == 7, 'Expected six open flames and one obstacle'
for row in matrix:
    name = f"{row['mode']}-{row['fps']}-{row['scene']}"
    if row['scene'] == 'fire_open':
        passed = (int(row['exit']) == 0 and float(row['fireCells']) >= 100
                  and float(row['fireOccupiedH20']) > 0
                  and float(row['fireOccupiedH40']) > 0
                  and float(row['fireWidthH40']) <= 22)
    else:
        passed = (int(row['exit']) == 0 and float(row['contactWidthFraction']) >= .5
                  and float(row['smokeAboveLeft']) >= 1
                  and float(row['smokeAboveRight']) >= 1)
    check(name, passed)

for mode in ['Simulation', 'Sandbox']:
    for fps in [30, 60, 100]:
        def rows(scene):
            return [{key: float(value) for key, value in row.items()}
                    for row in csv_rows(root / f'gunpowder/{mode}-{scene}-{fps}.csv')]

        name = f'{mode}-{fps}'
        cold = rows('cold-flame')
        check(name + ' G08 cold flame',
              all(abs(row['powderMass'] - 64) <= .064 for row in cold)
              and cold[-1]['fireCells'] == 0, finalMass=cold[-1]['powderMass'])
        quenched = rows('quenched')
        tail = [row for row in quenched if row['seconds'] >= .5]
        check(name + ' G08 quench', quenched[-1]['powderMass'] > .1
              and max(row['powderMass'] for row in tail)
              - min(row['powderMass'] for row in tail) <= .1
              and quenched[-1]['fireCells'] == 0,
              finalMass=quenched[-1]['powderMass'])
        contact = rows('contact')
        pressure = max(row['pulsePressure'] for row in contact)
        speed = max(row['speedPeak'] for row in contact)
        pulse_speed = max(row['pulseSpeed'] for row in contact)
        check(name + ' G03 contact', contact[-1]['powderMass'] < .64
              and pressure >= 1 and speed >= 3
              and contact[-1]['pulsePressure'] < .05 * pressure
              and contact[-1]['pulseSpeed'] < .05 * pulse_speed,
              peakPressure=pressure, peakSpeed=speed)

for directory in ['furnace', 'furnace-sandbox']:
    rows = csv_rows(root / directory / 'summary.csv')
    assert len(rows) == (3 if directory == 'furnace' else 1)
    widths = json.loads((root / directory / 'chimney-width.json').read_text())
    for row in rows:
        check(f"{row['Case']}-{row['Fps']} furnace", row['Passed'] == 'True')
    # The analyser itself checks pre-recorded width and carrier-speed limits.
    check(directory + ' width/speed', all(row['CW01Passed'] for row in widths))

for row in csv_rows(root / 'performance-after/summary.csv'):
    check(row['Mode'] + ' 100% clock', row['ClockPassed'] == 'True')

report = dict(passed=all(row['passed'] for row in checks), checks=checks)
(root / 'acceptance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
for row in checks:
    print(('PASS ' if row['passed'] else 'FAIL ') + row['check'])
raise SystemExit(0 if report['passed'] else 1)
