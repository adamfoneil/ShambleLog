# Repository instructions

## Tests and package publishing

- Do not write tests coupled to UI components or their implementation, including rendered HTML, CSS classes, render trees, UI events, or private component state.
- Test behavior through non-UI APIs and services instead. Keep tests used by NuGet publishing independent of the UI project, browsers, and interactive sessions, and make them reliable on the Linux GitHub Actions runner as well as locally.
- Do not skip, disable, or weaken failing tests merely to make a package publish. Investigate failures and preserve meaningful behavioral coverage.
