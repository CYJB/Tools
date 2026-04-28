import React, { useState, useRef, useCallback, useEffect } from 'react';
import './Japanese.css';

const IS_MAC = /Mac|iPhone|iPad/.test(navigator.platform);
const KATA_SHORTCUT = IS_MAC ? '⌘K' : 'Ctrl+K';

// JIS 配列: [平假名, 片假名, Shift平假名, Shift片假名]
const JIS_MAP: Record<string, [string, string, string, string]> = {
  Digit1:       ['ぬ','ヌ','ぬ','ヌ'],
  Digit2:       ['ふ','フ','ふ','フ'],
  Digit3:       ['あ','ア','ぁ','ァ'],
  Digit4:       ['う','ウ','ぅ','ゥ'],
  Digit5:       ['え','エ','ぇ','ェ'],
  Digit6:       ['お','オ','ぉ','ォ'],
  Digit7:       ['や','ヤ','ゃ','ャ'],
  Digit8:       ['ゆ','ユ','ゅ','ュ'],
  Digit9:       ['よ','ヨ','ょ','ョ'],
  Digit0:       ['わ','ワ','を','ヲ'],
  Minus:        ['ほ','ホ','ほ','ホ'],
  Equal:        ['へ','ヘ','へ','ヘ'],
  KeyQ:         ['た','タ','た','タ'],
  KeyW:         ['て','テ','て','テ'],
  KeyE:         ['い','イ','ぃ','ィ'],
  KeyR:         ['す','ス','す','ス'],
  KeyT:         ['か','カ','か','カ'],
  KeyY:         ['ん','ン','ん','ン'],
  KeyU:         ['な','ナ','な','ナ'],
  KeyI:         ['に','ニ','に','ニ'],
  KeyO:         ['ら','ラ','ら','ラ'],
  KeyP:         ['せ','セ','せ','セ'],
  BracketLeft:  ['゛','゛','゛','゛'],
  BracketRight: ['゜','゜','む','ム'],
  KeyA:         ['ち','チ','ち','チ'],
  KeyS:         ['と','ト','と','ト'],
  KeyD:         ['し','シ','し','シ'],
  KeyF:         ['は','ハ','は','ハ'],
  KeyG:         ['き','キ','き','キ'],
  KeyH:         ['く','ク','く','ク'],
  KeyJ:         ['ま','マ','ま','マ'],
  KeyK:         ['の','ノ','の','ノ'],
  KeyL:         ['り','リ','り','リ'],
  Semicolon:    ['れ','レ','れ','レ'],
  Quote:        ['け','ケ','け','ケ'],
  KeyZ:         ['つ','ツ','っ','ッ'],
  KeyX:         ['さ','サ','さ','サ'],
  KeyC:         ['そ','ソ','そ','ソ'],
  KeyV:         ['ひ','ヒ','ひ','ヒ'],
  KeyB:         ['こ','コ','こ','コ'],
  KeyN:         ['み','ミ','み','ミ'],
  KeyM:         ['も','モ','も','モ'],
  Comma:        ['ね','ネ','ね','ネ'],
  Period:       ['る','ル','る','ル'],
  Slash:        ['め','メ','め','メ'],
  IntlRo:       ['ろ','ロ','ろ','ロ'],
};

const DAKUTEN_MAP: Record<string, string> = {
  'か':'が','き':'ぎ','く':'ぐ','け':'げ','こ':'ご',
  'さ':'ざ','し':'じ','す':'ず','せ':'ぜ','そ':'ぞ',
  'た':'だ','ち':'ぢ','つ':'づ','て':'で','と':'ど',
  'は':'ば','ひ':'び','ふ':'ぶ','へ':'べ','ほ':'ぼ',
  'カ':'ガ','キ':'ギ','ク':'グ','ケ':'ゲ','コ':'ゴ',
  'サ':'ザ','シ':'ジ','ス':'ズ','セ':'ゼ','ソ':'ゾ',
  'タ':'ダ','チ':'ヂ','ツ':'ヅ','テ':'デ','ト':'ド',
  'ハ':'バ','ヒ':'ビ','フ':'ブ','ヘ':'ベ','ホ':'ボ',
  'う':'ゔ','ウ':'ヴ',
};
const HANDAKUTEN_MAP: Record<string, string> = {
  'は':'ぱ','ひ':'ぴ','ふ':'ぷ','へ':'ぺ','ほ':'ぽ',
  'ハ':'パ','ヒ':'ピ','フ':'プ','ヘ':'ペ','ホ':'ポ',
};

const CODE_LABEL: Record<string, string> = {
  Digit1:'1', Digit2:'2', Digit3:'3', Digit4:'4', Digit5:'5',
  Digit6:'6', Digit7:'7', Digit8:'8', Digit9:'9', Digit0:'0',
  Minus:'-', Equal:'=',
  KeyQ:'Q', KeyW:'W', KeyE:'E', KeyR:'R', KeyT:'T',
  KeyY:'Y', KeyU:'U', KeyI:'I', KeyO:'O', KeyP:'P',
  BracketLeft:'[', BracketRight:']',
  KeyA:'A', KeyS:'S', KeyD:'D', KeyF:'F', KeyG:'G',
  KeyH:'H', KeyJ:'J', KeyK:'K', KeyL:'L',
  Semicolon:';', Quote:"'",
  KeyZ:'Z', KeyX:'X', KeyC:'C', KeyV:'V', KeyB:'B',
  KeyN:'N', KeyM:'M', Comma:',', Period:'.', Slash:'/',
  IntlRo:'\\',
};

const ROWS: string[][] = [
  ['Digit1','Digit2','Digit3','Digit4','Digit5','Digit6','Digit7','Digit8','Digit9','Digit0','Minus','Equal'],
  ['KeyQ','KeyW','KeyE','KeyR','KeyT','KeyY','KeyU','KeyI','KeyO','KeyP','BracketLeft','BracketRight'],
  ['KeyA','KeyS','KeyD','KeyF','KeyG','KeyH','KeyJ','KeyK','KeyL','Semicolon','Quote'],
  ['KeyZ','KeyX','KeyC','KeyV','KeyB','KeyN','KeyM','Comma','Period','Slash'],
];

function applyDakuten(text: string): string {
  if (!text) return text;
  const last = text[text.length - 1];
  const combined = DAKUTEN_MAP[last];
  return combined ? text.slice(0, -1) + combined : text + '゛';
}
function applyHandakuten(text: string): string {
  if (!text) return text;
  const last = text[text.length - 1];
  const combined = HANDAKUTEN_MAP[last];
  return combined ? text.slice(0, -1) + combined : text + '゜';
}

export default function Japanese() {
  const [text, setText] = useState('');
  const [kata, setKata] = useState(false);
  const [shift, setShiftDisplay] = useState(false);
  const [shiftLocked, setShiftLockedDisplay] = useState(false);
  const shiftRef = useRef(false);
  const shiftLockedRef = useRef(false);
  const shiftInteractingRef = useRef(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const setShift = useCallback((val: boolean) => {
    shiftRef.current = val;
    setShiftDisplay(val);
  }, []);

  const setShiftLocked = useCallback((val: boolean) => {
    shiftLockedRef.current = val;
    setShiftLockedDisplay(val);
  }, []);

  const insertAtCursor = useCallback((insert: string) => {
    const el = textareaRef.current;
    if (!el) { setText(t => t + insert); return; }
    const start = el.selectionStart;
    const end = el.selectionEnd;
    setText(prev => prev.slice(0, start) + insert + prev.slice(end));
    requestAnimationFrame(() => {
      el.selectionStart = el.selectionEnd = start + insert.length;
      el.focus();
    });
  }, []);

  const handleKana = useCallback((code: string) => {
    const entry = JIS_MAP[code];
    if (!entry) return;
    const sh = shiftRef.current;
    const idx = kata ? (sh ? 3 : 1) : (sh ? 2 : 0);
    const char = entry[idx];
    if (char === '゛') { setText(t => applyDakuten(t)); return; }
    if (char === '゜') { setText(t => applyHandakuten(t)); return; }
    insertAtCursor(char);
  }, [kata, insertAtCursor]);

  // 物理键盘：不拦截 Ctrl/Meta 快捷键（Ctrl+K / Cmd+K 切换假名模式）
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyK') {
        e.preventDefault();
        setKata(v => !v);
        return;
      }
      if (e.ctrlKey || e.metaKey) return;
      if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') {
        // 锁定状态下按下物理 Shift：临时关闭 shift
        setShift(shiftLockedRef.current ? false : true);
        return;
      }
      if (JIS_MAP[e.code]) {
        e.preventDefault();
        handleKana(e.code);
      }
    };
    const onKeyUp = (e: KeyboardEvent) => {
      if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') {
        // 锁定状态下松开物理 Shift：恢复 shift；未锁定则关闭
        setShift(shiftLockedRef.current ? true : false);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, [handleKana, setShift]);

  // 全局 pointerup：处理虚拟 Shift 的释放与锁定逻辑
  useEffect(() => {
    const up = (e: PointerEvent) => {
      if (!shiftInteractingRef.current) {
        if (!shiftLockedRef.current) setShift(false);
        return;
      }
      shiftInteractingRef.current = false;
      const overShift = (e.target as Element)?.closest?.('.jp-shift');
      if (shiftLockedRef.current) {
        if (overShift) {
          setShiftLocked(false);
          setShift(false);
        } else {
          setShift(true);
        }
      } else {
        if (overShift) {
          setShiftLocked(true);
          setShift(true);
        } else {
          setShift(false);
        }
      }
    };
    window.addEventListener('pointerup', up);
    return () => window.removeEventListener('pointerup', up);
  }, [setShift, setShiftLocked]);

  const handleBackspace = useCallback(() => {
    const el = textareaRef.current;
    if (!el) { setText(t => t.slice(0, -1)); return; }
    const start = el.selectionStart;
    const end = el.selectionEnd;
    if (start !== end) {
      setText(prev => prev.slice(0, start) + prev.slice(end));
      requestAnimationFrame(() => { el.selectionStart = el.selectionEnd = start; el.focus(); });
    } else if (start > 0) {
      setText(prev => prev.slice(0, start - 1) + prev.slice(start));
      requestAnimationFrame(() => { el.selectionStart = el.selectionEnd = start - 1; el.focus(); });
    }
  }, []);

  const onKanaDown = useCallback((e: React.PointerEvent, code: string) => {
    e.preventDefault();
    handleKana(code);
    textareaRef.current?.focus();
  }, [handleKana]);

  const onFnDown = useCallback((e: React.PointerEvent, fn: () => void) => {
    e.preventDefault();
    fn();
    textareaRef.current?.focus();
  }, []);

  const KanaKey = useCallback(({ code }: { code: string }) => {
    const entry = JIS_MAP[code];
    if (!entry) return null;
    const label = kata ? entry[1] : entry[0];
    const shiftKana = kata ? entry[3] : entry[2];
    const showHint = shiftKana && shiftKana !== label && shiftKana !== '゛' && shiftKana !== '゜';
    const keyLabel = CODE_LABEL[code] ?? '';
    return (
      <button
        className={`jp-key${shift && showHint ? ' shifted' : ''}`}
        onPointerDown={(e) => onKanaDown(e, code)}
        tabIndex={-1}
      >
        {showHint && (
          <span className="jp-key-top">{shift ? label : shiftKana}</span>
        )}
        <span className="jp-key-main">
          {shift && showHint ? shiftKana : label}
        </span>
        <span className="jp-key-en">{keyLabel}</span>
      </button>
    );
  }, [kata, shift, onKanaDown]);

  const ShiftBtn = ({ cls }: { cls: string }) => (
    <button
      className={`jp-key jp-shift ${cls} ${shift ? 'active' : ''} ${shiftLocked ? 'locked' : ''}`}
      onPointerDown={(e) => {
        e.preventDefault();
        shiftInteractingRef.current = true;
        setShift(shiftLockedRef.current ? false : true);
      }}
      tabIndex={-1}
    >
      ⇧<span className="jp-key-en">Shift</span>
    </button>
  );

  return (
    <div className="jp-page">
      <header className="jp-header">
        <h1>日文输入</h1>
        <p className="jp-subtitle">JIS 假名键盘</p>
      </header>

      <textarea
        ref={textareaRef}
        className="jp-textarea"
        value={text}
        onChange={e => setText(e.target.value)}
        placeholder="在此输入日文，或点击下方键盘…"
        rows={5}
        spellCheck={false}
      />

      <div className="jp-controls">
        <button
          className={`jp-toggle ${kata ? 'active' : ''}`}
          onClick={() => setKata(v => !v)}
        >
          {kata ? '片假名' : '平假名'}
          <span className="jp-shortcut">{KATA_SHORTCUT}</span>
        </button>
        <button className="jp-toggle" onClick={() => { setText(''); textareaRef.current?.focus(); }}>
          清空
        </button>
      </div>

      <div className="jp-keyboard">
        {/* 数字行 + Backspace */}
        <div className="jp-row">
          {ROWS[0].map(code => <KanaKey key={code} code={code} />)}
          <button
            className="jp-key jp-backspace"
            onPointerDown={(e) => onFnDown(e, handleBackspace)}
            tabIndex={-1}
          >
            ⌫<span className="jp-key-en">BS</span>
          </button>
        </div>

        {/* QWERTY 行 */}
        <div className="jp-row">
          {ROWS[1].map(code => <KanaKey key={code} code={code} />)}
        </div>

        {/* ASDF 行（小缩进）+ Enter */}
        <div className="jp-row jp-row-indent">
          {ROWS[2].map(code => <KanaKey key={code} code={code} />)}
          <button
            className="jp-key jp-enter"
            onPointerDown={(e) => onFnDown(e, () => insertAtCursor('\n'))}
            tabIndex={-1}
          >
            ↵<span className="jp-key-en">Enter</span>
          </button>
        </div>

        {/* ZXCV 行：左 Shift + 假名 + 右 Shift */}
        <div className="jp-row">
          <ShiftBtn cls="jp-shift-left" />
          {ROWS[3].map(code => <KanaKey key={code} code={code} />)}
          <ShiftBtn cls="jp-shift-right" />
        </div>

        {/* 空格行 */}
        <div className="jp-row">
          <button
            className="jp-key jp-space"
            onPointerDown={(e) => onFnDown(e, () => insertAtCursor('　'))}
            tabIndex={-1}
          >
            スペース
          </button>
        </div>
      </div>
    </div>
  );
}
