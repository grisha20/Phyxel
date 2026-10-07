"""Local Russian neural narration. Model/cache/output stay outside the repo.

Install chatterbox-tts==0.1.7 in an isolated environment with CUDA PyTorch.
Use the model's built-in voice, or an explicitly supplied licensed stock
reference. The owner's inspiration videos are never used as voice references.
"""
from pathlib import Path
import argparse
import os
import json
import random

LINES = [
    'Я начал с воды. А дошёл до этого!',
    'Делаю свою физическую песочницу. Тут можно рисовать материалы и сразу проверять, что получится.',
    'Добавил огонь, нагрев и движение газов.',
    'Теперь можно собрать целую печку!',
    'А если захочется чего-то погромче...',
    'Игра ещё в разработке. Что испытать следующим?',
]


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--cache',type=Path,required=True)
    parser.add_argument('--reference',type=Path)
    parser.add_argument('--local-model',type=Path)
    parser.add_argument('--demo',action='store_true')
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    args.cache.mkdir(parents=True,exist_ok=True)
    os.environ['HF_HOME']=str(args.cache)
    os.environ['HF_HUB_DISABLE_SYMLINKS_WARNING']='1'
    import numpy as np
    import torch
    import soundfile as sf
    from chatterbox.mtl_tts import ChatterboxMultilingualTTS
    torch.set_num_threads(6)
    print('GPU:',torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU',flush=True)
    device='cuda' if torch.cuda.is_available() else 'cpu'
    model=(ChatterboxMultilingualTTS.from_local(args.local_model,device=device) if args.local_model
           else ChatterboxMultilingualTTS.from_pretrained(device=device))
    records=[]
    for i,text in enumerate(LINES[:1] if args.demo else LINES):
        path=args.output/f'neural_{i:02d}.wav'
        if path.exists() and not args.demo:
            audio,sr=sf.read(path)
            print(f'Keeping existing clip {i}',flush=True)
        else:
            random.seed(1700+i);np.random.seed(1700+i);torch.manual_seed(1700+i)
            torch.cuda.manual_seed_all(1700+i)
            print(f'Synthesizing {i+1}: {text}',flush=True)
            kwargs=dict(language_id='ru',exaggeration=.65,cfg_weight=.3,temperature=.75)
            if args.reference:
                kwargs['audio_prompt_path']=str(args.reference)
            wav=model.generate(text,**kwargs)
            audio=wav.squeeze().detach().cpu().numpy()
            sr=model.sr
            sf.write(path,audio,sr,subtype='PCM_16')
        records.append(dict(index=i,text=text,file=path.name,duration=len(audio)/sr))
    (args.output/'narration.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
    print('Neural narration ready.',flush=True)


if __name__=='__main__':
    main()
