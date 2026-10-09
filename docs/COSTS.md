# Costs — free during development, pay only when the resort goes live

Principle (brief §76): **spend ₹0 while building**; buy the smallest paid tier only when something real needs
it, usually at go-live. Prices were checked in October 2026 from the vendors' pages (sources at the end). They change, so
check the vendor page before buying. Figures marked *estimate* are planning numbers, not quotes.

## Summary

| Stage | When | Monthly cost |
|---|---|---|
| **Development** (now → ~Sprint 28) | Building and testing on your PCs | **₹0** |
| **Staging / demo** (optional, from ~Sprint 11) | Show the system online; test Razorpay/WhatsApp webhooks | **₹0** on free tiers |
| **Production** (Sprint 29, resort opens) | Real guests, real money | *estimate* **₹2,500–₹6,000/month** + per-transaction fees |

## 1. Development — ₹0

| What | Free because | Limit to watch |
|---|---|---|
| Git, VS Code, .NET 10 SDK, Node.js, PowerShell | Free and open source | — |
| **C# Dev Kit** (VS Code) | Free for personal use and **commercial teams of up to 5 developers** | 6+ developers need Visual Studio Professional licences |
| **Docker Desktop** | Free for businesses with **< 250 employees and < US$10M revenue** | Above that it's paid; Docker Engine on Linux/WSL is always free |
| PostgreSQL 16 (in Docker on your PC) | Open source | — |
| **GitHub Free**, private repo, unlimited collaborators | Free plan | **2,000 Actions minutes/month** and 500 MB artifact storage for private repos |
| Razorpay **test mode** | No charge for test transactions | — |
| WhatsApp Cloud API **test number** | Meta provides a free test business number; test messages cost nothing | Up to 5 recipient numbers |
| Zoho Books **free plan** | Free for revenue under ₹25 lakh/year | No API access or e-invoicing on the free plan (see §3) |

**GitHub Actions minutes in practice.** GitHub rounds each job up to the minute. One CI run is about **5 billed
minutes**: backend 2, frontend 1, security 1, migrations 1. That's roughly **400 runs a month**, plenty for 2–3 developers.
The *Onboarding* workflow runs only when setup scripts change; its Windows job bills at 2× minutes. If you approach
the limit: Settings → Billing shows usage; fewer Dependabot PRs or GitHub Pro (3,000 minutes) solve it.

**The one optional purchase now: GitHub Pro** (personal account: protected branches + 3,000 Actions minutes; price on github.com/pricing, in the same range as GitHub Team at US$4/user/month). Only needed if you want **branch protection** on
the private repo (stopping direct pushes to `main`/`develop` and requiring green CI before merge). GitHub's docs:
protected branches on private repositories need Pro, Team or Enterprise. With 1–3 trusted developers you can skip it
and follow the rules in CONTRIBUTING.md; buy it when the team grows or before go-live.

## 2. Staging / online demo — ₹0 (optional)

Not needed until you want to test real webhooks (Razorpay from Sprint 11, WhatsApp from Sprint 24) or show the system
to someone outside your PC. Free options:

| Need | Free option | Limits |
|---|---|---|
| Web app (Angular static files) | **Cloudflare Pages** | 500 builds/month, unlimited static bandwidth, custom domains |
| PostgreSQL | **Neon** free plan | 1 GB storage/project, 100 compute-hours/project/month, sleeps after 5 min idle, 6-hour restore window |
| API (.NET) server | **Oracle Cloud Always Free** Arm VM | 2 OCPU + 12 GB RAM, 200 GB disk, in your home region. Needs a card to sign up; idle instances can be reclaimed, so fine for staging only |
| Webhooks to your own PC instead | **Cloudflare Tunnel** (`cloudflared`) | Free; exposes `localhost:5180` on a public URL while it runs |
| Transactional email | **Resend** free plan | 3,000 emails/month, 100/day, API included |

None of these are suitable for production data: no uptime guarantee, short restore windows, sleeping databases.

## 3. Production — what you pay when the resort opens (Sprint 29)

Everything below is **paid only from go-live**, and most of it is per-use.

### Fixed monthly (*estimate*)

| Item | Recommended starting tier | *Estimate* |
|---|---|---|
| API server | 1 small Linux VM (2 vCPU / 4 GB) in an Indian region (Azure Central India, AWS Mumbai, or a reputable Indian VPS) | ₹1,000–2,500 |
| Database | **Managed PostgreSQL with point-in-time recovery** (Neon *Launch* pay-as-you-go: US$0.106 per compute-hour + US$0.35/GB-month, no minimum; or Azure/AWS smallest tier) | ₹800–2,500 |
| File storage (ID scans, photos, PDFs) | Blob/S3 storage, a few GB | ₹50–200 |
| Cloudflare (DNS, TLS, WAF, web app hosting) | Free plan | ₹0 |
| Domain name (`.in` or `.com`) | Annual | ~₹70–100/month equivalent |
| Monitoring & alerts | Cloud provider's built-in + free uptime checker | ₹0 |
| GitHub Pro (branch protection) | — | see github.com/pricing (Team is US$4/user) |

The database must be a **managed** service with backups and point-in-time recovery: that's what protects
invoices and payments (see OPERATIONS.md). It isn't the place to save money.

### Per use

| Service | Charged | Rate (verify before go-live) |
|---|---|---|
| **Razorpay** | Per successful payment only. No setup fee, no annual fee | **2%** domestic cards/UPI/netbanking/wallets; **3%** international cards, Amex, corporate cards, EMI; **18% GST on the fee** (2% → 2.36%) |
| **WhatsApp Business Platform** | Per message sent, by category | India: utility ≈ **₹0.115**, marketing ≈ **₹0.86**, service messages 1,000 free/month per number then ≈ ₹0.115; + 18% GST. Check Meta's current rate card; Meta changed the rules in July 2025 and again in October 2026 |
| Transactional email | Per email over the free tier | Resend free 3,000/month covers a 7-unit resort |

**Example month** (*estimate*): 120 bookings paid online at ₹6,000 average = ₹7.2 lakh → Razorpay ≈ ₹17,000 incl. GST.
WhatsApp ≈ 600 utility messages ≈ ₹80. Payment fees are by far the biggest cost; cash/UPI-at-desk bookings have none.

### Subscriptions you'll choose later

| Service | Needed for | When | Notes |
|---|---|---|---|
| **Zoho Books** | Accounting sync (Sprint 25) | Before Sprint 25 | Free plan has **no API access and no e-invoicing**, so the integration needs a paid plan. India annual pricing: Standard ₹749/mo, Professional ₹1,249/mo (+18% GST). The accountant decides which; you may already use Zoho |
| **Channel manager** (OTA sync) | Booking.com / MakeMyTrip / Airbnb (Sprint 26) | Before Sprint 26 | Indian providers (AxisRooms, STAAH, eZee Centrix, Djubo…) price by quote; budget roughly under ₹5,000/month for a small property. Ask each for **API access for a third-party PMS** before choosing |
| Payroll provider | Payroll export (Sprint 19) | Before Sprint 19 | Often already used by the business; we only export a file |
| Email provider paid tier | Only above free limits | Unlikely at 7 units | |

### When to upgrade (triggers, not dates)

| Trigger | Action |
|---|---|
| More than 5 developers | C# Dev Kit needs Visual Studio Professional licences for all of them |
| Team grows / first outside contractor | GitHub Pro or Team for branch protection |
| CI minutes > 80% of 2,000 | GitHub Pro (3,000) or reduce Dependabot frequency |
| First real guest payment | Razorpay KYC complete, live keys in production secret store |
| Production API CPU > 70% for days, or many properties | Bigger VM or second instance (not before) |
| Database > 80% of plan storage | Next storage step |

## Sources

- GitHub plans (Pro: protected branches, 3,000 minutes): https://docs.github.com/en/get-started/learning-about-github/githubs-plans · prices: https://github.com/pricing
- GitHub Actions billing (2,000 minutes, 500 MB for Free): https://docs.github.com/en/billing/concepts/product-billing/github-actions
- Protected branches availability: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches
- Docker Desktop licence: https://docs.docker.com/subscription/desktop-license/
- C# Dev Kit licence FAQ: https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq
- Neon pricing: https://neon.com/pricing
- Oracle Cloud Always Free: https://docs.oracle.com/en-us/iaas/Content/FreeTier/resourceref.htm
- Cloudflare Pages free tier (summary): https://temps.sh/blog/cloudflare-pages-free-tier-limits-2026
- Resend pricing: https://resend.com/pricing
- Razorpay fees: https://razorpay.com/blog/razorpay-payment-gateway-pricing-explained/
- WhatsApp pricing model: https://developers.facebook.com/docs/whatsapp/pricing · test number: https://developers.facebook.com/docs/whatsapp/cloud-api/get-started · India rates (third-party summary, verify on Meta's rate card): https://whautomate.com/whatsapp-business-api-pricing-india
- Zoho Books India pricing (third-party summary, verify on zoho.com/in/books/pricing): https://www.patronaccounting.com/blog/zoho-books-pricing-india-2026
- Channel managers in India: https://blog.axisrooms.com/best-channel-manager-india/
