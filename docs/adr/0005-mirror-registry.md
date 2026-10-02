# ADR 0005 — Mirror approval is per observed response

Date: 2026-10-02

`config/mirrors.json` records LICENSE fetches at 2026-10-02T19:42:10Z for commit `20c38289c29e4dba6b8f01ddd3273ec9ec169b46`.

Approved only where the host returned `200` and `text/plain`: `raw.githubusercontent.com`, `gitlab.com`, `codeberg.org`, `gitea.com`, `git.sr.ht`.

Rejected: `bitbucket.org` returned 404 HTML. `raw.githack.com` returned 301 HTML to GitHub raw. Yandex translate was not requested in this pass and stays unapproved.

The running fetcher does not load this file yet. An off-registry redirect is a failure (`OFF_REGISTRY_REDIRECT`), not a reason to add the host.
