// Small layout kit on top of the `docx` package: the house style for both handbooks.
// Everything that decides how the documents look is in this file.
import { readFileSync } from 'node:fs';
import {
  AlignmentType, BorderStyle, Footer, Header, HeadingLevel, ImageRun, LevelFormat, PageBreak, PageNumber,
  Paragraph, ShadingType, Table, TableCell, TableLayoutType, TableRow, TabStopType, TextRun, VerticalAlign, WidthType,
} from 'docx';

export const C = {
  tide: '0E3B43', tide2: '2F6F7B', kokum: '8A2449', palm: '2E6B3C', ink: '1B2A2E', grey: '5F6B6E',
  light: 'F5F7F6', rule: 'DCE3E1', wash: 'EAF1F2', amber: '8A5A00', amberBg: 'FBEFD8', greenBg: 'E3F0E6', greyBg: 'ECEFF0',
};
export const FONT = 'Calibri';
// A4 with 0.9" margins → 9314 DXA of text width
export const PAGE = { width: 11906, height: 16838, margin: 1296 };
export const TEXT_W = PAGE.width - 2 * PAGE.margin;

export const styles = {
  default: { document: { run: { font: FONT, size: 21, color: C.ink } } },
  paragraphStyles: [
    { id: 'Normal', name: 'Normal', run: { font: FONT, size: 21, color: C.ink }, paragraph: { spacing: { after: 120, line: 276, lineRule: 'auto' } } },
    { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
      run: { font: FONT, size: 36, bold: true, color: C.tide },
      paragraph: { spacing: { before: 0, after: 200 }, outlineLevel: 0, keepNext: true,
        border: { bottom: { style: BorderStyle.SINGLE, size: 12, color: C.kokum, space: 6 } } } },
    { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
      run: { font: FONT, size: 28, bold: true, color: C.tide },
      paragraph: { spacing: { before: 320, after: 120 }, outlineLevel: 1, keepNext: true } },
    { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true,
      run: { font: FONT, size: 23, bold: true, color: C.kokum },
      paragraph: { spacing: { before: 220, after: 80 }, outlineLevel: 2, keepNext: true } },
    { id: 'Caption', name: 'Caption', basedOn: 'Normal', run: { size: 18, italics: true, color: C.grey },
      paragraph: { alignment: AlignmentType.CENTER, spacing: { before: 60, after: 240 } } },
    { id: 'Small', name: 'Small', basedOn: 'Normal', run: { size: 18, color: C.grey }, paragraph: { spacing: { after: 80 } } },
  ],
};

export const numbering = {
  config: [
    { reference: 'bullets', levels: [
      { level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 360, hanging: 240 } } } },
      { level: 1, format: LevelFormat.BULLET, text: '–', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 720, hanging: 240 } } } },
    ] },
    { reference: 'steps', levels: [
      { level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 360, hanging: 300 } } } },
    ] },
  ],
};

// Inline markup: **bold** and `code` (underscores are left alone: table and column names use them)
export function runs(text, base = {}) {
  const out = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`)/g;
  let last = 0; let m;
  const s = String(text);
  while ((m = re.exec(s))) {
    if (m.index > last) out.push(new TextRun({ text: s.slice(last, m.index), ...base }));
    const tok = m[0];
    if (tok.startsWith('**')) out.push(new TextRun({ text: tok.slice(2, -2), bold: true, ...base }));
    else out.push(new TextRun({ text: tok.slice(1, -1), font: 'Consolas', size: (base.size ?? 21) - 2, color: base.color ?? C.tide }));
    last = m.index + tok.length;
  }
  if (last < s.length) out.push(new TextRun({ text: s.slice(last), ...base }));
  return out;
}

export const p = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), ...opts.para });
export const small = (text) => new Paragraph({ style: 'Small', children: runs(text, { size: 18, color: C.grey }) });
export const h1 = (text) => new Paragraph({ heading: HeadingLevel.HEADING_1, pageBreakBefore: true, children: [new TextRun(text)] });
export const h2 = (text) => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(text)] });
export const h3 = (text) => new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(text)] });
export const pageBreak = () => new Paragraph({ children: [new PageBreak()] });
export const spacer = (pts = 6) => new Paragraph({ spacing: { before: 0, after: pts * 20 }, children: [] });

export const bullets = (items, level = 0) => items.map(t => new Paragraph({ numbering: { reference: 'bullets', level }, spacing: { after: 60 }, children: runs(t) }));
let stepInstance = 0;
export const steps = (items) => { stepInstance++; return items.map(t => new Paragraph({ numbering: { reference: 'steps', level: 0, instance: stepInstance }, spacing: { after: 60 }, children: runs(t) })); };

const border = (color = C.rule) => ({ style: BorderStyle.SINGLE, size: 4, color });
const cellBorders = (color) => ({ top: border(color), bottom: border(color), left: border(color), right: border(color) });

function cell(content, width, { fill, header = false, color, bold, size = 19, align } = {}) {
  const paras = (Array.isArray(content) ? content : [content]).map(c => c instanceof Paragraph ? c
    : new Paragraph({ alignment: align, spacing: { after: 40, line: 252, lineRule: 'auto' }, children: runs(c ?? '', { size, color: header ? 'FFFFFF' : color, bold: header || bold }) }));
  return new TableCell({
    width: { size: width, type: WidthType.DXA },
    borders: cellBorders(header ? C.tide : C.rule),
    shading: fill || header ? { type: ShadingType.CLEAR, fill: header ? C.tide : fill, color: 'auto' } : undefined,
    margins: { top: 70, bottom: 50, left: 110, right: 110 },
    verticalAlign: VerticalAlign.TOP,
    children: paras,
  });
}

/** table(['A','B'], [['1','2']], [3000, 6314], {zebra:true, fills: (row, col) => hex}) — widths in DXA, must sum to TEXT_W */
export function table(headers, rows, widths, opts = {}) {
  const total = widths.reduce((a, b) => a + b, 0);
  const trs = [];
  if (headers) trs.push(new TableRow({ tableHeader: true, cantSplit: true, children: headers.map((h, i) => cell(h, widths[i], { header: true })) }));
  rows.forEach((r, ri) => trs.push(new TableRow({ cantSplit: true, children: r.map((c, i) => cell(c, widths[i], {
    fill: opts.fills?.(ri, i, r) ?? (opts.zebra && ri % 2 === 1 ? C.light : undefined),
    bold: opts.boldFirst && i === 0, color: opts.colors?.(ri, i, r), size: opts.size,
  })) })));
  return new Table({ width: { size: total, type: WidthType.DXA }, columnWidths: widths, layout: TableLayoutType.FIXED, rows: trs });
}

/** Two-column label/value table */
export const kv = (pairs, w1 = 2600) => table(null, pairs, [w1, TEXT_W - w1], { fills: (r, c) => (c === 0 ? C.light : undefined), boldFirst: true });

/** Shaded box with a title line and body paragraphs */
export function callout(title, body, { fill = C.wash, accent = C.tide } = {}) {
  const children = [new Paragraph({ spacing: { after: 60 }, children: [new TextRun({ text: title, bold: true, color: accent, size: 22 })] })];
  for (const b of Array.isArray(body) ? body : [body]) {
    if (b instanceof Paragraph) children.push(b);
    else children.push(new Paragraph({ spacing: { after: 60, line: 264, lineRule: 'auto' }, children: runs(b, { size: 20 }) }));
  }
  return new Table({
    width: { size: TEXT_W, type: WidthType.DXA }, columnWidths: [TEXT_W], layout: TableLayoutType.FIXED,
    rows: [new TableRow({ cantSplit: true, children: [new TableCell({
      width: { size: TEXT_W, type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill, color: 'auto' },
      borders: { top: border(fill), bottom: border(fill), right: border(fill), left: { style: BorderStyle.SINGLE, size: 24, color: accent } },
      margins: { top: 120, bottom: 80, left: 200, right: 200 },
      children,
    })] })],
  });
}

/** Row of big-number tiles: [{value, label}] */
export function stats(items) {
  const w = Math.floor(TEXT_W / items.length);
  const widths = items.map((_, i) => (i === items.length - 1 ? TEXT_W - w * (items.length - 1) : w));
  return new Table({
    width: { size: TEXT_W, type: WidthType.DXA }, columnWidths: widths, layout: TableLayoutType.FIXED,
    rows: [new TableRow({ cantSplit: true, children: items.map((it, i) => new TableCell({
      width: { size: widths[i], type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill: C.light, color: 'auto' },
      borders: cellBorders('FFFFFF'),
      margins: { top: 140, bottom: 120, left: 120, right: 120 },
      children: [
        new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 20 }, children: [new TextRun({ text: String(it.value), bold: true, size: 40, color: it.color ?? C.tide })] }),
        new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 0, line: 240, lineRule: 'auto' }, children: [new TextRun({ text: it.label, size: 17, color: C.grey })] }),
      ],
    })) })],
  });
}

function pngSize(buf) { return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20) }; }

/** Image scaled to widthIn inches (default: full text width), with an optional caption */
export function image(path, { widthIn, caption, border: withBorder = false } = {}) {
  const data = readFileSync(path);
  const { w, h } = pngSize(data);
  const maxW = TEXT_W / 1440;
  const wi = Math.min(widthIn ?? maxW, maxW);
  const px = Math.round(wi * 96);
  const out = [new Paragraph({
    alignment: AlignmentType.CENTER, keepNext: !!caption, spacing: { before: 120, after: caption ? 0 : 200, line: 240, lineRule: 'auto' },
    border: withBorder ? { top: border(), bottom: border(), left: border(), right: border() } : undefined,
    children: [new ImageRun({ type: 'png', data, transformation: { width: px, height: Math.round(px * h / w) },
      altText: { title: caption ?? 'diagram', description: caption ?? 'diagram', name: caption ?? 'diagram' } })],
  })];
  if (caption) out.push(new Paragraph({ style: 'Caption', children: [new TextRun(caption)] }));
  return out;
}

/** Pill-like status text for table cells */
export const STATUS = {
  done: { text: 'Done', fill: C.greenBg, color: C.palm },
  'in-review': { text: 'In review', fill: C.amberBg, color: C.amber },
  'in-progress': { text: 'In progress', fill: C.amberBg, color: C.kokum },
  planned: { text: 'Planned', fill: undefined, color: C.grey },
};

export function header(title) {
  return new Header({ children: [new Paragraph({
    tabStops: [{ type: TabStopType.RIGHT, position: TEXT_W }],
    border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: C.rule, space: 4 } },
    children: [new TextRun({ text: 'RESORT OS', bold: true, color: C.tide, size: 16, characterSpacing: 20 }),
      new TextRun({ text: `  ·  ${title}`, color: C.grey, size: 16 }),
      new TextRun({ text: '\tConfidential', color: C.kokum, size: 16 })],
  })] });
}

export function footer(versionText) {
  return new Footer({ children: [new Paragraph({
    tabStops: [{ type: TabStopType.RIGHT, position: TEXT_W }],
    border: { top: { style: BorderStyle.SINGLE, size: 4, color: C.rule, space: 4 } },
    children: [new TextRun({ text: versionText, color: C.grey, size: 16 }),
      new TextRun({ children: ['\tPage ', PageNumber.CURRENT, ' of ', PageNumber.TOTAL_PAGES], color: C.grey, size: 16 })],
  })] });
}

/** Cover page content: a tide banner block, title, subtitle, and a details table */
export function cover({ kicker, title, subtitle, details, note }) {
  const banner = new Table({
    width: { size: TEXT_W, type: WidthType.DXA }, columnWidths: [TEXT_W], layout: TableLayoutType.FIXED,
    rows: [new TableRow({ height: { value: 4600, rule: 'atLeast' }, children: [new TableCell({
      width: { size: TEXT_W, type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill: C.tide, color: 'auto' },
      borders: cellBorders(C.tide),
      margins: { top: 600, bottom: 400, left: 500, right: 500 },
      verticalAlign: VerticalAlign.BOTTOM,
      children: [
        new Paragraph({ spacing: { after: 280, line: 240, lineRule: 'auto' }, children: [new TextRun({ text: kicker.toUpperCase(), color: 'C9DADD', size: 20, bold: true, characterSpacing: 40 })] }),
        new Paragraph({ spacing: { after: 240, line: 240, lineRule: 'auto' }, children: [new TextRun({ text: title, color: 'FFFFFF', size: 60, bold: true })] }),
        new Paragraph({ spacing: { after: 0 }, border: { left: { style: BorderStyle.SINGLE, size: 24, color: C.kokum, space: 10 } },
          children: [new TextRun({ text: subtitle, color: 'E6EEF0', size: 26 })] }),
      ],
    })] })],
  });
  return [
    spacer(30), banner, spacer(18),
    kv(details, 2600),
    spacer(10),
    ...(note ? [new Paragraph({ spacing: { before: 200 }, children: runs(note, { size: 18, color: C.grey }) })] : []),
  ];
}

/** Manual table of contents (no Word field to refresh): [{n, title, level}] */
export function contents(entries) {
  return entries.map(e => new Paragraph({
    spacing: { after: e.level === 1 ? 70 : 30, before: e.level === 1 ? 90 : 0 },
    indent: { left: e.level === 1 ? 0 : 420 },
    children: [new TextRun({ text: `${e.n}  `, bold: e.level === 1, color: C.kokum, size: e.level === 1 ? 21 : 19 }),
      new TextRun({ text: e.title, bold: e.level === 1, color: e.level === 1 ? C.tide : C.ink, size: e.level === 1 ? 21 : 19 })],
  }));
}
