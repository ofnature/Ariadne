# Releasing Ariadne

Checklist for cutting an Ariadne release. Written 2026-09-19, cutting **v0.1.0** — the first release —
so the extra steps are marked **[FIRST]**. Everything else is the repeat path.

## State when this was written

| Thing | Value |
| --- | --- |
| `Ariadne/Ariadne.csproj` `<Version>` | 0.1.0 |
| `AriadnePlugin.PluginVersion` | derived from the assembly version — nothing to edit |
| `repo.json` (this repo) `AssemblyVersion` | 0.1.0.0 |
| GitHub releases / tags | v0.1.0 |
| Entry in `D:\Dev\Olympus\repo.json` | Ariadne at v0.1.0 |
| Icon at `images/icon.png` on raw | resolves, HTTP 200 |

## The two manifests — which one actually matters

There are two `repo.json` files and they are not equals:

- **`D:\Dev\Olympus\repo.json` is the one users install from.** It is the shared Dalamud third-party
  listing for Daedalus, Charon, SealBreaker, Theseus, Odysseus, Caduceus, Argus and Ariadne, served
  from `https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json`.
- **`D:\Dev\Ariadne\repo.json` is a mirror.** Keeping it correct is good hygiene, but editing it
  alone changes nothing for anyone.

> **Never add or bump the Olympus entry before the GitHub release exists.** That file is live for the
> whole fleet, and an entry whose download 404s offers every user a plugin that fails to install.
> The release comes first (step 5), the listing second (step 6).

## Steps

### 1. Clean tree

```bash
cd D:/Dev/Ariadne && git status --short
```

Commit or stash anything outstanding — the release must be built from what gets tagged.

### 2. Bump the version in one place

Patch bump per release (0.1.0 → 0.1.1 → …):

- `Ariadne/Ariadne.csproj` → `<Version>`

That is the whole list. `AriadnePlugin.PluginVersion` reads the assembly version
(`Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)`), so the `/ariadne` load line and
the window follow automatically, and DalamudPackager injects `AssemblyVersion` into the packaged
`Ariadne.json` from the same value — the shipped manifest cannot disagree with the csproj. When
`repo.json` exists, its `AssemblyVersion` **and all three `DownloadLink*` tag URLs** need the same
bump (4 edits in that file); a mismatch makes Dalamud either miss the update or reinstall in a loop.

### 3. Build and test

```bash
dotnet test Ariadne.Tests/Ariadne.Tests.csproj   # expect 0 failed
dotnet build Ariadne/Ariadne.csproj -c Release   # expect 0 errors
```

Ariadne has no `#if DEBUG`-only code today (unlike Argus, whose release must not ship its rating
learner). If that ever changes, step 4 grows a check on the shipped DLL — `grep -ac "<TypeName>"
Ariadne.dll` must be 0.

### 4. Verify the package, do not trust the build

DalamudPackager emits `Ariadne/bin/Release/Ariadne/latest.zip`. Check the artifact itself:

```bash
python -c "import zipfile;z=zipfile.ZipFile(r'D:/Dev/Ariadne/Ariadne/bin/Release/Ariadne/latest.zip');print(z.namelist());print(z.read('Ariadne.json').decode())"
```

Expect exactly three entries, **flat at the archive root** — `Ariadne.dll`, `Ariadne.deps.json`,
`Ariadne.json` — and an `AssemblyVersion` matching the bump. Dalamud does not find files nested in a
folder.

### 5. Tag and publish

Use the `github-release` skill (it handles the credential, tag push, release creation and asset
upload). Run from **Git Bash, never PowerShell**.

```bash
bash ~/.claude/skills/github-release/scripts/publish_release.sh \
  --tag v0.1.0 \
  --notes-file /path/to/notes.md \
  --asset D:/Dev/Ariadne/Ariadne/bin/Release/Ariadne/latest.zip \
  --repo ofnature/Ariadne
```

Push `main` first, or the tag lands on a commit GitHub does not have. Add `--dry-run` once to see
the plan without creating anything. The asset **must** be named `latest.zip` — that is what the
`DownloadLink*` URLs point at.

### 6. Add Ariadne to the Olympus manifest — the step that actually ships it

Only now, with the release live. Edit `D:\Dev\Olympus\repo.json` and append the Ariadne object (copy
it from this repo's `repo.json`, which is already in the right shape), then:

```bash
cd D:/Dev/Olympus && git add repo.json && git commit -m "chore(repo): add Ariadne at v0.1.0" && git push
```

Olympus uses conventional-commit style for these (`chore(repo): point X at vN`), unlike the plugin
repos.

On later releases this step is just the same 4 edits as step 2 — version plus the three URLs. Do not
edit only this repo's mirror and assume it shipped.

### 7. Verify from the remote, not from disk

```bash
curl -s https://api.github.com/repos/ofnature/Ariadne/releases/latest | grep -E '"tag_name"|browser_download_url'
curl -s https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json | grep -A2 '"Ariadne"'
```

**Expect the raw URL to lag.** `raw.githubusercontent.com` caches for a few minutes and will serve
the old manifest right after the push — this has happened on every Daedalus release. To prove the
push itself was correct without waiting, read the file through the API, which uses a different cache:

```bash
curl -s "https://api.github.com/repos/ofnature/Daedalus/contents/repo.json?ref=main" \
  | python3 -c "import json,sys,base64;print(base64.b64decode(json.load(sys.stdin)['content']).decode())"
```

Until raw catches up, Dalamud and any in-plugin update checker still report the old version, so don't
tell anyone to update before it flips.

## First-release extras

- **[FIRST]** `DalamudApiLevel` is 15 in both `Ariadne/Ariadne.json` and `repo.json`, matching the SDK
  this repo pins (`Dalamud.NET.Sdk/15.0.0`). If Dalamud's API level moves, both files need it, or the
  plugin is hidden from the installer rather than erroring.
- **[FIRST]** `IconUrl` points at `images/icon.png` on `main`. It must stay on `main` and stay a real
  PNG, or the listing shows a broken image.
- **[FIRST]** `IsTestingExclusive` is `false`, so the release goes to everyone the moment the manifest
  entry lands.
- **[FIRST]** There is no CHANGELOG in this repo: write the release notes into a file and pass
  `--notes-file`.

## Gotchas carried over from Argus and Daedalus releases

- Notes go in a **file**, never inline — long notes break the API payload through shell arguments.
- The packaged zip must have files at the archive **root**, not nested in a folder. DalamudPackager
  does this correctly; re-zipping by hand usually does not.
- `Ariadne.csproj` pins `<OutputPath>` to `bin\$(Configuration)\` because the solution declares x64
  and a solution build would otherwise land in `bin/x64/…`, where Dalamud is not looking. The
  artifact is under `Ariadne/`, not the repo root's `bin/`.
- Argus and Charon carry a `TouchAssemblyAfterPackaging` target (hooked to the packager targets, not
  `Build`) so a dev-plugin reload never reads a half-written manifest. Ariadne does not have it yet.
