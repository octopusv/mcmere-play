# Contributing

Read [README.md](README.md), [docs/DESIGN.md](docs/DESIGN.md), and [docs/RELEASING.md](docs/RELEASING.md) before making changes.

This repository is under active development. Run `scripts/Build.ps1` to verify the implemented foundation. The participant application and release installer are not yet available; do not describe planned functionality as working software.

- Keep server management and the distribution gateway in the mcmere repository. Integrate through versioned HTTP contracts, not direct database access or sibling-project references.
- Treat the entered Minecraft name as a distribution eligibility hint, not verified identity. Do not add a required Google or email login.
- Let Prism own Microsoft sign-in and credentials. Do not read, copy, publish, or log Prism account files.
- Use isolated data in `.test-data/` for verification. Existing Minecraft worlds, server files, accounts, and credentials are not fixtures.
- Preserve user settings and saves. Verify downloads and backups before replacing files or pruning old generations; cover interruption and recovery paths.
- Match the mcmere design system while keeping the participant UI focused on preparation and joining.
- Publish only original project material and approved third-party assets. Preserve applicable copyright and license notices.
- Document the relevant validation and remaining limitations in each change. A successful process launch is not proof of a successful Minecraft connection.
