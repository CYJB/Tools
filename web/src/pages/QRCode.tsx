import React, { useState, useRef, useCallback, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import QRCodeLib from 'qrcode';
import jsQR from 'jsqr';
import './QRCode.css';

type Tab = 'generate' | 'scan';

export default function QRCodePage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tab: Tab = searchParams.get('tab') === 'scan' ? 'scan' : 'generate';
  const setTab = useCallback((t: Tab) => {
    setSearchParams({ tab: t }, { replace: true });
  }, [setSearchParams]);

  return (
    <div className="qr-page">
      <header className="qr-header">
        <h1>二维码</h1>
        <p className="qr-subtitle">生成与识别二维码</p>
      </header>

      <div className="qr-tabs">
        <button
          className={`qr-tab${tab === 'generate' ? ' active' : ''}`}
          onClick={() => setTab('generate')}
        >生成</button>
        <button
          className={`qr-tab${tab === 'scan' ? ' active' : ''}`}
          onClick={() => setTab('scan')}
        >识别</button>
      </div>

      {tab === 'generate' ? <GeneratePanel /> : <ScanPanel />}
    </div>
  );
}

function GeneratePanel() {
  const [text, setText] = useState('');
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    if (!text.trim()) {
      const ctx = canvas.getContext('2d');
      canvas.width = 200;
      canvas.height = 200;
      if (ctx) {
        ctx.fillStyle = '#f5f7fa';
        ctx.fillRect(0, 0, 200, 200);
        ctx.fillStyle = '#ccc';
        ctx.font = '14px sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('输入内容生成二维码', 100, 105);
      }
      setError('');
      return;
    }
    QRCodeLib.toCanvas(canvas, text, {
      width: 280,
      margin: 2,
      errorCorrectionLevel: 'M',
      color: { dark: '#1a1a1a', light: '#ffffff' },
    }, (err) => {
      if (err) setError(err.message);
      else setError('');
    });
  }, [text]);

  const handleDownload = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas || !text.trim()) return;
    const url = canvas.toDataURL('image/png');
    const a = document.createElement('a');
    a.href = url;
    a.download = 'qrcode.png';
    a.click();
  }, [text]);

  return (
    <div className="qr-panel">
      <textarea
        className="qr-textarea"
        value={text}
        onChange={e => setText(e.target.value)}
        placeholder="输入任意文本生成二维码…"
        rows={4}
        spellCheck={false}
      />
      {error && <div className="qr-error">{error}</div>}
      <div className="qr-canvas-wrap">
        <canvas ref={canvasRef} className="qr-canvas" />
      </div>
      <button
        className="qr-btn"
        disabled={!text.trim() || !!error}
        onClick={handleDownload}
      >下载图片</button>
    </div>
  );
}

function ScanPanel() {
  const [result, setResult] = useState('');
  const [error, setError] = useState('');
  const [dragging, setDragging] = useState(false);
  const [preview, setPreview] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  const decodeImage = useCallback((file: File) => {
    setError('');
    setResult('');
    const url = URL.createObjectURL(file);
    setPreview(url);
    const img = new Image();
    img.onload = () => {
      const canvas = document.createElement('canvas');
      const MAX = 1500;
      let w = img.width, h = img.height;
      if (w > MAX || h > MAX) {
        const scale = MAX / Math.max(w, h);
        w = Math.round(w * scale);
        h = Math.round(h * scale);
      }
      canvas.width = w;
      canvas.height = h;
      const ctx = canvas.getContext('2d')!;
      ctx.drawImage(img, 0, 0, w, h);
      const data = ctx.getImageData(0, 0, w, h);
      const code = jsQR(data.data, w, h);
      if (code) {
        setResult(code.data);
      } else {
        setError('未识别到二维码');
      }
    };
    img.onerror = () => setError('无法加载图片');
    img.src = url;
  }, []);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragging(false);
    const file = e.dataTransfer.files[0];
    if (file && file.type.startsWith('image/')) decodeImage(file);
    else setError('请拖入图片文件');
  }, [decodeImage]);

  const handlePaste = useCallback((e: React.ClipboardEvent) => {
    const items = e.clipboardData.items;
    for (const item of items) {
      if (item.type.startsWith('image/')) {
        const file = item.getAsFile();
        if (file) decodeImage(file);
        return;
      }
    }
  }, [decodeImage]);

  const handleFileSelect = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) decodeImage(file);
    e.target.value = '';
  }, [decodeImage]);

  useEffect(() => {
    return () => { if (preview) URL.revokeObjectURL(preview); };
  }, [preview]);

  const handleCopy = useCallback(() => {
    if (result) navigator.clipboard.writeText(result);
  }, [result]);

  const handleClear = useCallback(() => {
    setResult('');
    setError('');
    setPreview('');
  }, []);

  return (
    <div className="qr-panel" onPaste={handlePaste} tabIndex={-1}>
      <div
        className={`qr-dropzone${dragging ? ' dragging' : ''}${preview ? ' has-image' : ''}`}
        onDragOver={e => { e.preventDefault(); setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        onClick={() => fileRef.current?.click()}
      >
        {preview ? (
          <img src={preview} alt="预览" className="qr-preview-img" />
        ) : (
          <div className="qr-dropzone-text">
            <img src="qrcode-scan-icon.png" alt="" className="qr-dropzone-icon" />
            <span>拖入图片、粘贴截图或点击选择</span>
          </div>
        )}
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          onChange={handleFileSelect}
          style={{ display: 'none' }}
        />
      </div>

      {error && <div className="qr-error">{error}</div>}

      {result && (
        <div className="qr-result">
          <div className="qr-result-label">识别结果</div>
          <div className="qr-result-text">{result}</div>
          <div className="qr-result-actions">
            <button className="qr-btn qr-btn-small" onClick={handleCopy}>复制</button>
            {/^https?:\/\//i.test(result) && (
              <a className="qr-btn qr-btn-small qr-btn-link" href={result} target="_blank" rel="noopener noreferrer">打开链接</a>
            )}
            <button className="qr-btn qr-btn-small" onClick={handleClear}>清除</button>
          </div>
        </div>
      )}
    </div>
  );
}
