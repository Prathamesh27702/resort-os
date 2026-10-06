# Security policy

This is a private repository for a system that handles guest personal data and payments.

## Reporting a vulnerability

Do **not** open an issue, PR or discussion. Contact the project owner directly (Prathamesh, via the private channel
you were onboarded with) with:

- what you found and where (file/endpoint),
- how to reproduce it,
- the impact you think it has.

You'll get an acknowledgement within 2 working days. Fixes for critical issues go out as a hotfix.

## Leaked secret?

If a key, password or token was committed or shared by mistake, tell the owner straight away. The secret is rotated
first; history is cleaned afterwards.

Security architecture and controls: [docs/SECURITY.md](docs/SECURITY.md).
