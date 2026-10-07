"""Render the first Phyxel portrait teaser from owner-provided captures.

Requires FFmpeg, Pillow, NumPy and Windows Arial fonts. All generated assets
stay in the requested output directory. No game source or footage is modified.
"""
from pathlib import Path
import argparse
import json
import math
import shutil
import subprocess
import wave
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FPS = 30
W, H = 1080, 1920
DURATION = 27.2
CYAN = '#5de1fa'
ORANGE = '#ffb25e'
WHITE = '#f4f7fb'

# crop boxes refer to original source pixels, before any scaling.
SHOTS = [
    dict(file='Desktop 2026.07.16 - 00.41.18.12.mp4', start=4.6, duration=3.6,
         crop=[250, 45, 690, 595], speed=1.65, title=['НАЧАЛ', 'С ВОДЫ'],
         label='01 / ПЕРВЫЕ ЭКСПЕРИМЕНТЫ', caption='Пишу свою физическую песочницу.', color=CYAN),
    dict(file='Desktop 2026.10.03 - 18.06.33.06.mp4', start=0.5, duration=3.0,
         crop=[310, 55, 560, 505], speed=1.0, title=['ДОБАВИЛ', 'ОГОНЬ'],
         label='02 / ОГОНЬ И НАГРЕВ', caption='Сначала — огонь и нагрев.', color=ORANGE),
    dict(file='photo_2026-10-07_22-21-28.jpg', start=0, duration=3.0,
         crop=[0, 0, 2560, 1440], speed=1.0, title=['ВЫБИРАЕШЬ', 'МАТЕРИАЛЫ'],
         label='03 / ИНТЕРФЕЙС И ИНСТРУМЕНТЫ', caption='Материалы и инструменты под рукой.',
         color=CYAN, interface=True),
    dict(file='photo_2026-10-07_22-21-28.jpg', start=0, duration=3.0,
         crop=[565, 85, 1540, 1060], speed=1, title=['СОБРАЛ', 'ПЕЧКУ'],
         label='04 / СВОИ КОНСТРУКЦИИ', caption='Строишь. Нагреваешь. Наблюдаешь.', color=ORANGE),
    dict(file='photo_2026-10-07_22-21-36.jpg', start=0, duration=2.0,
         crop=[280, 85, 1825, 1060], speed=1, title=['ДВИЖЕНИЕ', 'ГАЗОВ'],
         label='05 / ВНУТРИ СИМУЛЯЦИИ', caption='Потоки воздуха — прямо на экране.', color=CYAN),
    dict(file='Desktop 2026.10.07 - 18.40.03.12.mp4', start=0.0, duration=3.4,
         crop=[310, 55, 565, 510], speed=1.0, title=['А ПОТОМ…'],
         label='06 / ЕЩЁ ОДИН ЭКСПЕРИМЕНТ', caption='Решил проверить одну идею.', color=ORANGE),
    dict(file='Desktop 2026.10.07 - 18.43.04.13.mp4', start=3.65, duration=5.6,
         crop=[190, 45, 860, 525], speed=0.68, title=['И ВОТ', 'ЧТО ВЫШЛО'],
         label='07 / ФИНАЛЬНАЯ ПРОВЕРКА', caption='Эксперименты продолжаются.', color=ORANGE),
    dict(file='Desktop 2026.10.07 - 18.40.03.12.mp4', start=2.4, duration=3.6,
         crop=[270, 45, 640, 525], speed=1.0, title=['PHYXEL'],
         label='ФИЗИЧЕСКАЯ ПЕСОЧНИЦА В РАЗРАБОТКЕ',
         caption='Что проверить следующим?', color=CYAN, outro=True),
]
VOICE_STARTS = [0.25, 3.8, 6.9, 9.75, 14.85, 23.85]
VOICE_WINDOWS = [3.1, 2.65, 2.4, 4.6, 3.0, 3.1]


def run(command):
    subprocess.run([str(v) for v in command], check=True)


def font(size, bold=False):
    return ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf' if bold else
                              'C:/Windows/Fonts/arial.ttf', size)


def fit(draw, text, size, max_width, bold=True):
    while draw.textlength(text, font=font(size, bold)) > max_width:
        size -= 1
    return font(size, bold)


def wrap(draw, text, f, max_width):
    lines, current = [], ''
    for word in text.split():
        candidate = (current + ' ' + word).strip()
        if current and draw.textlength(candidate, font=f) > max_width:
            lines.append(current)
            current = word
        else:
            current = candidate
    return lines + [current]


def plate(shot, index, path):
    # Original typography/geometry; no third-party image or audio assets.
    yy, xx = np.mgrid[0:H, 0:W]
    glow = np.exp(-((xx-950)**2/(600**2) + (yy-400)**2/(750**2)))
    rgb = np.zeros((H, W, 3), dtype=np.uint8)
    for channel, base in enumerate([8, 14, 23]):
        rgb[:, :, channel] = base + glow * [4, 15, 22][channel]
    img = Image.fromarray(rgb).convert('RGBA')
    d = ImageDraw.Draw(img)
    for x in range(40, W, 80):
        for y in range(70, H, 80):
            d.ellipse((x,y,x+2,y+2), fill=(93,225,250,28))
    d.polygon([(83,176),(101,194),(83,212),(65,194)], outline=CYAN, width=3)
    d.text((123,171), 'PHYXEL', font=font(36,True), fill=WHITE)
    d.text((75,256), shot['label'], font=fit(d,shot['label'],24,850), fill=shot['color'])
    title_size = 110 if shot.get('outro') else 94
    for n, line in enumerate(shot['title']):
        d.text((70,318+n*104), line, font=fit(d,line,title_size,880),
               fill=shot['color'] if n == len(shot['title'])-1 else WHITE)
    if shot.get('outro'):
        d.text((76,443), 'Создавай свои эксперименты.', font=font(38), fill=WHITE)
    # The footage overlays this opening; captions remain outside gameplay.
    d.rounded_rectangle((45,565,1035,1515), radius=30, fill='#070a0f', outline='#293949', width=2)
    d.line((73,1516,1007,1516), fill=shot['color'], width=3)
    caption_font = font(48,True)
    for n, line in enumerate(wrap(d,shot['caption'],caption_font,835)):
        d.text((74,1561+n*60), line, font=caption_font, fill=WHITE)
    d.text((75,1722), 'ИГРА В РАЗРАБОТКЕ', font=font(24), fill='#8295a6')
    d.text((75,1770), 'Подпишись, чтобы следить за развитием.', font=font(27), fill='#a5b5c5')
    for i in range(len(SHOTS)):
        x=75+i*112
        d.rounded_rectangle((x,1830,x+96,1836),radius=3,fill=shot['color'] if i<=index else '#24333f')
    img.save(path)


def prepare_still(source, shot, path):
    x,y,w,h=shot['crop']
    im=Image.open(source).convert('RGB').crop((x,y,x+w,y+h))
    im.save(path)


def interface_still(source, path):
    im=Image.open(source).convert('RGB')
    hero=Image.new('RGB',(984,944),'#070a0f')
    hero.paste(im.resize((984,554),Image.Resampling.LANCZOS),(0,35))
    draw=ImageDraw.Draw(hero)
    draw.text((32,645),'ПАЛИТРА МАТЕРИАЛОВ',font=font(25,True),fill=CYAN)
    detail=im.crop((20,1220,1270,1388)).resize((924,124),Image.Resampling.LANCZOS)
    hero.paste(detail,(30,700))
    hero.save(path)


def export_resolve_xml(work, output, manifest):
    # FCP 7 XML: independent rendered shots and the exact mixed audio track.
    # Text/crops are baked into each shot; raw filenames and crop geometry are
    # preserved in edit_manifest.json for replacing any shot during revision.
    root=ET.Element('xmeml',version='5')
    sequence=ET.SubElement(root,'sequence',id='phyxel-short-v1')
    def field(parent,name,value):
        ET.SubElement(parent,name).text=str(value)
    def rate(parent):
        r=ET.SubElement(parent,'rate');field(r,'timebase',FPS);field(r,'ntsc','FALSE')
    field(sequence,'name','Phyxel Shorts RU v1')
    field(sequence,'duration',round(DURATION*FPS));rate(sequence)
    media=ET.SubElement(sequence,'media');video=ET.SubElement(media,'video')
    fmt=ET.SubElement(video,'format');sc=ET.SubElement(fmt,'samplecharacteristics')
    rate(sc);field(sc,'width',W);field(sc,'height',H)
    field(sc,'anamorphic','FALSE');field(sc,'pixelaspectratio','square');field(sc,'fielddominance','none')
    track=ET.SubElement(video,'track')
    for i,s in enumerate(manifest):
        frames=round(s['duration']*FPS);begin=round(s['at']*FPS)
        clip=ET.SubElement(track,'clipitem',id=f'shot-{i}')
        field(clip,'name',f'{i+1:02d} '+' / '.join(s['title']))
        field(clip,'duration',frames);rate(clip)
        field(clip,'start',begin);field(clip,'end',begin+frames)
        field(clip,'in',0);field(clip,'out',frames)
        file=ET.SubElement(clip,'file',id=f'file-{i}')
        field(file,'name',f'shot_{i:02d}.mp4')
        field(file,'pathurl',(work/f'shot_{i:02d}.mp4').resolve().as_uri())
        rate(file);field(file,'duration',frames)
        fm=ET.SubElement(file,'media');fv=ET.SubElement(fm,'video')
        ET.SubElement(fv,'samplecharacteristics').extend(list(ET.fromstring(ET.tostring(sc))))
        st=ET.SubElement(clip,'sourcetrack');field(st,'mediatype','video');field(st,'trackindex',1)
    audio=ET.SubElement(media,'audio')
    af=ET.SubElement(audio,'format');asc=ET.SubElement(af,'samplecharacteristics')
    field(asc,'depth',16);field(asc,'samplerate',48000)
    field(audio,'numOutputChannels',2)
    for channel in [1,2]:
        at=ET.SubElement(audio,'track');clip=ET.SubElement(at,'clipitem',id=f'audio-{channel}')
        field(clip,'name','Russian narration + original music / effects')
        frames=round(DURATION*FPS)
        field(clip,'duration',frames);rate(clip)
        field(clip,'start',0);field(clip,'end',frames);field(clip,'in',0);field(clip,'out',frames)
        file=ET.SubElement(clip,'file',id='audio-mix')
        if channel==1:
            field(file,'name','mix.wav');field(file,'pathurl',(work/'mix.wav').resolve().as_uri())
            rate(file);field(file,'duration',frames)
            fm=ET.SubElement(file,'media');fa=ET.SubElement(fm,'audio')
            field(fa,'channelcount',2)
            ET.SubElement(fa,'samplecharacteristics').extend(list(ET.fromstring(ET.tostring(asc))))
        st=ET.SubElement(clip,'sourcetrack');field(st,'mediatype','audio');field(st,'trackindex',channel)
    ET.indent(root)
    (output/'Phyxel_Resolve_timeline.xml').write_bytes(b'<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE xmeml>\n'+ET.tostring(root,encoding='utf-8'))


def soundtrack(output):
    """A restrained original electronic bed and synthetic editorial effects."""
    sr=48000
    t=np.arange(round(DURATION*sr))/sr
    left=np.zeros_like(t)
    right=np.zeros_like(t)
    rng=np.random.default_rng(20261007)

    def add(at, signal, pan=0):
        start=int(at*sr)
        n=min(len(signal),len(t)-start)
        if n<=0:return
        left[start:start+n]+=signal[:n]*math.sqrt((1-pan)/2)
        right[start:start+n]+=signal[:n]*math.sqrt((1+pan)/2)

    # 100 BPM; quiet enough to make narration the foreground.
    beat=.6
    for n,at in enumerate(np.arange(0,DURATION,beat)):
        if 17.6<at<19.6:continue
        u=np.arange(int(.36*sr))/sr
        phase=2*np.pi*(46*u+18*(1-np.exp(-20*u)))
        add(at,.12*np.sin(phase)*np.exp(-13*u))
        if n%2:
            u=np.arange(int(.11*sr))/sr
            noise=rng.normal(0,1,len(u))
            add(at,.026*noise*np.exp(-40*u),.25)
        u=np.arange(int(.065*sr))/sr
        add(at+.3,.018*rng.normal(0,1,len(u))*np.exp(-65*u),-.3)
    notes=[146.83,174.61,220.0,196.0]
    for n,at in enumerate(np.arange(0,23.4,1.2)):
        u=np.arange(int(.95*sr))/sr
        envelope=(1-np.exp(-35*u))*np.exp(-4*u)
        signal=.028*(np.sin(2*np.pi*notes[n%4]*u)+.3*np.sin(4*np.pi*notes[n%4]*u))*envelope
        add(at,signal,(-1 if n%2 else 1)*.4)
    # Soft transitions, then one larger final impact in sync with expansion.
    for at in [3.6,6.6,9.6,12.6,14.6,18.0,23.6]:
        u=np.arange(int(.22*sr))/sr
        add(max(0,at-.12),.018*rng.normal(0,1,len(u))*np.sin(np.pi*u/.22)**2,.1)
    for at,strength in [(15.55,.22),(18.9,.4)]:
        u=np.arange(int(1.65*sr))/sr
        noise=rng.normal(0,1,len(u))
        kernel=np.ones(15)/15
        noise=np.convolve(noise,kernel,'same')
        signal=strength*(.65*np.sin(2*np.pi*(43*u+4*(1-np.exp(-12*u))))*np.exp(-4*u)+noise*np.exp(-3*u))
        add(at,signal)
    fade=np.clip(t/.16,0,1)*np.clip((DURATION-t)/.6,0,1)
    stereo=np.stack([left*fade,right*fade],axis=1)
    with wave.open(str(output),'wb') as wav:
        wav.setnchannels(2);wav.setsampwidth(2);wav.setframerate(sr)
        wav.writeframes((np.clip(stereo,-.95,.95)*32767).astype('<i2').tobytes())


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--ffmpeg',default=shutil.which('ffmpeg') or 'ffmpeg')
    p.add_argument('--silent-voice',action='store_true',help='Skip narration; retain the original synthetic music/effects.')
    args=p.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    work=args.output/'work';work.mkdir(exist_ok=True)
    ff=args.ffmpeg
    for shot in SHOTS:
        if not (args.source/shot['file']).is_file():
            raise FileNotFoundError(args.source/shot['file'])
    if not args.silent_voice:
        # Windows PowerShell reads UTF-8 correctly through explicit decoding.
        script=Path(__file__).with_name('narration.ps1')
        ps=f"& ([ScriptBlock]::Create([IO.File]::ReadAllText('{str(script).replace(chr(39),chr(39)*2)}',[Text.Encoding]::UTF8))) -OutputDirectory '{str(work).replace(chr(39),chr(39)*2)}'"
        run(['powershell','-NoProfile','-Command',ps])

    elapsed=0.0
    manifest=[]
    for i,shot in enumerate(SHOTS):
        print(f'Rendering shot {i+1}/{len(SHOTS)}',flush=True)
        graphics=work/f'plate_{i:02d}.png'
        plate(shot,i,graphics)
        source=args.source/shot['file']
        duration=shot['duration']
        inputs=['-loop','1','-framerate',str(FPS),'-i',graphics]
        is_still=source.suffix.lower() in ['.jpg','.png','.jpeg']
        if shot.get('interface'):
            prepared=work/f'interface_{i:02d}.png';interface_still(source,prepared)
            inputs+=['-loop','1','-framerate',str(FPS),'-i',prepared]
            hero='[1:v]setsar=1[hero]'
        elif is_still:
            prepared=work/f'still_{i:02d}.png';prepare_still(source,shot,prepared)
            inputs+=['-loop','1','-framerate',str(FPS),'-i',prepared]
            # Ken Burns movement applies only to stills, not simulated gameplay.
            still_h=2*round(984*shot['crop'][3]/shot['crop'][2]/2)
            hero=f"[1:v]scale=1600:-2,zoompan=z='1.0+0.00045*on':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=984x{still_h}:fps={FPS},pad=984:944:0:(oh-ih)/2:color=0x070a0f,setsar=1[hero]"
        else:
            inputs+=['-ss',str(shot['start']),'-t',str(duration*shot['speed']+.2),'-i',source]
            x,y,cw,ch=shot['crop']
            hero=f'[1:v]crop={cw}:{ch}:{x}:{y},setpts=(PTS-STARTPTS)/{shot["speed"]},fps={FPS},scale=984:944:force_original_aspect_ratio=decrease,pad=984:944:(ow-iw)/2:(oh-ih)/2:color=0x070a0f,setsar=1[hero]'
        graph=hero+';[0:v][hero]overlay=48:568:shortest=1,format=yuv420p[v]'
        target=work/f'shot_{i:02d}.mp4'
        run([ff,'-hide_banner','-loglevel','error','-y',*inputs,'-filter_complex',graph,'-map','[v]',
             '-t',str(duration),'-an','-c:v','libx264','-preset','fast','-crf','18','-r',str(FPS),'-threads','4',target])
        manifest.append(dict(index=i,at=round(elapsed,3),**shot))
        elapsed+=duration
    concat=work/'concat.txt'
    concat.write_text('\n'.join(f"file 'shot_{i:02d}.mp4'" for i in range(len(SHOTS)))+'\n',encoding='utf-8')
    clean=work/'picture.mp4'
    run([ff,'-hide_banner','-loglevel','error','-y','-f','concat','-safe','0','-i',concat,'-c','copy',clean])
    bed=work/'original_soundtrack.wav';soundtrack(bed)
    sound=work/'mix.wav'
    if not args.silent_voice:
        inputs=['-i',bed]
        filters=[]
        for i,(at,window) in enumerate(zip(VOICE_STARTS,VOICE_WINDOWS)):
            voice=work/f'voice_{i:02d}.wav'
            with wave.open(str(voice),'rb') as wav:
                length=wav.getnframes()/wav.getframerate()
            speed=max(1,length/window)
            inputs+=['-i',voice]
            filters.append(f'[{i+1}:a]atempo={speed:.6f},aresample=48000,highpass=f=90,lowpass=f=10500,volume=1.45,adelay={round(at*1000)}:all=1[a{i}]')
        filters.append('[0:a]' + ''.join(f'[a{i}]' for i in range(6))+
                       'amix=inputs=7:duration=first:normalize=0,alimiter=limit=0.9,loudnorm=I=-16:TP=-1.5:LRA=9[a]')
        run([ff,'-hide_banner','-loglevel','error','-y',*inputs,'-filter_complex',';'.join(filters),'-map','[a]','-t',str(DURATION),'-ar','48000','-ac','2',sound])
    else:
        shutil.copyfile(bed,sound)
    final=args.output/'Phyxel_Shorts_RU_v1.mp4'
    run([ff,'-hide_banner','-loglevel','error','-y','-i',clean,'-i',sound,'-map','0:v','-map','1:a','-c:v','copy','-c:a','aac','-b:a','192k','-ar','48000','-t',str(DURATION),'-movflags','+faststart',final])
    alternate=args.output/'Phyxel_Shorts_no_voice_v1.mp4'
    run([ff,'-hide_banner','-loglevel','error','-y','-i',clean,'-i',bed,'-map','0:v','-map','1:a','-c:v','copy','-c:a','aac','-b:a','192k','-t',str(DURATION),'-movflags','+faststart',alternate])
    (args.output/'edit_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    export_resolve_xml(work,args.output,manifest)
    # Editor-neutral timeline for conforming the raw selection in Resolve.
    # A complete reference render is also importable in Resolve and CapCut.
    edl=['TITLE: PHYXEL SHORTS RU V1','FCM: NON-DROP FRAME','']
    def tc(seconds):
        frames=round(seconds*FPS)
        return f'{frames//108000:02d}:{frames//1800%60:02d}:{frames//30%60:02d}:{frames%30:02d}'
    for i,s in enumerate(manifest):
        edl.extend([f'{i+1:03d}  AX       V     C        {tc(s["start"])} {tc(s["start"]+s["duration"]*s["speed"])} {tc(s["at"])} {tc(s["at"]+s["duration"])}',f'* FROM CLIP NAME: {s["file"]}'])
        if s['speed']!=1:
            edl.append(f'M2   AX       {FPS*s["speed"]:.3f}                {tc(s["start"])}')
    (args.output/'Phyxel_selection_30fps.edl').write_text('\n'.join(edl)+'\n',encoding='utf-8')
    print(f'Finished: {final}',flush=True)


if __name__=='__main__':
    main()
