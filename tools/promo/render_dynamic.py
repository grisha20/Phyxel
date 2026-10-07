"""Portrait gameplay-led edit with camera movement, transitions and subtitles.

Run after neural_voice.py. Requires Pillow, NumPy and FFmpeg. Assets and
renders stay in the output folder; source captures remain untouched.
"""
from pathlib import Path
import argparse
import json
import subprocess
import shutil
import wave
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFont

FPS=30
W,H=1080,1920
TRANSITION=5/FPS


def run(args):
    subprocess.run([str(a) for a in args],check=True)


def duration(ffprobe,path):
    return float(subprocess.check_output([ffprobe,'-v','error','-show_entries','format=duration','-of','default=noprint_wrappers=1:nokey=1',str(path)]))


def graphics(path):
    im=Image.new('RGBA',(W,H),(0,0,0,0));d=ImageDraw.Draw(im)
    # Tiny brand remains visible; gameplay occupies the frame.
    d.rounded_rectangle((63,157,275,221),radius=16,fill=(7,13,20,155))
    d.polygon([(89,174),(105,190),(89,206),(73,190)],outline='#5de1fa',width=3)
    d.text((119,171),'PHYXEL',font=ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf',30),fill='white')
    d.text((72,1728),'ИГРА В РАЗРАБОТКЕ',font=ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf',26),fill=(255,255,255,200),stroke_width=2,stroke_fill=(0,0,0,150))
    im.save(path)


def make_ass(path,voices,transcript=None):
    head='''[Script Info]
ScriptType: v4.00+
PlayResX: 1080
PlayResY: 1920
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Word,Arial,78,&H00FFFFFF,&H00FFFFFF,&H00100A06,&H90000000,-1,0,0,0,100,100,0,0,1,6,3,5,80,180,0,1
Style: End,Arial,95,&H00FFFFFF,&H00FFFFFF,&H00100A06,&H90000000,-1,0,0,0,100,100,0,0,1,6,3,5,80,150,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
'''
    def stamp(t):
        ticks=round(t*100)
        return f'{ticks//360000}:{ticks//6000%60:02d}:{ticks//100%60:02d}.{ticks%100:02d}'
    events=[]
    if transcript:
        words=json.loads(transcript.read_text(encoding='utf-8'))
    else:
        # Fallback only; align_words.py supplies measured ASR word timings.
        words=[]
        for voice in voices:
            split=voice['text'].split();weights=[max(2,len(w)) for w in split];total=sum(weights)
            t=voice['at']+.06
            for word,weight in zip(split,weights):
                delta=(voice['duration']-.1)*weight/total
                words.append(dict(word=word,start=t,end=t+delta));t+=delta
    for word in words:
        text=word['word'].strip().upper().replace('{','').replace('}','')
        if not text:continue
        size=78 if len(text)<14 else 66
        # Short movement and scale settle gives emphasis without shaking text.
        y=1380 if voices[1]['at'] <= word['start'] < voices[1]['at']+voices[1]['duration'] else 1170
        effect=r'{\an5\pos(515,'+str(y)+r')\fs'+str(size)+r'\fscx108\fscy108\t(0,90,\fscx100\fscy100)\fad(25,35)}'
        events.append(f'Dialogue: 2,{stamp(word["start"])},{stamp(word["end"])},Word,,0,0,0,,{effect}{text}')
    end=voices[-1]['at']+voices[-1]['duration']+3.0 if len(voices)==3 else voices[-1]['at']
    events.append(f'Dialogue: 1,{stamp(end)},{stamp(end+2.2)},End,,0,0,0,,'+r'{\an5\pos(515,650)\fad(180,200)}PHYXEL')
    events.append(f'Dialogue: 1,{stamp(end)},{stamp(end+2.2)},Word,,0,0,0,,'+r'{\an5\pos(515,1250)\fs58\fad(180,200)}ЧТО ПРОВЕРИТЬ\NСЛЕДУЮЩИМ?')
    path.write_text(head+'\n'.join(events)+'\n',encoding='utf-8-sig')


def music(path,length,boom_at):
    sr=48000;t=np.arange(round(length*sr))/sr
    track=np.zeros((len(t),2));rng=np.random.default_rng(7214)
    def add(at,y,pan=0):
        s=round(at*sr);n=min(len(y),len(t)-s)
        if n<1:return
        track[s:s+n,0]+=y[:n]*math.sqrt((1-pan)/2)
        track[s:s+n,1]+=y[:n]*math.sqrt((1+pan)/2)
    beat=60/128
    for i,at in enumerate(np.arange(0,length,beat)):
        if boom_at-.7<at<boom_at+.35:continue
        u=np.arange(round(.32*sr))/sr
        add(at,.10*np.sin(2*np.pi*(48*u+9*(1-np.exp(-32*u))))*np.exp(-17*u))
        if i%2:
            u=np.arange(round(.13*sr))/sr
            add(at,.028*rng.normal(size=len(u))*np.exp(-35*u),-.2)
        u=np.arange(round(.055*sr))/sr
        for fraction in [.25,.5,.75]:
            add(at+beat*fraction,.011*rng.normal(size=len(u))*np.exp(-70*u),.3)
        frequency=[110,110,130.81,98][i//4%4]
        u=np.arange(round(.3*sr))/sr
        add(at,.034*np.sin(2*np.pi*frequency*u)*(1-np.exp(-90*u))*np.exp(-12*u))
        if i%2==0:
            u=np.arange(round(.35*sr))/sr
            hz=[440,523.25,659.25,587.33][i//2%4]
            add(at,.012*np.sin(2*np.pi*hz*u)*np.exp(-12*u),(-1 if i%4 else 1)*.6)
    # Editorial riser/impact; these are not represented as native game sounds.
    u=np.arange(round(.7*sr))/sr
    add(boom_at-.7,.045*rng.normal(size=len(u))*np.linspace(0,1,len(u))**2)
    u=np.arange(round(1.5*sr))/sr
    noise=np.convolve(rng.normal(size=len(u)),np.ones(21)/21,'same')
    add(boom_at,.37*(np.sin(2*np.pi*(38*u+6*(1-np.exp(-10*u))))*np.exp(-5*u)+noise*np.exp(-3*u)))
    for at in np.arange(1.4,length-1,2.0):
        u=np.arange(round(.14*sr))/sr
        add(at,.012*rng.normal(size=len(u))*np.sin(np.pi*u/.14)**2,.4)
    track*=np.minimum(1,t/.1)[:,None]*np.clip((length-t)/.5,0,1)[:,None]
    with wave.open(str(path),'wb') as wav:
        wav.setnchannels(2);wav.setsampwidth(2);wav.setframerate(sr)
        wav.writeframes((np.clip(track,-.95,.95)*32767).astype('<i2').tobytes())


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--voice',type=Path,required=True)
    p.add_argument('--ffmpeg',default=shutil.which('ffmpeg') or 'ffmpeg')
    p.add_argument('--ffprobe',default=shutil.which('ffprobe') or 'ffprobe')
    p.add_argument('--words',type=Path)
    p.add_argument('--picture-only',action='store_true',help='Rebuild picture using previously prepared narration/timeline')
    args=p.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    work=args.output/'dynamic_work';work.mkdir(exist_ok=True)
    ff=args.ffmpeg;probe=args.ffprobe
    voices=json.loads((args.voice/'narration.json').read_text(encoding='utf-8'))
    at=0.12
    for i,v in enumerate(voices):
        prepared=work/f'voice_{i:02d}.wav'
        if not args.picture_only:
            run([ff,'-hide_banner','-loglevel','error','-y','-i',args.voice/v['file'],
                 '-af','silenceremove=start_periods=1:start_duration=0.02:start_threshold=-45dB,areverse,silenceremove=start_periods=1:start_duration=0.02:start_threshold=-45dB,areverse,atempo=1.08',
                 '-ar','48000','-ac','2',prepared])
        v['duration']=duration(probe,prepared);v['at']=round(at,3)
        # Hold briefly after the setup; the explosion gets its own sound beat.
        at+=v['duration']+(2.0 if i==4 else .12)
    if len(voices)==3:
        bomb_start=voices[-1]['at']+voices[-1]['duration']+.1
        total=math.ceil((bomb_start+5.2)*FPS)/FPS
    else:
        total=math.ceil((at+.6)*FPS)/FPS
        bomb_start=voices[4]['at']+voices[4]['duration']+.05
    groups=[
        [('Desktop 2026.07.16 - 00.41.18.12.mp4',4.5,'focus',[400,35,342,608],.65),
         ('Desktop 2026.10.07 - 18.43.04.13.mp4',4.1,'focus',[470,35,304,540],.35)],
        [('photo_2026-10-07_22-21-28.jpg',0,'wide',None,.3),
         ('Desktop 2026.10.05 - 13.24.53.10.mp4',10.6,'focus',[417,35,304,540],.25),
         ('Desktop 2026.07.16 - 00.41.18.12.mp4',8.7,'focus',[400,35,342,608],.2),
         ('Desktop 2026.10.04 - 17.43.03.08.mp4',18.5,'wide',None,.25)],
        [('Desktop 2026.10.03 - 18.06.33.06.mp4',.4,'focus',[420,35,304,540],.4),
         ('Desktop 2026.10.03 - 12.59.15.03.mp4',8.1,'focus',[380,35,304,540],.3),
         ('photo_2026-10-07_22-21-36.jpg',0,'photo',[280,85,1825,1060],.3)],
        [('photo_2026-10-07_22-21-28.jpg',0,'photo',[565,85,1540,1060],.55),
         ('photo_2026-10-07_22-21-32.jpg',0,'photo',[565,85,1540,1060],.45)],
        [('Desktop 2026.10.07 - 18.40.03.12.mp4',.1,'focus',[380,35,304,540],.45),
         ('Desktop 2026.10.07 - 18.40.03.12.mp4',.85,'focus',[380,35,304,540],.55)],
        [('Desktop 2026.10.07 - 18.43.04.13.mp4',4.28,'wide',None,1.0)],
        [('Desktop 2026.10.07 - 18.40.03.12.mp4',3.2,'focus',[380,35,304,540],1.0)],
    ]
    if len(voices)==3:
        groups=[groups[0],groups[1],groups[2][:1]+[
            ('photo_2026-10-07_22-21-28.jpg',0,'photo',[565,85,1540,1060],.3),
            groups[2][-1]],groups[5],groups[6]]
        groups[2][0]=(*groups[2][0][:4],.35)
        groups[2][1]=(*groups[2][1][:4],.35)
        group_starts=[0,voices[1]['at'],voices[2]['at'],bomb_start,bomb_start+3.0]
    else:
        group_starts=[0]+[v['at'] for v in voices[1:5]]+[bomb_start,voices[5]['at']]
    group_ends=group_starts[1:]+[total]
    shots=[]
    for group,start,end in zip(groups,group_starts,group_ends):
        pos=start
        for j,(file,source,mode,crop,weight) in enumerate(group):
            finish=end if j==len(group)-1 else pos+(end-start)*weight
            shots.append(dict(file=file,source=source,mode=mode,crop=crop,at=pos,end=finish,duration=finish-pos))
            pos=finish
    # Quantize cut positions before creating the overlaps.
    for s in shots:
        s['at']=round(s['at']*FPS)/FPS;s['end']=round(s['end']*FPS)/FPS
        s['duration']=s['end']-s['at']
    for i,s in enumerate(shots):
        print(f'Dynamic shot {i+1}/{len(shots)}',flush=True)
        source=args.source/s['file'];still=source.suffix.lower() in ['.jpg','.png']
        length=s['duration']+(TRANSITION if i<len(shots)-1 else 0)
        inputs=['-loop','1','-framerate',str(FPS),'-i',source] if still else ['-ss',str(s['source']),'-i',source]
        n=round(length*FPS)
        zoom=f"zoompan=z='min(1.14,1+0.12*on/{max(1,n)})':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=1080x1920:fps={FPS}"
        if s['mode']=='focus':
            x,y,cw,ch=s['crop']
            graph=f'[0:v]crop={cw}:{ch}:{x}:{y},fps={FPS},scale=1080:1920,{zoom},setsar=1[v]'
        else:
            crop=f'crop={s["crop"][2]}:{s["crop"][3]}:{s["crop"][0]}:{s["crop"][1]},' if s['crop'] else ''
            image_h=round(1080*(s['crop'][3]/s['crop'][2] if s['crop'] else 9/16)/2)*2
            graph=(f'[0:v]{crop}fps={FPS},split=2[foreground][background];'
                   '[background]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,gblur=sigma=40,eq=brightness=-0.20:saturation=0.65[bg];'
                   f'[foreground]scale=1080:{image_h},setsar=1[fg];'
                   f'[bg][fg]overlay=0:(H-h)/2,{zoom},setsar=1[v]')
        if i==len(shots)-1:
            graph=graph.replace('[v]',',eq=brightness=-0.16[v]')
        run([ff,'-hide_banner','-loglevel','error','-y',*inputs,'-filter_complex',graph,'-map','[v]',
             '-t',str(length),'-an','-c:v','libx264','-preset','fast','-crf','18','-threads','4',work/f'scene_{i:02d}.mp4'])
    inputs=[]
    for i in range(len(shots)):inputs+=['-i',work/f'scene_{i:02d}.mp4']
    filters=[];prior='0:v'
    for i in range(1,len(shots)):
        transition='smoothleft' if i in [1,4,7,10] else 'fade'
        filters.append(f'[{prior}][{i}:v]xfade=transition={transition}:duration={TRANSITION}:offset={shots[i]["at"]}[x{i}]')
        prior=f'x{i}'
    picture=work/'picture.mp4'
    run([ff,'-hide_banner','-loglevel','error','-y',*inputs,'-filter_complex_threads','1','-filter_complex',';'.join(filters),'-map',f'[{prior}]','-t',str(total),'-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-threads','4',picture])
    ass=work/'captions.ass';make_ass(ass,voices,args.words)
    brand=work/'brand.png';graphics(brand)
    music_path=work/'music.wav';music(music_path,total,bomb_start)
    audio_inputs=['-i',music_path]
    af=[]
    for i,v in enumerate(voices):
        audio_inputs+=['-i',work/f'voice_{i:02d}.wav']
        af.append(f'[{i+1}:a]highpass=f=75,lowpass=f=11000,loudnorm=I=-18:TP=-2:LRA=7,aresample=48000,adelay={round(v["at"]*1000)}:all=1[a{i}]')
    af.append('[0:a]'+''.join(f'[a{i}]' for i in range(len(voices)))+f'amix=inputs={len(voices)+1}:duration=first:normalize=0,loudnorm=I=-15:TP=-1.5:LRA=8[mix]')
    mix=work/'mix.wav'
    run([ff,'-hide_banner','-loglevel','error','-y',*audio_inputs,'-filter_complex',';'.join(af),'-map','[mix]','-ar','48000','-ac','2','-t',str(total),mix])
    # Run from work so the ASS file requires no platform-dependent path escaping.
    command=[ff,'-hide_banner','-loglevel','error','-y','-i',str(picture.resolve()),'-loop','1','-i',str(brand.resolve()),'-i',str(mix.resolve()),
             '-filter_complex','[0:v][1:v]overlay=0:0,ass=captions.ass[v]','-map','[v]','-map','2:a','-t',str(total),
             '-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-threads','4','-c:a','aac','-b:a','192k','-movflags','+faststart',str((args.output/'Phyxel_Shorts_RU_v2.mp4').resolve())]
    subprocess.run(command,check=True,cwd=work)
    (work/'timing.json').write_text(json.dumps(dict(duration=total,voices=voices,shots=shots,bomb_at=bomb_start),ensure_ascii=False,indent=2),encoding='utf-8')
    print('Dynamic render ready.',flush=True)


if __name__=='__main__':
    main()
