# Handbooks

Two Word documents, rebuilt from the repository at the end of every accepted sprint.

| File | For | Contents |
|---|---|---|
| [Resort-OS-Product-Overview.docx](Resort-OS-Product-Overview.docx) | Clients, partners, investors | What Resort OS does, area by area; a guest's journey; roles; built for India; security; technology in brief; what is available today with screenshots; roadmap; how we deliver; running costs; glossary |
| [Resort-OS-Software-Documentation.docx](Resort-OS-Software-Documentation.docx) | Developers, reviewers, the owner | Technology stack with versions; architecture, layers, request pipeline, module map; security; data and API design; frontend; engineering process and CI; **sprint record** (UI, API and database design, code changed, tests and results for every accepted sprint, plan of the current one); quality report; roadmap; roles and permissions |

The Product Overview may be shared with clients: it contains no passwords, keys, personal data or repository details.
The Software Documentation is for the team and technical reviewers under confidentiality.

**Don't edit the .docx files by hand**: the next build overwrites them. Change the source instead:
sprint content in `docs/sprints/sprints.yml`, wording in `tools/handbooks/content/`, diagrams in
`tools/handbooks/diagrams/`. How to rebuild: [tools/handbooks/README.md](../../tools/handbooks/README.md).
