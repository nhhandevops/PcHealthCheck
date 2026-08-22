# Releasing

What `build-release.ps1` cannot do for you, in the order it needs doing.

```powershell
.\build-release.ps1
```

Produces `release\PcHealthCheck-<version>-win-x64.zip` and `SHA256SUMS.txt`, after running the
tests and the no-network gate. Everything below assumes that succeeded.

---

## 1. Before tagging anything

- [ ] `ACCEPTANCE.md` completed on at least one machine, all blocking rows passed.
- [ ] Version in `src/PcHealth.App/PcHealth.App.csproj` is the one you mean to ship, and
      `app.manifest`'s `assemblyIdentity version` matches its numeric part. They are separate
      files and nothing checks that they agree.
- [ ] `HANDOFF.md` status table reflects reality.

## 2. Code signing — the one that changes the user's first impression

Unsigned, every user meets a blue SmartScreen box telling them Windows protected their PC
from your program. They then have to click through it to run something that asks for
administrator rights. That is a bad first thirty seconds, and it teaches exactly the habit
this app exists to discourage.

**SignPath Foundation** signs open-source projects free: <https://signpath.org/apply>

- Requires a public repository. **This repo is private** — publishing it is a prerequisite,
  not an afterthought. Read `PRIVACY.md` and the commit history first and decide whether you
  want them public.
- Approval takes weeks. Apply before you need it.
- Once approved, signing is a CI step; nothing about the build changes.

Until then, `SHA256SUMS.txt` is the only integrity check a downloader has, so it ships with
every release and the checksum is quoted in the release notes.

## 3. GitHub release

```powershell
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
gh release create v1.0.0-rc.1 `
    release\PcHealthCheck-1.0.0-rc.1-win-x64.zip `
    release\SHA256SUMS.txt `
    --title "1.0.0-rc.1" --notes-file release-notes.md --prerelease
```

Release notes should carry, every time:

- The SHA-256 of the zip, in the notes themselves — not only in the attached file, so it can
  be checked without downloading anything.
- That the build is unsigned and SmartScreen will warn, with the "More info → Run anyway"
  wording.
- What changed, in terms of what the user sees rather than what the code does.

Keep `--prerelease` until acceptance has been done on more than the machine it was built on.

## 4. Microsoft SmartScreen

Even signed, a new certificate has no reputation and warns until enough people run it.

Submit the binary at <https://www.microsoft.com/en-us/wdsi/filesubmission> under *Software
developer → incorrectly detected*. Do this per release while reputation builds.

## 5. winget

Only worth doing once the package is signed and the release is public — the manifest points
at a URL and a hash, and submitting an unsigned binary to the community repo will be
questioned.

```yaml
# manifests/n/NguyenHuuHoangAn/PcHealthCheck/<version>/...installer.yaml
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: PcHealthCheck.exe
      PortableCommandAlias: pchealthcheck
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/<owner>/<repo>/releases/download/v<version>/PcHealthCheck-<version>-win-x64.zip
    InstallerSha256: <from SHA256SUMS.txt>
```

`portable` rather than an installer, deliberately: it keeps the "delete the folder to
uninstall" promise that the rest of the packaging makes.

## 6. After publishing

- [ ] Download the release on a machine that never built it, verify the checksum, run it.
      A release nobody has installed from the published artifact is a release nobody has
      tested.
- [ ] Bump the version in the csproj to the next `-rc` or dev version so the next build
      cannot be confused with the released one.

---

## Things that are deliberately not done

**No installer.** Nothing in Program Files, nothing in the registry, no uninstall entry. The
app promises not to change the machine it inspects; its own packaging should not be the
exception.

**No auto-update.** It would mean a network connection the user did not ask for, in an app
whose main claim is that it makes none. Update by downloading the next zip.

**No MSIX** until signing is in place — an unsigned MSIX cannot be installed at all, which is
worse than a warning that can be clicked through.
