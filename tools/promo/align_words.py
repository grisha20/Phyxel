"""Align the supplied narration script against offline Russian ASR word times.

Uses Vosk's small Russian model; no audio is uploaded. Unrecognized words
retain the original script and receive times interpolated between anchors.
Reports preserve the raw recognition so timing corrections are reviewable.
"""
from pathlib import Path
import argparse
import json
import re
import wave
import difflib
import subprocess


def normalized(word):
    return re.sub(r'[^а-яa-z0-9]','',word.lower().replace('ё','е'))


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--work',type=Path,required=True)
    p.add_argument('--model',type=Path,required=True)
    p.add_argument('--ffmpeg',default='ffmpeg')
    args=p.parse_args()
    import vosk
    vosk.SetLogLevel(-1)
    model=vosk.Model(str(args.model))
    data=json.loads((args.work/'timing.json').read_text(encoding='utf-8'))
    words=[];reports=[]
    for i,voice in enumerate(data['voices']):
        wav=args.work/f'align_{i:02d}.wav'
        subprocess.run([args.ffmpeg,'-loglevel','error','-y','-i',str(args.work/f'voice_{i:02d}.wav'),'-ar','16000','-ac','1',str(wav)],check=True)
        rec=vosk.KaldiRecognizer(model,16000);rec.SetWords(True)
        recognized=[];texts=[]
        with wave.open(str(wav),'rb') as file:
            while chunk:=file.readframes(4000):
                if rec.AcceptWaveform(chunk):
                    result=json.loads(rec.Result());recognized+=result.get('result',[]);texts.append(result.get('text',''))
        result=json.loads(rec.FinalResult());recognized+=result.get('result',[]);texts.append(result.get('text',''))
        expected=voice['text'].split()
        matcher=difflib.SequenceMatcher(a=[normalized(w) for w in expected],b=[normalized(w['word']) for w in recognized],autojunk=False)
        aligned=[None]*len(expected)
        matched=0
        for block in matcher.get_matching_blocks():
            for j in range(block.size):
                aligned[block.a+j]=dict(start=recognized[block.b+j]['start'],end=recognized[block.b+j]['end']);matched+=1
        cursor=0
        while cursor<len(expected):
            if aligned[cursor] is not None:cursor+=1;continue
            first=cursor
            while cursor<len(expected) and aligned[cursor] is None:cursor+=1
            start=aligned[first-1]['end'] if first else 0
            end=aligned[cursor]['start'] if cursor<len(expected) else voice['duration']
            if end<=start:end=start+.05*(cursor-first)
            weight=sum(max(2,len(w)) for w in expected[first:cursor])
            position=start
            for j in range(first,cursor):
                finish=position+(end-start)*max(2,len(expected[j]))/weight
                aligned[j]=dict(start=position,end=finish);position=finish
        for j,(text,time) in enumerate(zip(expected,aligned)):
            start=max(0,time['start']);end=max(start+.07,time['end'])
            next_start=aligned[j+1]['start'] if j+1<len(aligned) else voice['duration']
            # Fill tiny silence gaps without allowing adjacent words to overlap.
            if next_start>start:end=min(max(end,next_start-.02),next_start)
            words.append(dict(word=text,start=round(voice['at']+start,3),end=round(voice['at']+end,3)))
        reports.append(dict(index=i,script=voice['text'],recognized=' '.join(texts).strip(),matched_words=matched,total_words=len(expected)))
    (args.work/'words.json').write_text(json.dumps(words,ensure_ascii=False,indent=2),encoding='utf-8')
    (args.work/'recognition.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(reports,ensure_ascii=True,indent=2))


if __name__=='__main__':main()
