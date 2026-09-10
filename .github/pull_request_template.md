<!-- Release PR (dev -> main) or hotfix PR (-> main). This description is the change log and becomes the release notes. -->

## What changed and why

<!-- One entry per change: what a consumer will notice, and why it was done. Group under Library / Tools / Documentation as needed. -->

## Version

<!-- Release PR: the last commit drops "-dev" from <Version> in Directory.Build.props — minor when the public API grew, patch otherwise. Hotfix PR: patch. -->

## Checks

- [ ] `dotnet build` in the default configuration reports 0 warnings
- [ ] `main` is an ancestor of this branch (the release-pr-guard job verifies it)
- [ ] the tag will be created on the merge commit, and `main` merged back into `dev` afterwards
