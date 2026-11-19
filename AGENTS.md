## Agent skills

### Testing and delegation

- Use the credentials provided in `Testing-Credentials.txt` for testing, and verify UI changes directly with Playwright. Keep credentials out of source, logs, screenshots, and reports.
- Use as many sub-agents as needed to complete the task, leveraging cheaper models where possible.

### Issue tracker

Issues and specs are tracked as local Markdown files under `.scratch/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, and `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repo; domain terms and decisions live in root `GLOSSARY.md` and `docs/adr/`. See `docs/agents/domain.md`.