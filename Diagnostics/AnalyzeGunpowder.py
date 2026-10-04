"""Observable acceptance, with the pre-recorded criteria in the passport."""
import argparse,csv,json,pathlib
p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('--matrix-only',action='store_true');a=p.parse_args();root=pathlib.Path(a.root)
def load(directory):
    out={}
    for file in (root/directory).glob('*.csv'):
        with file.open(encoding='utf-8-sig',newline='') as f:
            rows=[{k:float(v) for k,v in row.items()} for row in csv.DictReader(f)]
        if rows: out[file.stem]=rows
    return out
matrix=load('matrix-final');rooms=load('enclosure-final');dose=load('dose-final');baseline=load('baseline-revised');contact=load('cold-contact')
results=[]
def check(name,ok,**values): results.append({'check':name,'passed':bool(ok),**values})
def peak(rows,key): return max(r[key] for r in rows)
def integral(rows,key,start=0): return sum(r[key]*.1 for r in rows if r['seconds']>=start)
for name,rows in contact.items():
    check(name+' G03 contact',rows[-1]['powderMass']<.64 and peak(rows,'pulsePressure')>=1 and peak(rows,'speedPeak')>=3 and
          rows[-1]['pulsePressure']<.05*peak(rows,'pulsePressure') and rows[-1]['pulseSpeed']<.05*peak(rows,'pulseSpeed'),
          peakPressure=peak(rows,'pulsePressure'),peakSpeed=peak(rows,'speedPeak'),
          consumedAt=next((r['seconds'] for r in rows if r['powderMass']<.001),None))
for name,rows in matrix.items():
    if '-open-' in name:
        check(name+' G03',rows[-1]['powderMass']<.64 and peak(rows,'pulsePressure')>=1 and peak(rows,'speedPeak')>=3 and
              rows[-1]['pulsePressure']<.05*peak(rows,'pulsePressure') and rows[-1]['pulseSpeed']<.05*peak(rows,'pulseSpeed'),
              peakPressure=peak(rows,'pulsePressure'),peakSpeed=peak(rows,'speedPeak'),tailPulseSpeed=rows[-1]['pulseSpeed'])
    if '-furnace-' in name and '-control-' not in name:
        mode,_,fps=name.split('-');control=matrix[f'{mode}-furnace-control-{fps}'];ratio=peak(rows,'pipeUpSpeed')/peak(control,'pipeUpSpeed')
        heatRatio=peak(rows,'pipeHeatFlux')/max(1e-20,peak(control,'pipeHeatFlux'))
        check(name+' G04',(ratio>=1.15 or heatRatio>=1.25) and max(r['pulseSpeed'] for r in rows if r['seconds']>=13)<.1,
              pipePeak=peak(rows,'pipeUpSpeed'),controlPipePeak=peak(control,'pipeUpSpeed'),pipeRatio=ratio,heatRatio=heatRatio)
for mode in ['Simulation','Sandbox']:
    if not a.matrix_only:
        closed=rooms[f'{mode}-closed-60'];vented=rooms[f'{mode}-vented-60'];cold=rooms[f'{mode}-cold-60'];sealed=rooms[f'{mode}-furnace-sealed-60']
        c=integral(closed,'pulsePressureStock',3);v=integral(vented,'pulsePressureStock',3)
        check(mode+' G06',c>v and peak(closed,'outsidePulse')<1e-5,closedPressureIntegral=c,ventedPressureIntegral=v,outsidePressure=peak(closed,'outsidePulse'))
        check(mode+' G01',all(abs(r['powderMass']-64)<.001 and r['fireCells']==0 for r in cold))
        check(mode+' G05',peak(sealed,'pipeUpSpeed')>.1 and sealed[-1]['pulseSpeed']<.1,pipePeak=peak(sealed,'pipeUpSpeed'))
    for scene in ['open','furnace']:
        cases=[matrix[f'{mode}-{scene}-{fps}'] for fps in [30,60,100]]
        for metric,values in [('speedPeak',[peak(r,'speedPeak') for r in cases]),
                              ('pressureIntegral',[integral(r,'pulsePressureStock') for r in cases]),
                              ('speedIntegral',[integral(r,'speedPeak') for r in cases])]:
            spread=max(values)/min(values)-1
            check(f'{mode}-{scene} G09 {metric}',spread<=.2,values=values,spread=spread)
        check(f'{mode}-{scene} G09 fuel',all(r[-1]['powderMass']<.001 for r in cases))
    if dose and not a.matrix_only:
        rows=dose[f'{mode}-furnace-large-60'];check(mode+' large-dose',rows[-1]['powderMass']<.01,
             pipePeak=peak(rows,'pipeUpSpeed'),pipeFirePeak=peak(rows,'pipeFire'),speedPeak=peak(rows,'speedPeak'))
report={'passed':all(r['passed'] for r in results),'checks':results,
        'baseline':{n:{'pressure':peak(r,'pressurePeak'),'speed':peak(r,'speedPeak'),'pipe':peak(r,'pipeUpSpeed')} for n,r in baseline.items()}}
(root/('matrix-acceptance.json' if a.matrix_only else 'acceptance.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
for r in results:
    print(('PASS ' if r['passed'] else 'FAIL ')+r['check']+' '+json.dumps({k:v for k,v in r.items() if k not in ['check','passed']},ensure_ascii=False))
raise SystemExit(0 if report['passed'] else 1)
