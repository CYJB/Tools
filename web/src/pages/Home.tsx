import React from 'react';
import { useNavigate } from 'react-router-dom';
import './Home.css';

interface Tool {
  path: string;
  icon: string;
  name: string;
  desc: string;
}

const TOOLS: Tool[] = [
  {
    path: '/lyric',
    icon: '🎵',
    name: 'LRC 歌词合并',
    desc: '将多语言 LRC 歌词合并为一份，自动匹配并支持手动修正',
  },
  {
    path: '/japanese',
    icon: '🇯🇵',
    name: '日文输入',
    desc: 'JIS 假名键盘，支持平假名/片假名切换、浊点合字',
  },
];

export default function Home() {
  const navigate = useNavigate();
  return (
    <div className="home">
      <header className="home-header">
        <h1>CYJB Tools</h1>
        <p className="home-subtitle">常用小工具合集</p>
      </header>
      <div className="home-grid">
        {TOOLS.map((t) => (
          <button key={t.path} className="tool-card" onClick={() => navigate(t.path)}>
            <div className="tool-icon">{t.icon}</div>
            <div className="tool-name">{t.name}</div>
            <div className="tool-desc">{t.desc}</div>
          </button>
        ))}
      </div>
      <footer className="home-footer">
        <a href="https://github.com/CYJB/Tools">GitHub</a>
      </footer>
    </div>
  );
}
