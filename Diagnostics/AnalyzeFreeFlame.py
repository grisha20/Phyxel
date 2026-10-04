"""Physical widths from the existing FireOpen ASCII fixture, excluding glow."""
import argparse
import json
from pathlib import Path
import re
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('root', type=Path)
parser.add_argument('--candidate', default='final')
parser.add_argument('--require-pass', action='store_true')
args = parser.parse_args()
results = []

def local_width(mask):
    widths = []
    # Twelve five-row windows y90..149 above the source at y170.
    for y in range(0, 60, 5):
        histogram = mask[y:y + 5].sum(axis=0)
        total = histogram.sum()
        if total:
            quantiles = np.searchsorted(histogram.cumsum(), np.array([.05, .95]) * total)
            widths.append(int(quantiles[1] - quantiles[0] + 1))
    return float(np.mean(widths)) if widths else 0

for path in sorted(args.root.glob('*/*-60/fire-open-ascii.txt')):
    lines = path.read_text().splitlines()
    bounds = re.search(r'x=(\d+)\.\.(\d+); y=(\d+)\.\.(\d+)', lines[0])
    if not bounds:
        raise ValueError(f'Missing physical bounds: {path}')
    left, right, top, bottom = map(int, bounds.groups())
    grid = np.array([list(line) for line in lines[1:]])
    if grid.shape != (bottom - top + 1, right - left + 1) or top > 90 or bottom < 149:
        raise ValueError(f'Invalid FireOpen fixture: {path}')
    band = grid[90 - top:150 - top]
    report = (path.parent / 'acceptance-report.txt').read_text(encoding='utf-8-sig')
    metrics = {key: float(value) for key, value in re.findall(
        r'\b(fireCells|fireWidthH20|fireSmokeWidthH20|fireWidthH40|fireSmokeWidthH40)=([\d.]+)', report)}
    entry = {'case': path.parent.relative_to(args.root).as_posix(),
             'localFire90Width': local_width(band == 'F'),
             # Gas width here is occupied-cell width, not mixed gas mass width.
             'localGas90Width': local_width((band == 'F') | (band == 'S')),
             'fireBandCount': int((band == 'F').sum()), **metrics}
    results.append(entry)
    print(json.dumps(entry))

case = next((row for row in results if row['case'] == f'{args.candidate}/Sandbox-60'), None)
passed = (case is not None and 0 < case['localFire90Width'] <= 8.0 and
          0 < case['localGas90Width'] <= 10.0 and case['fireCells'] > 0)
output = {'FW01Passed': bool(passed), 'candidate': args.candidate, 'results': results}
(args.root / 'open-width.json').write_text(json.dumps(output, indent=2), encoding='utf-8')
if args.require_pass and not passed:
    raise SystemExit('FW01 failed; do not weaken the fixed thresholds')
