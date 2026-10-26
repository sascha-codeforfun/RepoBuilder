# RepoBuilder

Turns a repo folder or zip into one self-contained .exe — no IDE, no network, and nothing to take on faith.

> Trust by checking, not by believing.

## Why this exists

A lot of the perceived safety of the past came from scarcity. Serious attack capability was rare, and where it existed it was expensive, so threat profiles were narrow and focused. The risk was never lower than it is today; most targets simply weren't worth the effort.

That assumption no longer holds. In an AI-driven world you have to assume bad actors have formidable capability, and that the cost of targeting even the most insignificant entity is so low that nothing is stopping them. Under that threat model, trust and custody of every artifact — from source code to final binary — matter more than ever.

A development machine connected to the internet is therefore a much bigger risk than it used to be. Yet the need to keep it connected has grown at the same time: online documentation, services to consume, modern communication.

One way out is to stop building where you develop. Code is written on a connected machine, and binaries are built on a separate, isolated one. What makes that split trustworthy rather than just inconvenient is **deterministic, reproducible builds** — and that is where RepoBuilder comes in.

The capability in that threat model isn't one-sided. The same AI that lowers the cost of attack also lowers the cost of defence: RepoBuilder itself was written with AI assistance, a tool that would once have been too much effort for one person to build and maintain. AI is not on either side. It is a capability, and it amplifies whoever uses it.

That is also why RepoBuilder doesn't care whether code was written by a person or a model. All code is treated the same way — as input to be built in custody and checked, never as something to trust because of where it came from.

## What that gives you

- **The tool is readable.** The whole app is a single C# file with no third-party dependencies, and you can read it top to bottom. About a quarter of it — the `HueTheme.Xaml` string, lines 452 to 1712 — is the shared dark theme, a resource dictionary that decides what the window looks like and nothing else. The remaining ~4,100 lines are the program: what it scans, what it runs, what it writes and where. Skip the theme and you have read everything that touches a build.
- **What it is and how it was built are recorded and checkable.** The header shows the version of the exe you are running and the exact .NET SDK and runtime that built it.
- **The toolchain runs in isolation.** RepoBuilder, a portable SDK and a package cache form a complete build environment that needs no network. Source goes in, a binary comes out, and nothing else has to cross.
- **Isolation can be audited.** Nothing about the build requires access to the machine from outside, so keeping it closed is a matter of policy you can verify, not a feature you have to trust.
- **Any binary can be compared.** Given the same SDK and runtime packs, a build made on your isolated machine can be compared byte for byte with a build of the same source made anywhere else.

The same zip built by RepoBuilder on two machines with the same SDK and package cache gives byte-identical executables.

## What this does and doesn't replace

Existing mechanisms ask you to trust someone: the certificate, the operating system, the toolchain vendor. This approach doesn't make trust disappear — the SDK and runtime packs are still Microsoft's, and their files carry Microsoft's signatures. What changes is that such trust is no longer your *only* safeguard.

You hold custody of everything that matters: the source you feed in, the binaries that come out, the tool that does the building, the SDK it uses, and, where the code needs them, the packages it compiles against. And any claim about a binary can be checked by rebuilding it independently and comparing the bytes.

The stamp in the header identifies which SDK built an exe; it does not prove it on its own. The rebuild is the proof.

## Verify a build yourself

The header identifies a build: the **version** says which source it is, and the **SDK and runtime** beneath it say what turned that source into the exe. The version lives in the csproj, so the same source always carries the same version.

1. Note the header of the exe in question: **built with SDK** and **runtime**.
2. On any Windows machine, obtain that SDK (the xcopy zip from Microsoft) and the matching runtime packs, `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64`.
3. Build the same source with the same options. The build log records the full `dotnet publish` command, so a plain command-line build can be used instead of RepoBuilder for a fully independent check.
4. Compare the two executables, for example with `Get-FileHash` or `fc /b`.

The same works for RepoBuilder itself: build its source with the SDK its header names, and compare the result with the exe you are running.

## Setting up an isolated build machine

Put these side by side:

```
RepoBuilder.exe
sdk\        unzipped .NET SDK, found before any installed one
packages\   NuGet package cache with the runtime packs
TOOLCHAIN\  the SDK zip sdk\ was unpacked from, and its .sha512
```

That is the complete toolchain. It can be carried on a stick, and nothing is written to the registry, `PATH` or `%AppData%`. RepoBuilder can prime the `packages` folder on a connected machine for you.

**Check SDK**, in the SDK card, proves the chain from Microsoft's download page to the folder builds run from. It computes the SHA-512 of `TOOLCHAIN\dotnet-sdk-<version>-win-<arch>.zip` — the version is the one in `sdk\` — and compares it with the `.sha512` beside the zip and with a hash pasted from the download page, whichever are present. Then it compares `sdk\` with the zip byte for byte: any file missing, different or extra fails the check, and the Output tab lists them all. When a pasted hash matches and no `.sha512` exists yet, the check offers to save one, in `sha512sum` format, so the isolated machine has its reference.

Two optional folders, picked up automatically on the lowest fixed drive that has them:

- `\REPO` — where finished executables go. Each successfully built zip is filed under `\REPO\HISTORY\<AssemblyName>\<AssemblyName>-0001.zip`, numbered in build order, so every binary keeps the exact source it came from.
- `\TEMP` — where build scratch lives. Everything RepoBuilder creates there is removed after the build.

Then drop a repo folder or a `.zip` onto the window, pick the project, and press **Build exe**.

## Rebuild everything at once

**Rebuild all** takes the newest zip from every project folder in `HISTORY` and builds it again with the current settings. That makes three things cheap enough to actually do: applying a new option — like **Reproducible** — to every project you have ever built, folding in a new SDK, and refreshing all your binaries without spending attention on each one.

- Each exe is built to temp and compared with the one already in **Save to**. Identical output leaves the file and its timestamp untouched; only exes that actually changed are replaced. After an unrelated change, a reproducible setup reports almost everything as *unchanged*, and the few *changed* lines are the ones worth looking at.
- In a multi-project zip, the project to build is the one whose `AssemblyName` matches its `HISTORY` folder, which is how it was filed in the first place.
- A failure never stops the batch. The old exe stays, the reason goes into the summary, and the next project starts. **Cancel** stops after the current project.
- RepoBuilder rebuilds itself last — and a single build of RepoBuilder's own zip uses the same swap described here, so building it with itself never needs a spare copy. Windows won't overwrite a running exe but does allow renaming one, so the running copy steps aside as `RepoBuilder-OLD.exe` — the fallback — and the new build takes the name `RepoBuilder.exe`. Running from the fallback instead, nothing is locked and `RepoBuilder.exe` is simply replaced.
- The summary also lists exes in **Save to** that have no `HISTORY` folder: the ones that cannot be regenerated.

Every run writes a record into **Save to**, named after the time and the folder, for example `2026-09-22-14-05-33-REPO.sha256`. It is in `sha256sum` format and lists every exe in the folder, followed by the source zip each rebuilt exe came from:

```
<sha256> *RepoBuilder.exe
<sha256> *TicTacToe.exe
…
<sha256> *HISTORY/RepoBuilder/RepoBuilder-<n>.zip
<sha256> *HISTORY/TicTacToe/TicTacToe-<n>.zip
```

A zip is listed only when its exe was built from it in that run, so every source line pairs with a binary that demonstrably came from it. Run `sha256sum -c` on the file inside the folder to verify the lot.

## Build it from source

Requires the .NET 10 SDK on Windows.

```
dotnet publish -c Release
```

The result lands in `bin\Release\net10.0-windows\win-x64\publish\RepoBuilder.exe`. Or point an existing RepoBuilder.exe at this folder and let it build itself; each generation shows in its header what built it.

## How it works

Drop a repo folder or a `.zip` anywhere on the window, or use the browse buttons. Zips are unpacked to temp and a single wrapper folder is collapsed automatically.

It scans for `.csproj` files (skipping `bin`, `obj`, `.git`, `node_modules`, `packages`), reads `OutputType`, and auto-selects when exactly one project is `Exe` or `WinExe`. With more than one, it selects the first and asks you to confirm.

Then it runs `dotnet publish` with self-contained, single-file and native-extract flags, plus whatever you picked for runtime ID, trimming, ReadyToRun, reproducibility, verbosity and extra MSBuild arguments. The exe is copied into the folder in **Save to**, and the source zip is filed into `HISTORY`.

## Design notes

- `dotnet.exe` is located via a portable `sdk` folder next to the exe, then
  `DOTNET_ROOT`, then the per-architecture registry key, then `Program Files`,
  then `PATH` — so a stale `PATH` entry won't win.
- **No SDK on the machine?** When none is found the log offers a download link,
  a search-query fallback, and an option to point at a
  `dotnet-sdk-*-win-x64.zip`. That zip is a plain xcopy layout, so it is
  unpacked into an `sdk` folder beside the exe and found automatically from then
  on. Nothing is written to the registry, `PATH`, or `%AppData%` — move the exe
  and its `sdk` folder together and it keeps working.
- **No network either?** The SDK zip ships targeting packs and the apphost, but
  not the runtime packs that self-contained publish needs, so restore fails with
  `NU1301`. A `packages` folder beside the exe fixes that — `NUGET_PACKAGES` is
  pointed at it automatically.
- **Priming is automated.** On a machine that has network, load the repo you
  intend to build offline, then use the *Prime an offline packages folder* link.
  It publishes that project with `NUGET_PACKAGES` aimed at `packages`, so
  everything the repo's own csproj asks for is fetched — including extras like
  crossgen2 when the project sets `PublishReadyToRun`, which a synthetic project
  would never pull. With no repo loaded it falls back to a throwaway WPF project,
  which covers the base runtime packs only.
- Priming is per repo and per runtime ID. A different repo, or a switch from
  win-x64 to win-arm64, may need packs the earlier run did not fetch. The log
  offers to prime again whenever restore fails on a source.
- Alternatively, point the app at a zip of a `packages` folder primed on another
  machine — the log offers that when it sees a restore-source failure.
- `RepoBuilder.exe` + `sdk\` + `packages\` is a complete offline toolchain that
  can be carried on a stick.
- **Where the scratch goes.** Like **Save to** and its `\REPO` lookup, the app
  prefers an existing `\TEMP` folder on a fixed drive, lowest drive letter first,
  and works inside `<drive>\TEMP\RepoBuilder`. If there is no such folder, or it
  cannot be written to, it falls back to `%TEMP%\RepoBuilder` and says so in the
  log along with how to change that. A short root also keeps publish paths well
  clear of the 260-character limit.
- **Temp hygiene.** Zip extractions, publish output and priming scratch all live
  under that root. Publish and priming folders are deleted as soon as they are
  finished with; an extraction goes as soon as the build it fed succeeds. A
  failed build keeps its extraction, since the error output points into it, and
  that is cleared when the window closes. Anything left behind by a crash is
  swept at startup and every thirty minutes after, once it is more than six hours
  old — loose files as well as folders. Nothing is written outside the temp root
  and the folder in **Save to**.
- **Build files stay out of the repo.** Publish runs with
  `-p:ArtifactsPath=<workdir>\artifacts`, so `obj\` and `bin\` land inside the
  per-run folder that gets deleted at the end instead of beside the `.csproj` —
  which for a folder build would be your own source tree. `ArtifactsPath` is
  used rather than `BaseIntermediateOutputPath` and `BaseOutputPath` because a command-line
  property reaches every project in the graph, and one shared `obj\` makes
  referenced projects overwrite each other's `project.assets.json`.
  - A repo that sets `OutputPath` itself wins over the redirect, so its build
    files still land in the repo. The log says so when it spots one; the build is
    unaffected either way.
  - If a repo trips over the redirect — a custom target copying into a hardcoded
    `bin\Release` path, say — the failure offers a one-click retry with build
    files back in the repo folder.
- **The source zip is filed automatically.** A successful build moves it to
  `<Save to>\HISTORY\<name>\<name>-0001.zip`, so Downloads is left clean and
  every exe has the source it came from kept beside its siblings. `<name>` is the
  project's `AssemblyName`, which is what the exe is called, rather than the
  `.csproj` file name — the two are not always the same. The number is read from
  the folder rather than the incoming file name — the highest one already there,
  plus one — so whatever the download was called it becomes the next in sequence
  and two builds can never collide. Any trailing number counts, so a folder
  seeded by hand carries on from what is there: `RepoBuilder-src-006.zip`
  continues at `RepoBuilder-0007.zip`. Only the zip is filed; the unpacked copy
  lives under the temp root and is deleted separately.
  - If the exe a build would replace is running, nothing is built: the unpacked
    copy is removed, the zip stays where it is, and the Status tab says to close
    the app and try again. In **Rebuild all** that project is marked failed and
    the batch goes on. RepoBuilder's own exe is exempt; the `-OLD` swap handles it.
  - The move happens only after the exe has reached **Save to**. A failed build
    leaves the zip where it was.
  - Pressing Build again after that reports that the zip has gone and says where
    it was filed, since there is nothing left to unpack until the next download.
  - Rebuilding a zip that is already in `HISTORY` does not file a second copy.
  - **The same delivery twice is flagged, not refused.** A loaded zip is compared
    with the newest zip of every project in `HISTORY` — size first, bytes only on
    a size match. If it is identical, the Status tab says it has already been
    built. Build exe still builds it, since other settings, another SDK or other
    packages can be the point; the zip is then not filed a second time and the
    download is removed, because the identical copy is already the latest entry.
    Older zips are not compared, and **Rebuild all** never checks.
- **Reproducible** (on by default) adds `-p:ContinuousIntegrationBuild=true`.
  Every build runs in a random temp folder, and a project that emits debug info or
  uses `[CallerFilePath]` would otherwise bake that path into the exe, so two
  builds of the same zip would differ purely because the folder name did. With it
  the source root is mapped to `/_/`. It does not need a `.git` folder. Projects
  with `DebugType none` and no `[CallerFilePath]` are reproducible with or
  without it; the flag is the safety net for the rest.
- **The version**, top right in the header, is the csproj's `<Version>`. A
  source-revision suffix is kept out of it, so a build from a git checkout and one
  from a zip show the same version.
- **Built with SDK**, beneath the version, is the exact .NET SDK that built
  this exe. The csproj stamps `$(NETCoreSdkVersion)` — defined by the SDK itself
  during the build — into an `AssemblyMetadata` attribute, and the header reads it
  back from the assembly. Nothing has to be passed in, so it is right whether the
  build came from RepoBuilder, a plain `dotnet publish`, a portable SDK or an
  installed one. Below it, the runtime version that SDK packed into the exe
  (`$(BundledNETCoreAppPackageVersion)`; the desktop pack shares the number).
  Together that is the rebuild recipe: fetch that SDK and those runtime packs,
  build the same source, and the exe should come out byte-identical. The same SDK
  always writes the same strings, so the stamp does not disturb reproducibility.
- **Trim is offered only where it works.** WPF and WinForms resolve types by name
  at runtime out of XAML, so the trimmer removes code that is in use; Microsoft
  does not support trimming desktop apps. Selecting such a project clears and
  disables the box with an explanation, and a batch skips trimming for desktop
  projects rather than letting one tick break them all.
- **A batch reports what the cache is actually for.** Each build's
  `project.assets.json` — written by restore and deleted moments later — lists
  every package it resolved, transitive ones included, plus the runtime and
  targeting packs. After a batch where every project built, the Output tab shows
  three groups with sizes: used by these projects, kept for another runtime ID or
  for Trim and ReadyToRun, and used by nothing here. The last group comes with an
  action that **moves** those folders to `packages-unused` beside the cache rather
  than deleting them, so one can be moved back if a later build wants it instead
  of being fetched over a network the machine does not have. Nothing is reported
  after a batch with failures: a project that did not build would make its own
  packages look unused.
  - **Each used package names who resolved it.** "all 25" is the toolchain and
    says nothing; a name or two points at a csproj worth looking at. That is what
    turns a number into something you can act on — a runtime pack for an
    architecture nobody builds for, held in the cache by one project's
    `<RuntimeIdentifiers>` line, is invisible as a total and obvious as a name.
  - The toolchains behind Trim and ReadyToRun are kept **only at the runtime
    version in the header stamp**. That is the only version this SDK will ask
    for; any other is reported as unused like everything else.
- **The Packages card opens the Packages tab** when clicked: every package id in
  the cache with the versions on disk, one row each, and a **Copy list** button
  that puts the lot on the clipboard as plain text — the folder, the count, then
  one aligned line per package. That is what to hand to anything that needs to
  know what this machine can build offline. The list is on its own tab rather
  than in the build log, so looking costs no log and works during a build.
- **Child-process scratch.** `dotnet` is given its own `TMP`/`TEMP` inside the
  temp root. NuGet and MSBuild make scratch folders (`NuGetScratch` and friends)
  wherever `%TEMP%` points and never remove them, so without this they
  accumulate in the user profile no matter where the app puts its own files.
- **Self-extraction.** `IncludeAllContentForSelfExtract` is deliberately not set.
  With it, every managed assembly would be unpacked to
  `%TEMP%\.net\RepoBuilder\<hash>\` at each start — a fresh copy for every build,
  since the folder is keyed on a hash of the bundle, and never cleaned up by the
  runtime. Without it only the native libraries extract. Extraction folders of
  other builds are swept at startup, each one renamed before deletion so the live
  folder fails the rename and is skipped whole rather than half-deleted.
- The NuGet vulnerability audit is disabled (`-p:NuGetAudit=false`), which stops
  the call to nuget.org that otherwise happens even when every package resolves
  from the local cache. Package resolution is unchanged and still local-first,
  so a genuinely missing package fails with its name in the log.
- Certificate revocation checking is set to offline (`NUGET_CERT_REVOCATION_MODE`).
  Verifying a package signature builds its certificate chain, which asks the
  issuing CA whether that certificate is revoked — an outbound call per package,
  made even for packages that came from the local cache. On a disconnected
  machine that is a firewall drop and a timeout each time, then `NU3028`. Offline
  mode uses the revocation list already on the machine. A certificate revoked
  after that list was written would go unnoticed, which is the honest cost; on a
  machine with no route out the online check could not have run regardless.
- Workload advertising manifests are not downloaded
  (`DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE`). The CLI fetches these in the
  background on build, publish, restore, run, pack and test — once per project in
  a batch. It is meant to be throttled to once a day, but a download that cannot
  complete is retried on the next command, which offline means every time.
- Network attempts are tried once rather than six times
  (`NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT`). A blocked outbound connection does not
  fail fast: the firewall drops the packet silently and the caller waits out
  Windows' retry sequence, roughly 21 seconds. Six of those per request is two
  minutes of nothing happening.
- **Save to** defaults to an existing `\REPO` on the lowest fixed drive that has
  one, falling back to `C:\REPO`.
- The **Zip…** dialog opens in Downloads.
- The full `dotnet publish` command line is printed at the top of every run, so
  you can always see exactly what is being executed.
- **A clock in the header** counts from the moment a build or a batch starts and
  keeps its last value afterwards, so the time a finished run took is still
  readable. With the "Rebuilding 13 of 22" line in the status strip it is enough
  to judge the pace of a batch without watching it.
- No third-party packages. `app.ico` is the exe icon and `app_256.png` is
  embedded for the header and the title bar.

## The look

RepoBuilder wears **HueTheme**, the shared dark theme of the icon-family app
suite, in the **Cyan** family that marks dev and build tools — neutral surfaces,
glow 0.35. Everything visible comes from the theme: the surfaces, the status
colours, the button roles, the pills, the callouts, the marker dot in the status
strip and the halo behind the app mark. Nothing in the code builds a colour.

The theme lives in this file, as a `ResourceDictionary` parsed from a string at
startup and merged into the application's resources. That is the one place
RepoBuilder departs from the kit, which ships `HueTheme.xaml` and
`HueThemeKit.cs` as files to drop into a project. Every other app in the suite
takes them that way; this one stays a single `.cs` because it is the tool the
others are built with, and it has to be readable end to end to be worth
trusting.

What that costs, stated plainly:

- The kit's 2,618-line dictionary is carried at about 1,200 lines, holding the
  styles this app actually uses. Left out: radio buttons, switches, sliders,
  lists, trees, data grids, menus, expanders, group boxes, HTTP code chips,
  timestamp stamps, category tags and the busy-overlay brushes.
- **Nothing is rewritten.** The palette, the ACTIVE block and every kept style
  are the kit's own text, so a difference against the kit is a kit change and
  never a local edit. A control this app has not used before gets its style
  brought over from the kit rather than invented here.
- The kit's `HueContrast` and `HueTitleBar` are inlined for the same reason, and
  keep their names. `HueSwitch` and `HueConfirm` are not here: there is no switch
  and no confirmation in this app.
- **Two buttons break the theme on purpose.** The suite gives each app one accent,
  and Cyan is this one's — but **Zip…** is amber and **Rebuild all** is indigo,
  so the two actions the hand reaches for most after Build stand apart from it.
  Both are the kit's own palette entries (Amber is the data and registry family,
  Indigo the media family), taken with their text partners, so the fills stay
  WCAG-checked, and both collapse into the system highlight under a Windows
  contrast theme like every other fill. The cost is three filled buttons in one
  view where the suite asks for one; it is accepted knowingly, written down in the
  theme block, and goes no further — nothing else in the app uses another
  family's colour.
- A Windows contrast theme still wins. `HueContrast` swaps every brush for the
  user's own colours while one is on, and hands the title bar back to Windows —
  which is why colours from code go through `SetResourceReference` with a token
  key rather than a brush held in a field.

## Files

| File | Purpose |
| --- | --- |
| `RepoBuilder.cs` | Everything: entry point, UI, theme, scanner, publisher |
| `RepoBuilder.csproj` | Single-file self-contained publish settings |
| `app.ico` | Exe icon (16–256 px) |
| `app_256.png` | Embedded resource for the in-app header |
| `app.svg`, `app.png`, `app_1024.png` | Source art, not referenced by the build |
