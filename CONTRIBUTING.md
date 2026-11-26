# Contributing

Build and test with PowerShell 7, Visual Studio C++ Build Tools, the Windows SDK and .NET 10. Run `./scripts/check.ps1` followed by `./scripts/build.ps1`. See [testing](docs/testing.md) for audio hardware and stability procedures.

Open a focused pull request. Use an English Conventional Commit subject and describe the observable behavior, verification and any hardware that was unavailable. For routing changes, include endpoint types, Windows build, sample rates and before/after diagnostics. Use anonymized device names and keep audio samples out of issues.

Add translated strings to all three locale files and keep their format placeholders consistent. Keep native interop layout compatible or increase the ABI version. Preserve the bounded queues and worker shutdown contract.

For security issues, follow [SECURITY.md](SECURITY.md).
