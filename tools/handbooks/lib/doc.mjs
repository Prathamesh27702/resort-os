// Collects the body of a handbook, numbers headings (1, 1.1, 1.1.1) and records them for the contents page.
import { h1, h2, h3 } from './kit.mjs';

export class Body {
  constructor() { this.items = []; this.toc = []; this.n = [0, 0, 0]; }
  add(...xs) { for (const x of xs.flat(Infinity)) if (x) this.items.push(x); return this; }
  h1(title) { this.n = [this.n[0] + 1, 0, 0]; const n = `${this.n[0]}`; this.toc.push({ n, title, level: 1 }); return this.add(h1(`${n}   ${title}`)); }
  h2(title, { toc = true } = {}) { this.n[1]++; this.n[2] = 0; const n = `${this.n[0]}.${this.n[1]}`; if (toc) this.toc.push({ n, title, level: 2 }); return this.add(h2(`${n}   ${title}`)); }
  h3(title) { this.n[2]++; return this.add(h3(`${this.n[0]}.${this.n[1]}.${this.n[2]}   ${title}`)); }
}
