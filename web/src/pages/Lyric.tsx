import React, { useState, useRef, useCallback, useEffect } from 'react';
import { Button, Tag, Modal, Textarea } from 'shineout';
import { parseLRC, mergeLRCs, serializeLRC, formatTimestamp, MergedLine, ParsedLrc } from '../utils/lrcParser';
import './Lyric.css';

interface DropZoneProps {
  content: string;
  onContent: (v: string) => void;
  index: number;
}

function DropZone({ content, onContent, index }: DropZoneProps) {
  const [dragOver, setDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files[0];
    if (file) {
      const reader = new FileReader();
      reader.onload = (ev) => onContent(ev.target?.result as string);
      reader.readAsText(file, 'utf-8');
    }
  }, [onContent]);

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (ev) => onContent(ev.target?.result as string);
    reader.readAsText(file, 'utf-8');
  };

  return (
    <div
      className={`drop-zone${dragOver ? ' drag-over' : ''}`}
      onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
      onDragLeave={() => setDragOver(false)}
      onDrop={handleDrop}
    >
      <div className="drop-zone-header">
        <span className="drop-zone-label">
          {index === 0 ? '基础歌词 (Base) — 时间轴以此为准' : '合并歌词 (Secondary) — 提取翻译等信息'}
        </span>
        <Button size="small" onClick={() => fileInputRef.current?.click()}>
          选择文件
        </Button>
        <input
          ref={fileInputRef}
          type="file"
          accept=".lrc,.txt"
          style={{ display: 'none' }}
          onChange={handleFileChange}
        />
      </div>
      <Textarea
        value={content}
        onChange={onContent}
        placeholder={index === 0
          ? '拖入 .lrc 文件或粘贴基础歌词'
          : '拖入 .lrc 文件或粘贴要合并的歌词（可选）'}
        rows={12}
        style={{ fontFamily: 'monospace', fontSize: 13 }}
      />
      <div className="drop-zone-info">
        {content ? `${content.split('\n').filter(Boolean).length} 行` : '未输入'}
      </div>
    </div>
  );
}

interface StatusBadgeProps {
  status: MergedLine['matchStatus'];
  sim: number;
}

function StatusBadge({ status, sim }: StatusBadgeProps) {
  if (status === 'matched') {
    return <Tag color="success" size="small">匹配 {Math.round(sim * 100)}%</Tag>;
  }
  if (status === 'unmatched') {
    return <Tag color="warning" size="small">未匹配</Tag>;
  }
  if (status === 'noSourceIncluded') {
    return <Tag color="success" size="small">仅副源·已纳入</Tag>;
  }
  return <Tag color="danger" size="small">仅副源</Tag>;
}

interface TranslationCellProps {
  tags: Record<string, string>;
  onChange: (tags: Record<string, string>) => void;
}

function TranslationCell({ tags, onChange }: TranslationCellProps) {
  const trEntries = Object.entries(tags).filter(([k]) => k.startsWith('tr:'));
  if (trEntries.length === 0) return <span className="no-tr">—</span>;
  return (
    <div className="tr-cell">
      {trEntries.map(([lang, text]) => (
        <div key={lang} className="tr-row">
          <span className="lang-tag">{lang.replace('tr:', '')}</span>
          <input
            className="tr-input"
            value={text}
            onChange={(e) => {
              onChange({ ...tags, [lang]: e.target.value });
            }}
          />
        </div>
      ))}
    </div>
  );
}

interface ManualMatchModalProps {
  open: boolean;
  baseLine: MergedLine | null;
  secLines: ParsedLrc['lyricLines'];
  onClose: () => void;
  onMatch: (secIdx: number) => void;
}

function ManualMatchModal({ open, baseLine, secLines, onClose, onMatch }: ManualMatchModalProps) {
  const [selectedSec, setSelectedSec] = useState<number | null>(null);
  useEffect(() => {
    return () => {
      document.documentElement.style.overflow = '';
      document.documentElement.style.paddingRight = '';
    };
  }, []);
  if (!open || !baseLine) return null;

  return (
    <Modal
      visible={open}
      title={`手动匹配 — [${formatTimestamp(baseLine.timestamp)}] ${baseLine.text}`}
      onClose={onClose}
      width={700}
      footer={
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <Button onClick={onClose}>取消</Button>
          <Button
            type="danger"
            onClick={() => { onMatch(-1); onClose(); }}
          >
            清除匹配
          </Button>
          <Button
            type="primary"
            disabled={selectedSec === null}
            onClick={() => { if (selectedSec !== null) { onMatch(selectedSec); onClose(); } }}
          >
            确认匹配
          </Button>
        </div>
      }
    >
      <div style={{ marginBottom: 8, color: '#666', fontSize: 13 }}>
        选择要与该行匹配的副源歌词行：
      </div>
      <div className="manual-match-list">
        {secLines.map((sec, i) => (
          <div
            key={i}
            className={`manual-match-item${selectedSec === i ? ' selected' : ''}`}
            onClick={() => setSelectedSec(i)}
          >
            <span className="ts">[{formatTimestamp(sec.timestamp)}]</span>
            <span className="text">{sec.text}</span>
            {Object.entries(sec.tags).filter(([k]) => k.startsWith('tr:')).map(([k, v]) => (
              <span key={k} className="tr-preview"> [{k.replace('tr:', '')}] {v}</span>
            ))}
          </div>
        ))}
      </div>
    </Modal>
  );
}

interface MergeResult {
  headers: ParsedLrc['headers'];
  lines: MergedLine[];
  secondary: ParsedLrc;
}

export default function Lyric() {
  const [lrc1, setLrc1] = useState('');
  const [lrc2, setLrc2] = useState('');
  const [mergeResult, setMergeResult] = useState<MergeResult | null>(null);
  const [matchModal, setMatchModal] = useState<{ idx: number } | null>(null);
  const [outputText, setOutputText] = useState('');
  const [showOutput, setShowOutput] = useState(false);
  const [copied, setCopied] = useState(false);

  const handleMerge = useCallback(() => {
    if (!lrc1.trim()) return;
    const base = parseLRC(lrc1);
    const secondary = lrc2.trim() ? parseLRC(lrc2) : { headers: [], lyricLines: [] };
    const lines = mergeLRCs(base, secondary);
    setMergeResult({ headers: base.headers, lines, secondary });
    setShowOutput(false);
    setOutputText('');
  }, [lrc1, lrc2]);

  const handleLineTagChange = (idx: number, newTags: Record<string, string>) => {
    setMergeResult(prev => {
      if (!prev) return prev;
      const lines = [...prev.lines];
      lines[idx] = { ...lines[idx], tags: newTags };
      return { ...prev, lines };
    });
  };

  const handleLineTextChange = (idx: number, text: string) => {
    setMergeResult(prev => {
      if (!prev) return prev;
      const lines = [...prev.lines];
      lines[idx] = { ...lines[idx], text };
      return { ...prev, lines };
    });
  };

  const handleManualMatch = (baseIdx: number, secIdx: number) => {
    setMergeResult(prev => {
      if (!prev) return prev;
      const lines = [...prev.lines];
      const base = lines[baseIdx];
      if (secIdx === -1) {
        const newTags: Record<string, string> = {};
        for (const [k, v] of Object.entries(base.tags)) {
          if (!k.startsWith('tr:')) newTags[k] = v;
        }
        lines[baseIdx] = { ...base, tags: newTags, matchStatus: 'unmatched', matchedSecondaryIdx: null, similarity: 0 };
      } else {
        const secLines = prev.secondary.lyricLines.filter(l => l.text);
        const secLine = secLines[secIdx];
        const newTags = { ...base.tags };
        for (const [k, v] of Object.entries(secLine.tags)) {
          newTags[k] = v;
        }
        lines[baseIdx] = { ...base, tags: newTags, matchStatus: 'matched', matchedSecondaryIdx: secIdx, similarity: 1 };
      }
      return { ...prev, lines };
    });
  };

  const handleToggleNoSource = (idx: number) => {
    setMergeResult(prev => {
      if (!prev) return prev;
      const lines = [...prev.lines];
      const line = lines[idx];
      lines[idx] = {
        ...line,
        matchStatus: line.matchStatus === 'noSource' ? 'noSourceIncluded' : 'noSource',
      };
      return { ...prev, lines };
    });
  };

  const handleExport = () => {
    if (!mergeResult) return;
    const exportLines = mergeResult.lines.filter(l => l.matchStatus !== 'noSource');
    const text = serializeLRC(mergeResult.headers, exportLines);
    setOutputText(text);
    setShowOutput(true);
  };

  const handleCopy = () => {
    navigator.clipboard.writeText(outputText).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const handleDownload = () => {
    const blob = new Blob([outputText], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'merged.lrc';
    a.click();
    URL.revokeObjectURL(url);
  };

  const stats = mergeResult ? {
    total: mergeResult.lines.length,
    matched: mergeResult.lines.filter(l => l.matchStatus === 'matched').length,
    unmatched: mergeResult.lines.filter(l => l.matchStatus === 'unmatched').length,
    noSource: mergeResult.lines.filter(l => l.matchStatus === 'noSource' || l.matchStatus === 'noSourceIncluded').length,
  } : null;

  const secLinesForModal = mergeResult
    ? mergeResult.secondary.lyricLines.filter(l => l.text)
    : [];

  return (
    <div className="app">
      <header className="app-header">
        <h1>LRC 歌词合并工具</h1>
        <p className="subtitle">将多语言 LRC 歌词合并为一份，自动匹配并支持手动修正</p>
      </header>

      <div className="input-section">
        <DropZone content={lrc1} onContent={setLrc1} index={0} />
        <DropZone content={lrc2} onContent={setLrc2} index={1} />
      </div>

      <div className="action-bar">
        <Button
          type="primary"
          size="large"
          onClick={handleMerge}
          disabled={!lrc1.trim()}
        >
          开始合并
        </Button>
      </div>

      {mergeResult && stats && (
        <div className="result-section">
          <div className="result-header">
            <h2>合并结果</h2>
            <div className="stats">
              <Tag color="success">匹配 {stats.matched}</Tag>
              <Tag color="warning">未匹配 {stats.unmatched}</Tag>
              {stats.noSource > 0 && <Tag color="danger">仅副源 {stats.noSource}</Tag>}
              <span className="total">共 {stats.total} 行</span>
            </div>
            <Button type="primary" onClick={handleExport}>导出 LRC</Button>
          </div>

          <div className="merge-table-wrapper">
          <div className="merge-table">
            <div className="merge-table-head">
              <div className="col-ts">时间轴</div>
              <div className="col-text">歌词原文</div>
              <div className="col-tr">翻译</div>
              <div className="col-status">匹配状态</div>
              <div className="col-action">操作</div>
            </div>
            <div className="merge-table-body">
              {mergeResult.lines.map((line, idx) => (
                <div
                  key={idx}
                  className={`merge-row status-${line.matchStatus}`}
                >
                  <div className="col-ts mono">
                    [{formatTimestamp(line.timestamp)}]
                  </div>
                  <div className="col-text">
                    <input
                      className="text-input"
                      value={line.text || ''}
                      onChange={(e) => handleLineTextChange(idx, e.target.value)}
                    />
                  </div>
                  <div className="col-tr">
                    <TranslationCell
                      tags={line.tags}
                      onChange={(newTags) => handleLineTagChange(idx, newTags)}
                    />
                  </div>
                  <div className="col-status">
                    <StatusBadge status={line.matchStatus} sim={line.similarity} />
                  </div>
                  <div className="col-action">
                    {(line.matchStatus === 'noSource' || line.matchStatus === 'noSourceIncluded') ? (
                      <Button
                        size="small"
                        type={line.matchStatus === 'noSourceIncluded' ? 'danger' : 'primary'}
                        onClick={() => handleToggleNoSource(idx)}
                      >
                        {line.matchStatus === 'noSourceIncluded' ? '移出输出' : '加入输出'}
                      </Button>
                    ) : (
                      <Button
                        size="small"
                        onClick={() => setMatchModal({ idx })}
                      >
                        手动匹配
                      </Button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </div>
          </div>

          {showOutput && (
            <div className="output-section">
              <div className="output-header">
                <h3>输出内容</h3>
                <div className="output-actions">
                  <Button size="small" onClick={handleCopy}>
                    {copied ? '已复制!' : '复制'}
                  </Button>
                  <Button size="small" type="primary" onClick={handleDownload}>
                    下载 .lrc
                  </Button>
                </div>
              </div>
              <pre className="output-pre">{outputText}</pre>
            </div>
          )}
        </div>
      )}

      {matchModal && (
        <ManualMatchModal
          open={!!matchModal}
          baseLine={mergeResult!.lines[matchModal.idx]}
          secLines={secLinesForModal}
          onClose={() => setMatchModal(null)}
          onMatch={(secIdx) => handleManualMatch(matchModal.idx, secIdx)}
        />
      )}
    </div>
  );
}
