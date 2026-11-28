# Repository instructions

## Tests and package publishing

- Do not write tests coupled to UI components or their implementation, including rendered HTML, CSS classes, render trees, UI events, or private component state.
- Test behavior through non-UI APIs and services instead. Keep tests used by NuGet publishing independent of the UI project, browsers, and interactive sessions, and make them reliable on the Linux GitHub Actions runner as well as locally.
- Core publishing requires the tests to pass before the new Core package exists. Tests of the current Core code must use the source project, not require the unpublished package. Check transitive project and package references for older Core binaries compiled against incompatible APIs; do not make Core publication depend on updating downstream packages first.
- When changing dependencies, preserve the intended package dependency metadata and run the publishing workflow's Release test command against the current sources.
- Do not skip, disable, or weaken failing tests merely to make a package publish. Investigate failures and preserve meaningful behavioral coverage.
