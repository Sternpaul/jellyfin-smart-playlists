# Contributing — Release Workflow

## The Rule: Tag-Only Releases

```bash
# The ONLY way to release:
git tag v1.5.33
git push origin v1.5.33
```

**Do NOT `git push origin main` at the same time as a tag push.**

The GitHub Actions workflow (`release.yml`) exclusively owns `repo/manifest.json`. On each tag push it:
1. Builds the DLL and zips it
2. Creates a GitHub Release with the zip
3. Checks out `main`, edits `manifest.json` (appends the new version entry), and pushes `main`

If you also push `main` concurrently, the workflow's `git push origin main` can fail as a non-fast-forward, and the manifest never gets updated.

## When It's OK to Push Main

Only to **consolidate source code** that was shipped via tags but never landed on `main` (e.g., if multiple tag-only releases left `main` stale). Rules:

1. **Never edit `repo/manifest.json` yourself.** The workflow owns it.
2. **Do it before tagging**, not concurrently. Push main first, wait for it to land, then tag and push the tag.
3. **Always `git pull --rebase origin main` first** to avoid conflicts with prior workflow commits.

## File Ownership

| Path | Owner | Notes |
|---|---|---|
| `repo/manifest.json` | `release.yml` workflow only | Never edit manually. The workflow appends version entries. |
| Everything else | Developer / agent | Push to main or via tags as needed. |

## Quick Reference

```bash
# Normal release (tag-only):
git tag v1.5.33 && git push origin v1.5.33

# If main is stale and needs source consolidation:
git pull --rebase origin main
git push origin main          # source only, never manifest.json
# wait for push to complete
git tag v1.5.33
git push origin v1.5.33
```
