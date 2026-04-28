import React from 'react';
import { HashRouter, Routes, Route } from 'react-router-dom';
import Home from './pages/Home';
import Lyric from './pages/Lyric';
import Japanese from './pages/Japanese';

export default function App() {
  return (
    <HashRouter>
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/lyric" element={<Lyric />} />
        <Route path="/japanese" element={<Japanese />} />
      </Routes>
    </HashRouter>
  );
}
