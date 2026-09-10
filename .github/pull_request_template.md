<!-- Release PR (dev -> main) or hotfix PR (-> main). This description is the change log: publish.yml copies it into the release notes when it creates the release (a release created by hand beforehand keeps its own notes). -->

## What changed and why

<!-- One entry per change: what a consumer will notice, and why it was done. Group under Library / Tools / Documentation as needed. -->

## Version

<!-- Release PR: the last commit drops "-dev" from <Version> in Directory.Build.props — minor when the public API grew, patch otherwise. Hotfix PR: patch. Documentation-only changes do not get a release; they ride the next one. -->

## Checks

- [ ] `dotnet build` in the default configuration reports 0 warnings
- [ ] `main` is an ancestor of this branch (the release-pr-guard job verifies it)
- [ ] the tag will be created on the merge commit, `main` merged back into `dev` afterwards, and `dev` bumped to the next `-dev` version
