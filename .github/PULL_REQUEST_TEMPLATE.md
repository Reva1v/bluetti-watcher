## What and why

<!-- What this change does and the problem it solves. Link the issue if there is one. -->

## How it was tested

<!-- The commands you ran (see CONTRIBUTING.md) and their result; anything you could not test, e.g. on a real station. -->

## Checklist

- [ ] `dotnet test bluetti-watcher.slnx -c Release` and the node and pwsh tests pass
- [ ] The station stays read only: no write or control path to the station or the cloud
- [ ] Behaviour changes are in `docs/design.md` (and `docs/configuration.md` or `docs/api.md` for keys and fields)
- [ ] A change to the quick start or the reaction table updates both `README.md` and `README.uk.md`
- [ ] A user-visible change has a line under `## [Unreleased]` in `CHANGELOG.md`
