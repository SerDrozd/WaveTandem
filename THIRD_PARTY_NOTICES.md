# Third-party notices

- WaveTandem uses the Windows Core Audio, COM and Multimedia Class Scheduler APIs from the Windows SDK. They are supplied by Windows.
- The self-contained Windows package includes the Microsoft .NET and Windows Desktop runtimes. See the runtime license and notice files bundled in the archive for their terms.
- The managed tests use [xUnit.net v3](https://github.com/xunit/xunit), licensed under Apache-2.0. The test runner and its dependencies are used only at build time. Their pinned versions and integrity hashes appear in the test project's lock file.
- The native build uses the Microsoft C++ runtime supplied with the Visual Studio Build Tools. This runtime is linked statically.
