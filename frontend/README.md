# Resort OS — staff web app

Angular 20. See the root [README](../README.md) and [docs/SETUP.md](../docs/SETUP.md).

```bash
npm ci
npm start          # http://localhost:4300, proxies /api → http://localhost:5180 (proxy.conf.json)
npm run test:ci    # unit tests, headless Chrome
npx ng lint
npm run build:prod
```

Structure, routes and UI conventions: [docs/architecture/frontend.md](../docs/architecture/frontend.md).
