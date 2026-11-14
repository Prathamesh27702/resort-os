import { inZone } from '../../core/property-context';
import { OrderBill, PaperWidth } from './india.api';

/**
 * Thermal receipts (Sprint 30): the restaurant bill and the kitchen ticket (KOT) as plain HTML for 80 mm and 58 mm roll
 * paper, the two widths Indian restaurants use. The same HTML is shown as the preview and printed from a hidden frame
 * with its own page size, so the app's menu, colours and dialogs never reach the paper. Everything is black on white,
 * in one font, because a thermal head prints nothing else well.
 */

/** A kitchen ticket to print. */
export interface KotTicket {
  number: string; orderNumber: string; where: string; sentAt: string; sentByName: string | null; note: string | null; reprint: boolean;
  items: { quantity: number; name: string; modifiers: string | null; note: string | null; voided: boolean }[];
}

const esc = (s: string | number | null | undefined) =>
  String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** 1,250.00 (Indian grouping, always two decimals, no sign: the column heading says ₹). */
export const amount = (v: number) => new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);

const when = (iso: string, tz: string) =>
  inZone(iso, tz, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });

/** The bill: seller with GSTIN and FSSAI number, the dishes, discount, each tax, the total, then the UPI QR or how it was paid. */
export function billHtml(b: OrderBill, tz: string): string {
  const p = b.property;
  const head = [
    `<h1>${esc(p.name)}</h1>`,
    p.legalName && p.legalName !== p.name ? `<p class="c">${esc(p.legalName)}</p>` : '',
    ...p.addressLines.map(l => `<p class="c">${esc(l)}</p>`),
    p.phone ? `<p class="c">Ph: ${esc(p.phone)}</p>` : '',
    p.gstin ? `<p class="c">GSTIN: ${esc(p.gstin)}</p>` : '',
    p.fssaiLicenceNo ? `<p class="c" data-fssai>FSSAI Lic. No. ${esc(p.fssaiLicenceNo)}</p>` : '',
  ].join('');
  const meta = [
    `<div class="row"><span>Bill no. <b>${esc(b.number)}</b></span><span>${esc(when(b.printedAt, tz))}</span></div>`,
    `<div class="row"><span>${esc(b.where)}${b.covers ? `, ${b.covers} guest${b.covers === 1 ? '' : 's'}` : ''}</span><span>${b.waiterName ? esc(b.waiterName) : ''}</span></div>`,
    b.guestName ? `<p>Guest: ${esc(b.guestName)}</p>` : '',
  ].join('');
  const lines = b.lines.map(l =>
    `<tr><td>${esc(l.name)}${l.detail ? `<small>${esc(l.detail)}</small>` : ''}</td><td class="r">${l.quantity}</td><td class="r">${amount(l.unitPrice)}</td><td class="r">${amount(l.amount)}</td></tr>`).join('');
  const totals = [
    `<div class="row"><span>Subtotal</span><span>${amount(b.subtotal)}</span></div>`,
    b.discountAmount ? `<div class="row"><span>${esc(b.discountLabel ?? 'Discount')}</span><span>−${amount(b.discountAmount)}</span></div>` : '',
    ...b.taxes.map(t => `<div class="row"><span>${esc(t.code)} ${t.rate}%</span><span>${amount(t.amount)}</span></div>`),
    `<div class="row total"><span>TOTAL</span><span>₹${amount(b.total)}</span></div>`,
  ].join('');
  const foot = [
    b.sacCode ? `<p class="c sm">SAC ${esc(b.sacCode)} (restaurant service)</p>` : '',
    !b.taxes.length && b.taxNote ? `<p class="c sm">No GST charged</p>` : '',
    b.paidBy ? `<p class="c paid">${esc(b.paidBy)}</p>` : '',
    b.upi ? `<div class="qr"><img src="${esc(b.upi.qrPng)}" alt="UPI QR" /><p class="c"><b>Scan to pay ₹${amount(b.total)} by UPI</b></p><p class="c sm">${esc(b.upi.upiId)}</p></div>` : '',
    `<p class="c">Thank you. Please visit again.</p>`,
  ].join('');
  return `${head}<hr />${meta}<hr /><table><thead><tr><th>Item</th><th class="r">Qty</th><th class="r">Rate</th><th class="r">₹</th></tr></thead><tbody>${lines}</tbody></table><hr />${totals}<hr />${foot}`;
}

/** The kitchen ticket: big, with what to cook and nothing about money. Voided lines stay on it, struck through. */
export function kotHtml(k: KotTicket, tz: string): string {
  const items = k.items.map(i =>
    `<li class="${i.voided ? 'void' : ''}"><b>${i.quantity} ×</b> ${esc(i.name)}${i.modifiers ? `<small>${esc(i.modifiers)}</small>` : ''}${i.note ? `<small class="note">** ${esc(i.note)} **</small>` : ''}${i.voided ? '<small>VOID: do not make</small>' : ''}</li>`).join('');
  return [
    `<h1>KITCHEN TICKET</h1>`,
    k.reprint ? `<p class="c">(reprint)</p>` : '',
    `<div class="row big"><span>${esc(k.number)}</span><span>${esc(k.where)}</span></div>`,
    `<div class="row"><span>Order ${esc(k.orderNumber)}</span><span>${esc(when(k.sentAt, tz))}</span></div>`,
    k.sentByName ? `<p>Waiter: ${esc(k.sentByName)}</p>` : '',
    `<hr /><ul class="kot">${items}</ul>`,
    k.note ? `<hr /><p class="note">Note: ${esc(k.note)}</p>` : '',
    `<hr /><p class="c sm">${k.items.filter(i => !i.voided).reduce((s, i) => s + i.quantity, 0)} item(s)</p>`,
  ].join('');
}

/** Styles shared by the preview and the printed page. Sizes are in points and millimetres so paper and screen agree. */
export const RECEIPT_CSS = `
.receipt { box-sizing: border-box; background: #fff; color: #000; font-family: 'DejaVu Sans Mono', 'Liberation Mono', Consolas, 'Courier New', monospace; font-size: 9.5pt; line-height: 1.3; padding: 3mm 3mm 6mm; }
.receipt.w80 { width: 80mm; } .receipt.w58 { width: 58mm; font-size: 8pt; padding: 2mm 2mm 5mm; }
.receipt * { box-sizing: border-box; }
.receipt h1 { font-size: 1.25em; margin: 0 0 1mm; text-align: center; text-transform: uppercase; }
.receipt p { margin: 0; overflow-wrap: anywhere; } .receipt .c { text-align: center; } .receipt .sm { font-size: .85em; }
.receipt hr { border: 0; border-top: 1px dashed #000; margin: 1.5mm 0; }
.receipt .row { display: flex; justify-content: space-between; gap: 2mm; } .receipt .row span:last-child { text-align: right; white-space: nowrap; }
.receipt .row.total { font-weight: 700; font-size: 1.35em; margin-top: 1mm; } .receipt .row.big { font-weight: 700; font-size: 1.5em; }
.receipt table { width: 100%; border-collapse: collapse; } .receipt th { text-align: left; font-weight: 700; border-bottom: 1px solid #000; padding-bottom: .5mm; }
.receipt td, .receipt th { vertical-align: top; padding: .4mm 0; } .receipt .r { text-align: right; white-space: nowrap; padding-left: 1.5mm; }
.receipt td small, .receipt li small { display: block; font-size: .85em; }
.receipt .paid { font-weight: 700; margin: 1mm 0; }
.receipt .qr { text-align: center; margin: 2mm 0; } .receipt .qr img { width: 34mm; height: 34mm; image-rendering: pixelated; }
.receipt.w58 .qr img { width: 30mm; height: 30mm; }
.receipt ul.kot { list-style: none; margin: 0; padding: 0; } .receipt ul.kot li { font-size: 1.3em; padding: 1mm 0; border-bottom: 1px dotted #000; }
.receipt ul.kot li.void { text-decoration: line-through; } .receipt ul.kot li.void small:last-child { text-decoration: none; font-weight: 700; }
.receipt .note { font-weight: 700; }
`;

/**
 * Prints a receipt from a hidden frame whose page is the roll's width and as long as the receipt. Returns false when the
 * browser gave no frame to print from (then the caller says so). The frame is removed once the print dialog has gone.
 */
export function printReceipt(html: string, width: PaperWidth, title: string): boolean {
  const frame = document.createElement('iframe');
  frame.setAttribute('aria-hidden', 'true');
  frame.title = title;
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
  document.body.appendChild(frame);
  const doc = frame.contentDocument;
  const win = frame.contentWindow;
  if (!doc || !win) { frame.remove(); return false; }
  doc.open();
  doc.write(`<!doctype html><html><head><meta charset="utf-8"><title>${esc(title)}</title><style>@page { size: ${width}mm auto; margin: 0; } html, body { margin: 0; padding: 0; background: #fff; }${RECEIPT_CSS}</style></head><body><div class="receipt w${width}">${html}</div></body></html>`);
  doc.close();
  const go = () => {
    win.focus();
    win.print();
    setTimeout(() => frame.remove(), 60_000);
  };
  const waiting = Array.from(doc.images).filter(i => !i.complete);
  if (!waiting.length) { setTimeout(go, 50); return true; }
  let left = waiting.length;
  const one = () => { if (--left === 0) go(); };
  waiting.forEach(i => { i.addEventListener('load', one); i.addEventListener('error', one); });
  return true;
}
