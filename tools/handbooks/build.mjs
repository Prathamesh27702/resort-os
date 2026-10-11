// Builds the two handbooks into docs/handbooks/:
//   Resort-OS-Product-Overview.docx        — for clients and partners (plain language)
//   Resort-OS-Software-Documentation.docx  — for developers, reviewers and the owner
// Usage (from tools/handbooks):  npm ci   then   npm run build
// Optional: DOC_DATE=2026-10-10 to fix the date printed on the cover (defaults to today).
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Document, Packer, Paragraph, TextRun } from 'docx';
import { loadRepo, two } from './lib/repo.mjs';
import { styles, numbering, PAGE, C, cover, contents, header, footer, table, h1, p, small, spacer, TEXT_W } from './lib/kit.mjs';
import { clientBody } from './content/client.mjs';
import { technicalBody } from './content/technical.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const r = loadRepo(root);
const edition = r.history.at(-1);
const date = new Date(process.env.DOC_DATE ?? edition.date ?? Date.now());
const longDate = date.toLocaleDateString('en-IN', { day: 'numeric', month: 'long', year: 'numeric' });
const outDir = join(root, 'docs', 'handbooks');
mkdirSync(outDir, { recursive: true });

const page = { size: { width: PAGE.width, height: PAGE.height }, margin: { top: PAGE.margin, bottom: PAGE.margin, left: PAGE.margin, right: PAGE.margin, header: 600, footer: 600 } };

function controlPage(title, audience, distribution) {
  return [
    new Paragraph({ heading: 'Heading1', children: [new TextRun('Document control')] }),
    table(['Version', 'Date', 'Change'], [...r.history].reverse().map(h => [h.version, String(h.date), h.change]), [1100, 1500, TEXT_W - 2600], { zebra: true }),
    spacer(8),
    table(null, [
      ['Document', title],
      ['Audience', audience],
      ['Status', `Living document, rebuilt at the end of every sprint. This edition reflects Sprint ${two(r.lastDone?.id ?? 0)} accepted and Sprint ${two(r.current.id)} ${r.current.status === 'in-review' ? 'built and in review' : 'in progress'}.`],
      ['Owner', 'Prathamesh, product owner'],
      ['Distribution', distribution],
    ], [2200, TEXT_W - 2200], { fills: (ri, ci) => (ci === 0 ? C.light : undefined), boldFirst: true }),
  ];
}

function build({ file, title, subtitle, kicker, audience, distribution, body, note }) {
  const b = body(r);
  const doc = new Document({
    creator: 'Resort OS', title: `Resort OS — ${title}`, description: subtitle,
    styles, numbering,
    sections: [
      { properties: { page, titlePage: true }, children: cover({
          kicker, title: `Resort OS\n${title}`.replace('\n', ' — '), subtitle,
          details: [
            ['Edition', `Version ${edition.version}`],
            ['Date', longDate],
            ['Covers', `Sprints ${r.done.map(s => two(s.id)).join(', ')} accepted · Sprint ${two(r.current.id)} ${r.current.title} ${r.current.status === 'in-review' ? 'in review' : 'in progress'}`],
            ['Prepared by', 'Prathamesh, product owner, Resort OS'],
            ['Classification', 'Confidential'],
          ],
          note,
        }) },
      { properties: { page }, headers: { default: header(title) }, footers: { default: footer(`Version ${edition.version} · ${longDate}`) },
        children: [
          ...controlPage(`Resort OS — ${title}`, audience, distribution),
          h1('Contents'),
          ...contents(b.toc),
          ...b.items,
        ] },
    ],
  });
  return Packer.toBuffer(doc).then(buf => { writeFileSync(join(outDir, file), buf); console.log(`docs/handbooks/${file}  (${(buf.length / 1024).toFixed(0)} KB)`); });
}

await build({
  file: 'Resort-OS-Product-Overview.docx',
  title: 'Product Overview',
  kicker: 'For clients and partners',
  subtitle: 'One system to run an Indian resort: rooms and dormitory beds, bookings, front desk, GST billing, restaurant, staff and more.',
  audience: 'Resort owners and managers, prospective clients, partners and investors. No technical knowledge needed.',
  distribution: 'May be shared with clients and partners. Contains no passwords, keys or personal data.',
  note: 'Features marked as planned are part of the agreed roadmap and are delivered sprint by sprint; the roadmap section shows the status of each.',
  body: clientBody,
});
await build({
  file: 'Resort-OS-Software-Documentation.docx',
  title: 'Software Documentation',
  kicker: 'Technical handbook',
  subtitle: 'Architecture, technology, design of the UI, API and database, the sprint-by-sprint record, code and test results.',
  audience: 'Developers, technical reviewers and auditors, and the product owner.',
  distribution: 'Internal and technical reviewers under confidentiality. Contains no passwords or keys; it names repository paths and internal design details.',
  body: technicalBody,
});
