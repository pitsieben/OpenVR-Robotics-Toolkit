# Plane Exchange (France ⇄ Austria / Linz)

The **Vive Plane Exchange** component (`Vive ▸ Utilities`) sends recorded planes from one site to the other through a GitHub branch. Each site **pushes** into its own folder and **pulls** from the partner's folder. No git install is needed, just internet access and a token stored on the PC.

```
GitHub repo pitsieben/OpenVR-Robotics-Toolkit, branch "plane-data"
planes/
  france/    latest.json, run-03.json, 2026-11-24_10-15-02.json, ...
  austria/   latest.json, ...
```

---

## Quick start for the partner site (Linz / Austria)

You need: Rhino 8 on Windows, internet access, the release ZIP (or the repo), and an **invite link + passphrase** from the France team (sent over two different channels).

### 1. Install (once)
1. Right-click the ZIP ▸ **Properties** ▸ **Unblock**, then extract it.
2. Close Rhino and double-click **`copy_gha_to_grasshopper.bat`**.

### 2. Store the token (once per PC and Windows user)
1. Run **`ViveExchangeSetup\publish-win-x64\ViveExchangeSetup.exe`** once.
2. Click the invite link you received. It opens the app (or paste it into the **Accept invite** tab).
3. Type the passphrase ▸ **Accept**. The app checks the token with GitHub and saves it encrypted on this PC. You never type the token into Grasshopper.

### 3. Place the component
1. In Grasshopper, place **Vive Plane Exchange** (`Vive ▸ Utilities`).
2. A **Site** drop-down appears next to it. Set it to **Austria**. You will then push to `austria/` and pull from `france/`.
3. Wire Grasshopper **Buttons** into `Push` and `Pull`.

### 4. Receive planes from France
1. Leave `Pull Name` at `latest` (or type a set name, e.g. `run-03`).
2. Press **Pull**. `Pulled Planes` gives the planes **in the same data-tree branches France pushed** (e.g. one branch per take), already scaled to your document units.
3. `Info` shows the set name, when it was recorded, its units and any note.
4. Not sure which names exist? Zoom in on the component, click **+** until the `List` input appears, wire a Button and press it. `Available` lists France's sets.

### 5. Send planes to France
1. Wire your planes (a list or a tree) into `Planes`. Branches are kept.
2. Type a `Push Name` (e.g. `linz-run-01`). Empty uses a timestamp.
3. Press **Push**. The set is saved as `austria/<name>.json` **and** as `austria/latest.json`, so France gets it with a plain Pull.
4. Optional: add the `Note` input with **+** to send a short description along.

### 6. Check it worked
`Status` reports every action, e.g. `Pushed 154 planes to …:planes/austria/linz-run-01.json`. Push and pull run in the background, so the canvas never freezes. If something fails, see **Status messages** at the end of this page.

### Good practice between the sites
- Use clear, unique **Push Names** (`linz-run-01`, `linz-run-02` …). `latest` is overwritten by every push; named sets stay.
- Agree on units: both documents in **millimetres** is simplest (other units are converted automatically).
- Agree on the **reference frame**: planes are sent in world coordinates of the sender's document. Both sites should place their brick / work surface at the same world position, or re-orient the pulled planes (e.g. with Orient) from the sender's brick plane to yours.
- Nothing is ever deleted: every push stays in the branch history and can be restored.

---

## Component reference

- **Visible inputs:** `Planes` (tree), `Site` (France / Austria), `Push`, `Pull` (buttons), `Push Name`, `Pull Name` (default `latest`).
- **Hidden inputs** (zoom in, click **+** / **-**): `Repo` (default `pitsieben/OpenVR-Robotics-Toolkit`), `Pull Repo` (when the partner uses its own data repo), `Branch` (default `plane-data`), `Token` (**leave empty**: anything typed here is saved inside the .gh file), `List` (button), `Note`.
- **Outputs:** `Pulled Planes` (tree), `Info`, `Available`, `Status`.
- **File format:** JSON with site, name, UTC time, Rhino units, note and per plane its origin, X axis, Y axis and Grasshopper branch path. Sets pushed by older plugin versions have no branch paths and come back in branch `{0}`.

---

# Owner setup (France, once)

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

## 3. Easiest: the token and invite app

Run **`ViveExchangeSetup\publish-win-x64\ViveExchangeSetup.exe`**. It has three tabs:

- **My token:** paste your token and click *Check & save*. The app asks GitHub whether the token can read and write the repo, then saves it encrypted on this PC (same place and format as below).
- **Create invite:** makes an encrypted `viveexchange://invite/...` link for a partner, now or later. Leave the token box empty to share the token saved on this PC. A passphrase is generated for you. **Send the link and the passphrase over two different channels** (for example, the link by email and the passphrase by phone or Signal). The link alone is useless.
- **Accept invite:** clicking the link opens the app, if it has been run once on that PC. Otherwise paste the link, type the passphrase and accept. The app checks the token with GitHub, saves it encrypted, and tells you which Site to pick in Grasshopper.

The link carries the token, repo, branch and site names. It's encrypted with AES-256-GCM, with a key derived from the passphrase (PBKDF2-SHA256, 300,000 rounds). A wrong passphrase or an edited link is rejected. An invite can't be revoked; to cut someone off, delete that token on GitHub. One token per site means you only cut off that site.

## 3b. Store the token by script (alternative)

Close Rhino, right-click **`set_exchange_token.ps1`** ▸ *Run with PowerShell*, then paste the token. The input is hidden. The token is saved to

`%APPDATA%\Grasshopper\Libraries\ViveExchange\token.dat`

The file sits next to the installed plugin, is hidden, and is encrypted with Windows DPAPI. Only the same Windows user on the same PC can decrypt it, so a copied file is useless elsewhere. No plain-text copy is ever written. `.gitignore` excludes `ViveExchange/`, `token.txt` and `token.dat`, so the token can't be committed by accident. To revoke it, delete the file and delete the token on GitHub.

Leave the component's `Token` input **empty**: anything typed there is saved inside the .gh file. If a plain `token.txt` is dropped into that folder, the component encrypts it on first use and deletes the plain file. The environment variable `VIVE_EXCHANGE_TOKEN` also works, but it isn't encrypted.

Pulling from a public repo works without a token.

## Status messages

- `401`: the token is wrong or expired.
- `403`: the token lacks Contents write permission, or the branch is protected.
- `404`: the repo, branch or file doesn't exist, or the token can't see the repo. Use **List** to see which sets exist.
