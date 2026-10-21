#!/usr/bin/env python3
"""生成日文键盘的假名语音文件（public/voice/u<codepoint>.mp3）。

语音源：Google Translate TTS（神经网络语音，音质优于系统自带 TTS）。
字符集合从 src/pages/Japanese.tsx 的 JIS_MAP / DAKUTEN_MAP / HANDAKUTEN_MAP 中提取，
保证与键盘可产出的字符始终一致。

用法：python3 scripts/generate-voice.py
依赖：无（仅标准库）
"""
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / 'src' / 'pages' / 'Japanese.tsx'
OUT = ROOT / 'public' / 'voice'

URL = 'https://translate.google.com/translate_tts?ie=UTF-8&q={q}&tl=ja&client=tw-ob'
HEADERS = {'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)'}

# 小写假名（拗音/促音）单独朗读无实际发音，用同音的全角假名合成
SMALL_TO_FULL = {
    'ぁ': 'あ', 'ぃ': 'い', 'ぅ': 'う', 'ぇ': 'え', 'ぉ': 'お',
    'ゃ': 'や', 'ゅ': 'ゆ', 'ょ': 'よ', 'っ': 'つ',
    'ァ': 'ア', 'ィ': 'イ', 'ゥ': 'ウ', 'ェ': 'エ', 'ォ': 'オ',
    'ャ': 'ヤ', 'ュ': 'ユ', 'ョ': 'ヨ', 'ッ': 'ツ',
}
# 长音符号单独朗读无意义，读其名称
SPECIAL_TEXT = {'ー': 'ちょうおん'}


def collect_kana() -> set:
    src = SRC.read_text(encoding='utf-8')
    chars: set = set()
    # JIS_MAP: ['ぬ','ヌ','ぬ','ヌ']
    for group in re.findall(r"\['([^']*)','([^']*)','([^']*)','([^']*)'\]", src):
        chars.update(group)
    # DAKUTEN_MAP / HANDAKUTEN_MAP: 'か':'が'
    for name in ('DAKUTEN_MAP', 'HANDAKUTEN_MAP'):
        block = re.search(rf'const {name}[^{{]*{{(.*?)}};', src, re.S)
        if not block:
            sys.exit(f'未找到 {name}')
        for k, v in re.findall(r"'([^']+)':'([^']+)'", block.group(1)):
            chars.update((k, v))
    # 浊点/半浊点符号本身不发声（仅合字时播放），排除
    chars -= {'゛', '゜'}
    return chars


def tts_text(ch: str) -> str:
    if ch in SPECIAL_TEXT:
        return SPECIAL_TEXT[ch]
    return SMALL_TO_FULL.get(ch, ch)


def filename(ch: str) -> str:
    return f'u{ord(ch):04x}.mp3'


def fetch(ch: str) -> bytes:
    url = URL.format(q=urllib.parse.quote(tts_text(ch)))
    for attempt in range(4):
        req = urllib.request.Request(url, headers=HEADERS)
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = resp.read()
            # MPEG 帧同步头校验
            if len(data) > 1000 and data[0] == 0xFF and (data[1] & 0xE0) == 0xE0:
                return data
            raise ValueError(f'非法音频: {len(data)}B')
        except Exception as e:  # noqa: BLE001 - 网络重试
            if attempt == 3:
                raise
            time.sleep(1.5 * (attempt + 1))
            print(f'  重试 {ch}: {e}')
    raise RuntimeError('unreachable')


def main():
    chars = sorted(collect_kana())
    OUT.mkdir(parents=True, exist_ok=True)
    print(f'共 {len(chars)} 个字符 → {OUT}')
    done = skipped = 0
    for i, ch in enumerate(chars):
        out = OUT / filename(ch)
        if out.exists() and out.stat().st_size > 1000:
            skipped += 1
            continue
        data = fetch(ch)
        out.write_bytes(data)
        done += 1
        print(f'  [{i + 1}/{len(chars)}] {ch} {filename(ch)} {len(data)}B')
        time.sleep(0.3)  # 限流保护
    print(f'完成：新生成 {done}，跳过 {skipped}')


main()
