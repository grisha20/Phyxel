"""Check paired FPS and the existing cold powder criteria after coal tuning."""
import csv, json, pathlib, sys

root=pathlib.Path(sys.argv[1])
def rows(path):
    with path.open(encoding='utf-8-sig',newline='') as stream:
        return list(csv.DictReader(stream))
checks=[]
def check(name,passed,**values):
    checks.append(dict(check=name,passed=bool(passed),**values))
before={r['Mode']:r for r in rows(root/'performance-paired-before/summary.csv')}
after={r['Mode']:r for r in rows(root/'performance-paired-after/summary.csv')}
for mode in ['Simulation','Sandbox']:
    b,a=before[mode],after[mode]
    ratio=float(a['RealFps'])/float(b['RealFps'])
    check(mode+' paired performance',ratio>=.9 and a['ClockPassed']=='True' and
          b['ClockPassed']=='True',beforeFps=float(b['RealFps']),afterFps=float(a['RealFps']),
          ratio=ratio,simulationRate=float(a['SimulationRate']),airHz=float(a['AirHz']))
    for fps in [30,60,100]:
        cold=rows(root/'powder'/f'{mode}-cold-flame-{fps}.csv')
        large=rows(root/'powder'/f'{mode}-large-contact-{fps}.csv')
        at5=next(r for r in large if abs(float(r['seconds'])-5)<.0001)
        check(f'{mode}-{fps} cold flame',len(cold)>10 and
              all(abs(float(r['powderMass'])-64)<=.064 for r in cold),
              finalMass=float(cold[-1]['powderMass']))
        check(f'{mode}-{fps} large cold front',float(at5['powderMass'])<=10.24,
              massAt5=float(at5['powderMass']))
output=dict(passed=all(c['passed'] for c in checks),checks=checks)
(root/'related-acceptance.json').write_text(json.dumps(output,indent=2)+'\n')
for c in checks: print(('PASS ' if c['passed'] else 'FAIL ')+json.dumps(c))
raise SystemExit(0 if output['passed'] else 1)
