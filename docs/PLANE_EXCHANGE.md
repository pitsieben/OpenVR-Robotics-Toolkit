# Plane Exchange (France ⇄ Austria)

The **Vive Plane Exchange** component (`Vive > TCP`) uploads recorded planes as JSON to a GitHub branch and downloads the partner's planes with one click.

```
planes/
  france/   latest.json, 2026-11-23_calib-A.json, ...
  austria/  latest.json, ...
```

Each file stores site, name, UTC time, Rhino units and the planes (origin, X axis, Y axis). On pull, coordinates are scaled to the current document's units.

## Wiring

`Vive Calibration Recorder.Planes` → `Planes`. Add Buttons to `Push`, `Pull` and `List`. Set `Site` to your own site and `Remote Site` to the partner's. `Pulled Planes` can go straight into your 3-point calibration / Orient.

Push writes `<Site>/<Name>.json` **and** `<Site>/latest.json`. If `Name` is empty, a timestamp is used. Pull reads `<Remote Site>/<Pull Name>.json` (by default `latest`).

## 1. Protect `main` (do this once, as repo owner)

Repo → **Settings → Rules → Rulesets → New branch ruleset**:

- Name `protect-main`, Enforcement **Active**, Target branches → **Include default branch**.
- Tick **Restrict deletions**, **Block force pushes**, **Require a pull request before merging**:
  - Required approvals **1**, tick **Require review from Code Owners** (the repo's `.github/CODEOWNERS` names `@pitsieben`), tick **Dismiss stale approvals when new commits are pushed**.
- **Bypass list**: add *Repository admin* and set it to **For pull requests only**. This lets you merge your own PRs, while a direct push, even one made with a token you created, is rejected.

Add a second ruleset `protect-plane-data` targeting `plane-data` with only **Restrict deletions** and **Block force pushes**. The sites can still add and update plane files, but nobody can wipe the history.

## 2. Generate a token per site

GitHub → avatar → **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**:

| Field | Value |
| :--- | :--- |
| Token name | `plane-exchange-france` (one token per site, so you can revoke one site alone) |
| Expiration | Custom: shortly after the workshop |
| Resource owner | your account (or the org that owns the repo) |
| Repository access | **Only select repositories**, the data repo(s) only |
| Repository permissions | **Contents: Read and write**. *Metadata: Read* is added automatically. **Leave everything else at No access.** |

Without the *Administration* permission, a token can't change or remove the rulesets. Copy the token straight away; GitHub shows it only once. Send it to the partner over a private channel (Signal, or a password manager share), not by email or in the repo.

**Limiting write further.** A token's write access always covers the whole repository; GitHub can't limit it to a folder. With the rules above, the worst a leaked token can do is add or edit files on `plane-data`. Every change stays in the history, so it can be restored. To stop the two sites from writing into each other's folder, use **one data repo per site**: the France token writes only to `vive-planes-france` and reads `vive-planes-austria`, and Austria's token is the mirror image. In Grasshopper, set `Repo` to your own repo and `Pull Repo` to the partner's.

## 3. Store the token on each PC (hidden and encrypted)

Close Rhino, right-click **`set_exchange_token.ps1`** ▸ *Run with PowerShell*, then paste the token. The input is hidden. The token is saved to

`%APPDATA%\Grasshopper\Libraries\ViveExchange\token.dat`

The file sits next to the installed plugin, is hidden, and is encrypted with Windows DPAPI. Only the same Windows user on the same PC can decrypt it, so a copied file is useless elsewhere. No plain-text copy is ever written. `.gitignore` excludes `ViveExchange/`, `token.txt` and `token.dat`, so the token can't be committed by accident. To revoke it, delete the file and delete the token on GitHub.

Leave the component's `Token` input **empty**: anything typed there is saved inside the .gh file. If a plain `token.txt` is dropped into that folder, the component encrypts it on first use and deletes the plain file. The environment variable `VIVE_EXCHANGE_TOKEN` also works, but it isn't encrypted.

Pulling from a public repo works without a token.

## Status messages

- `401`: the token is wrong or expired.
- `403`: the token lacks Contents write permission, or the branch is protected.
- `404`: the repo, branch or file doesn't exist, or the token can't see the repo. Use **List** to see which sets exist.
