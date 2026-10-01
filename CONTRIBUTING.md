# Contributing

Thanks for looking. This is a personal tool shared as is, so the bar is: keep
it working for its one real use, and keep it honest about its numbers.

- **Bugs and questions:** open an issue. Please leave out your own usage data,
  app lists and device names; describe the shape of the problem instead.
- **Small fixes:** a pull request is welcome.
- **Anything larger:** open an issue first, so we can agree it fits before you
  spend the time.
- **Security issues:** not in a public issue; see [SECURITY.md](SECURITY.md).

## Before sending a change

```powershell
dotnet build ScreenTimeNative.slnx
dotnet test --project tests\ScreenTime.Core.Tests\ScreenTime.Core.Tests.csproj
```

The tests need no admin rights and no real data; they run the real sampler
for a couple of seconds into a throwaway folder.

## Conventions

- **Conventional commit messages** (`feat:`, `fix:`, `docs:`), one concern per
  commit.
- **Never capture window titles.** A title carries the document, the page,
  the person. The sampler records which app and for how long, and nothing more.
- **Keep every `.ps1` pure ASCII.** Windows PowerShell 5.1 misreads a UTF-8
  dash in a BOM-less script, and silently changes its logic.
- **Never commit collected data.** `.gitignore` blocks databases, CSVs,
  `demo-data/` and the `screenshots/` folder.
- **Screenshots come from the demo data only.** Make it with
  `dotnet run --project src\ScreenTime.Cli -- demo-data demo-data` and capture
  with `tools\Capture-Views.ps1`, rather than photographing your own history.
- **Nothing runs elevated.** If a change seems to need Administrator, it is a
  design change: open an issue first.
- **No new dependencies without a reason.** The app is meant to build years
  from now from what is on disk.
