"""Use Resemble AI's public multilingual demo for a voice preview.

Only narration text is sent. Owner captures/references are never uploaded.
This selects the Russian male reference provided by the demo's language menu.
Availability/quotas belong to the demo provider; use neural_voice.py locally
or a licensed production TTS account for a dependable publishing workflow.
"""
from pathlib import Path
import argparse
import json
import urllib.request
import wave
import time
from neural_voice import LINES

BASE='https://resembleai-chatterbox-multilingual-tts.hf.space'


def call(name,data):
    request=urllib.request.Request(BASE+'/gradio_api/call/'+name,data=json.dumps({'data':data}).encode(),headers={'Content-Type':'application/json'})
    event=json.load(urllib.request.urlopen(request,timeout=30))['event_id']
    kind=None
    with urllib.request.urlopen(BASE+'/gradio_api/call/'+name+'/'+event,timeout=180) as stream:
        for raw in stream:
            line=raw.decode().strip()
            if line.startswith('event:'):kind=line[6:].strip()
            if line.startswith('data:') and kind=='error':raise RuntimeError(line[5:])
            if line.startswith('data:') and kind=='complete':return json.loads(line[5:])
    raise RuntimeError('No completed result returned by demo.')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--demo',action='store_true')
    p.add_argument('--limit',type=int,default=len(LINES))
    args=p.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    reference=None
    records=[]
    for i,text in enumerate(LINES[:1] if args.demo else LINES[:args.limit]):
        path=args.output/f'neural_{i:02d}.wav'
        if not path.exists():
            if reference is None:
                reference=call('on_language_change',['ru',None,''])[0]
            print(f'Queued stock Russian voice preview {i+1}.',flush=True)
            for attempt in range(3):
                try:
                    output=call('generate_tts_audio',[text,'ru',reference,.65,.75,1700+i+37*attempt,.3])
                    break
                except RuntimeError as error:
                    if str(error).strip()!='null' or attempt==2:raise
                    print('Demo returned an unspecified generation error; retrying once with a fresh seed.',flush=True)
                    time.sleep(2)
            if not output:raise RuntimeError('No completed audio returned by demo.')
            asset=output[0]
            url=asset.get('url') or (BASE+'/gradio_api/file='+asset['path'])
            urllib.request.urlretrieve(url,path)
            print(f'Saved narration clip {i+1}.',flush=True)
        with wave.open(str(path),'rb') as wav:
            length=wav.getnframes()/wav.getframerate()
        records.append(dict(index=i,text=text,file=path.name,duration=length))
    (args.output/'narration.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
    (args.output/'voice_provenance.json').write_text(json.dumps(dict(provider='ResembleAI official public Chatterbox Multilingual demo',endpoint=BASE,reference='Language-specific default Russian male demo reference',source='https://github.com/resemble-ai/chatterbox/blob/master/multilingual_app.py',note='MIT model license; production rights for the demo speaker recording separately unverified. Preview only.'),ensure_ascii=False,indent=2),encoding='utf-8')
    print('Voice previews ready.',flush=True)


if __name__=='__main__':main()
