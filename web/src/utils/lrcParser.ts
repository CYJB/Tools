/**
 * LRC file parser and merger.
 *
 * LRC lines can be:
 *   [mm:ss.xxx] text          - lyric line
 *   [mm:ss.xxx][tr:lang] text - translation
 *   [mm:ss.xxx][tt] ...       - timing track
 *   [key:value]               - metadata header
 */

/** Remove ruby/furigana annotation: 《text》 or ｛text｝ patterns, keep base text */
function stripRuby(text: string): string {
  return text
    .replace(/《[^》]*》/g, '')
    .replace(/｛[^｝]*｝/g, '')
    .replace(/\([ぁ-ん]*\)/g, '')
    .trim();
}

/** Normalize text for comparison: strip ruby, collapse spaces, lowercase */
export function normalizeForMatch(text: string): string {
  return stripRuby(text)
    .replace(/[\s　]+/g, ' ')
    .toLowerCase()
    .trim();
}

/** Parse [mm:ss.xxx] timestamp to milliseconds */
export function parseTimestamp(ts: string): number | null {
  const m = ts.match(/^(\d+):(\d+(?:\.\d+)?)$/);
  if (!m) return null;
  return Math.round((parseInt(m[1], 10) * 60 + parseFloat(m[2])) * 1000);
}

/** Format milliseconds to [mm:ss.xxx] */
export function formatTimestamp(ms: number): string {
  const totalSec = ms / 1000;
  const min = Math.floor(totalSec / 60);
  const sec = (totalSec % 60).toFixed(3).padStart(6, '0');
  return `${String(min).padStart(2, '0')}:${sec}`;
}

export interface LrcHeader {
  key: string;
  value: string;
}

export interface LrcLine {
  timestamp: number;
  text: string | null;
  tags: Record<string, string>;
}

export interface ParsedLrc {
  headers: LrcHeader[];
  lyricLines: LrcLine[];
}

export interface MergedLine {
  timestamp: number;
  text: string | null;
  tags: Record<string, string>;
  matchStatus: 'matched' | 'unmatched' | 'noSource' | 'noSourceIncluded';
  matchedSecondaryIdx: number | null;
  similarity: number;
}

/**
 * Parse an LRC string into structured form.
 */
export function parseLRC(raw: string): ParsedLrc {
  const lines = raw.split('\n').map(l => l.trim()).filter(Boolean);
  const headers: LrcHeader[] = [];
  const lyricMap = new Map<number, LrcLine>();

  const headerRe = /^\[([a-zA-Z][^:\]]*):([^\]]*)\]$/;

  for (const line of lines) {
    if (headerRe.test(line)) {
      const m = line.match(headerRe)!;
      headers.push({ key: m[1], value: m[2] });
      continue;
    }

    let rest = line;
    const timestamps: number[] = [];

    while (true) {
      const tm = rest.match(/^\[(\d+:\d+(?:\.\d+)?)\]/);
      if (!tm) break;
      const ts = parseTimestamp(tm[1]);
      if (ts !== null) timestamps.push(ts);
      rest = rest.slice(tm[0].length);
    }

    if (timestamps.length === 0) continue;

    let tagName: string | null = null;
    let tagContent: string | null = null;

    const tagM = rest.match(/^\[([^\]]+)\](.*)/);
    if (tagM) {
      tagName = tagM[1];
      tagContent = tagM[2].trim();
    } else {
      tagContent = rest.trim();
    }

    for (const ts of timestamps) {
      if (!lyricMap.has(ts)) {
        lyricMap.set(ts, { timestamp: ts, text: null, tags: {} });
      }
      const entry = lyricMap.get(ts)!;
      if (tagName) {
        entry.tags[tagName] = tagContent ?? '';
      } else if (tagContent) {
        entry.text = tagContent;
      }
    }
  }

  const lyricLines = Array.from(lyricMap.values()).sort((a, b) => a.timestamp - b.timestamp);
  return { headers, lyricLines };
}

function similarity(a: string | null, b: string | null): number {
  if (!a || !b) return 0;
  if (a === b) return 1;
  const na = normalizeForMatch(a);
  const nb = normalizeForMatch(b);
  if (!na || !nb) return 0;
  if (na === nb) return 1;

  const m = na.length, n = nb.length;
  if (m === 0 || n === 0) return 0;
  let prev = new Int32Array(n + 1);
  let curr = new Int32Array(n + 1);
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      if (na[i - 1] === nb[j - 1]) {
        curr[j] = prev[j - 1] + 1;
      } else {
        curr[j] = Math.max(prev[j], curr[j - 1]);
      }
    }
    [prev, curr] = [curr, prev];
  }
  return (2 * prev[n]) / (m + n);
}

const MATCH_THRESHOLD = 0.6;
const TIME_TOLERANCE_MS = 3000;

export function mergeLRCs(baseParsed: ParsedLrc, secondaryParsed: ParsedLrc): MergedLine[] {
  const baseLines = baseParsed.lyricLines.filter(l => l.text);
  const secLines = secondaryParsed.lyricLines.filter(l => l.text);

  const usedSec = new Set<number>();

  const result: MergedLine[] = baseLines.map((base) => {
    const mergedTags = { ...base.tags };
    let bestIdx = -1;
    let bestSim = 0;
    let bestTimeDiff = Infinity;

    for (let i = 0; i < secLines.length; i++) {
      if (usedSec.has(i)) continue;
      const sec = secLines[i];
      const timeDiff = Math.abs(sec.timestamp - base.timestamp);
      if (timeDiff > TIME_TOLERANCE_MS) continue;

      const sim = similarity(base.text, sec.text);
      if (sim > bestSim || (sim === bestSim && timeDiff < bestTimeDiff)) {
        bestSim = sim;
        bestIdx = i;
        bestTimeDiff = timeDiff;
      }
    }

    let matchStatus: MergedLine['matchStatus'];
    if (bestIdx >= 0 && bestSim >= MATCH_THRESHOLD) {
      matchStatus = 'matched';
      usedSec.add(bestIdx);
      const secEntry = secLines[bestIdx];
      for (const [k, v] of Object.entries(secEntry.tags)) {
        if (!mergedTags[k]) mergedTags[k] = v;
      }
    } else {
      matchStatus = 'unmatched';
    }

    return {
      timestamp: base.timestamp,
      text: base.text,
      tags: mergedTags,
      matchStatus,
      matchedSecondaryIdx: bestIdx >= 0 ? bestIdx : null,
      similarity: bestSim,
    };
  });

  for (let i = 0; i < secLines.length; i++) {
    if (usedSec.has(i)) continue;
    const sec = secLines[i];
    const hasTr = Object.keys(sec.tags).some(k => k.startsWith('tr:'));
    if (!hasTr) continue;
    result.push({
      timestamp: sec.timestamp,
      text: sec.text,
      tags: { ...sec.tags },
      matchStatus: 'noSource',
      matchedSecondaryIdx: i,
      similarity: 0,
    });
  }

  result.sort((a, b) => a.timestamp - b.timestamp);
  return result;
}

export function serializeLRC(headers: LrcHeader[], mergedLines: MergedLine[]): string {
  const parts: string[] = [];
  for (const { key, value } of headers) {
    parts.push(`[${key}:${value}]`);
  }
  if (parts.length) parts.push('');

  for (const line of mergedLines) {
    const ts = formatTimestamp(line.timestamp);
    parts.push(`[${ts}]${line.text}`);
    for (const [tag, val] of Object.entries(line.tags)) {
      parts.push(`[${ts}][${tag}]${val}`);
    }
  }
  return parts.join('\n');
}
