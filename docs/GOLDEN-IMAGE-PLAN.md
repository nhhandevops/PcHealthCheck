<!-- Written 2026-10-27 from a read-only review: two research agents (the build process; what the app
     and kit can check), each checked by a skeptic against Microsoft, Lenovo, Rescuezilla/Clonezilla
     and NetBird sources. It is a plan: nothing in it is built. Never add an image folder name, a
     company name, a user name, a serial or a UUID to this file. -->

# Golden image plan: building, checking and restoring the company's Windows images

The owner restores a Rescuezilla disk image onto every laptop before handing it to a new employee
(decisions in [REMEDIES-PLAN.md](REMEDIES-PLAN.md), section 6). The image is still being built. This file
is the task plan for it:
- what to do with the two old images;
- how to build new ones;
- the decisions only the owner can make;
- what PC Health Check and its USB kit can check along the way;
- a short BitLocker guide for when an employee asks for encryption.

Facts were checked on 2026-10-27. Check dates and prices again before acting on them.

## The short version

- **Build one image per Windows version, not one per laptop model.** Build it in a Hyper-V virtual
  machine on the dev desktop, generalize it with Sysprep, capture it with Rescuezilla, then add each
  model's drivers after the restore.
- **Sysprep (`/generalize`) is not optional.** Microsoft does not support copying a Windows install
  to another PC without it. On Windows 11 24H2 and later, laptops that share one machine SID also
  fail to sign in to each other (shares, printers, remote desktop; event 6167).
- **The two November 2025 images are not deployable as they are.**
  - The T470p one is a full image of a laptop that was in use, so it holds a former user's files,
    browser sessions and saved Wi-Fi keys. It also came from the SSD that is now failing.
  - Whether either image was generalized is unknown. Treat both as personal data until checked
    (task 2).
- **Licensing is the biggest open question.**
  - Microsoft grants the right to put one standard image on many laptops ("reimaging rights") only
    through Commercial/Volume Licensing.
  - Each laptop still activates only on its own licence. A Pro image will not activate on the
    T470p's Home licence.
  - Ask a reseller before the full build (decision A).
- **Which Windows 11 version:** 26H2. Home and Pro support ends on 2028-10-10 (the app's support table, from Microsoft's
  release-information page); 25H2 ends on
  2027-10-12, and 24H2 already ended on 2026-10-13. An unsupported T470 is reportedly not offered
  feature updates (community reports, not Microsoft), so it stays on the version it was imaged with.
  Microsoft says unsupported devices are not entitled to updates at all.
- **Lenovo has no Windows 11 drivers for the T470 family.** Its last Windows 10 driver packs are
  21H1 (T470, T470s) and 20H2 (T470p). On Windows 11 those laptops run on Windows 10 drivers, which
  neither Microsoft nor Lenovo supports. The T14 Gen 1 has Windows 11 packs and a supported
  processor.
- **Keep two verified copies of every image.** Neither copy may be the 1 TB audit drive alone, since
  it logged disk errors.

## Start today: no decision needed

### 1. Secure the two old images (owner, small)

1. Copy both image folders from the audit drive to a second, healthy drive. K: on the T470p has
   202 GB free, and the two images are about 25 GB.
2. Make a SHA-256 manifest of the source and of the copy (`Get-FileHash -Algorithm SHA256`), and
   compare them.
3. Boot Rescuezilla and run **Verify Image** on the copy. Use Verify only: Restore overwrites a disk.

Only a matching hash plus a passing Verify counts as a good copy, because the source drive has
logged errors. The image folders contain the source laptop's firmware details (`Info-dmi.txt`) and
their names include a company name and a user name. They never go near the repository or
`audit-results/`.

### 2. Find out what the old images are, without restoring them onto a fleet laptop (owner, small)

1. Open each copy in Rescuezilla's **Image Explorer** (2.6.1 or later), and read
   `Windows\Setup\State\State.ini`.
2. Only `ImageState=IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE` means "generalized and never booted
   since". Do not judge by the first-boot screen: an image sealed with `/oobe` but without
   `/generalize` also boots to the region screen, and it keeps the original SID.
3. If Image Explorer cannot open the image, restore it into a throwaway Hyper-V VM with **no network
   adapter while Windows boots**. A non-generalized image online can reach NetBird, Chrome and
   OneDrive as the original laptop.
4. Write down, without names: the Windows build, the edition, MBR or GPT, and whether the image was
   a fresh build or someone's laptop.

**Verdict.** Do not deploy an image that is not generalized, or one captured from a laptop that was in
use. Keeping or deleting the personal image is a data-retention decision for the owner.

### 3. Before wiping each T470, while its old Windows 10 still runs (technician)

- **Update the BIOS first.** Lenovo publishes the BIOS packages only in the Windows 10 catalogs for
  these models. After a Windows 11 restore there is no catalogued way to get them. The latest
  versions are T470 1.79, T470s 1.55 and T470p 1.41.
- **In ThinkPad Setup (F1):**
  - Security > Security Chip: TPM 2.0, Enabled;
  - Startup: UEFI Only, CSM off;
  - Secure Boot: Enabled.

  A supervisor or hard-disk password blocks these changes, so check for one first. Set boot mode
  **before** the restore, because Lenovo warns against changing it after an OS is installed.
- **Before switching the TPM on:** if the laptop's Windows is encrypted, suspend BitLocker first.

## The build, step by step (owner)

| # | Task | Effort | Needs |
|---|---|---|---|
| 4 | **Build host and pinned sources.** Turn on Hyper-V on the dev desktop. The T470 laptops cannot host a Windows 11 VM. Download into a folder outside the repo, each with its version and SHA-256 written down: the Windows 11 26H2 ISO (keep it, since later downloads differ), and Windows 10 22H2 only if ESU is bought. Also Rescuezilla 2.6.2, the Chrome Enterprise 64-bit MSI, UniKey 4.6 RC2 x64 (check its signature), LightShot, a pinned NetBird MSI, and each model's Lenovo SCCM driver pack | small | — |
| 5 | **Answer file (`unattend.xml`).** Settings: local IT admin account; `HideOnlineAccountScreens`; time zone `SE Asia Standard Time`; English UI; computer name `*` (random) until handover; no CopyProfile. Windows 11 needs it, because Microsoft is removing the local-account workarounds in OOBE: without it, an employee may be pushed into a personal Microsoft account. Pass it to Sysprep **from a separate virtual disk or ISO**, never from C:, or it is captured into every laptop. Delete Windows' cached copy from `SetupComplete.cmd`. Test that 26H2 still honours it | medium | decisions C, D |
| 6 | **Reference VM in audit mode.** Use a Gen 2 VM with Secure Boot and a vTPM, a **small** dynamic disk (64-100 GB, see task 11), and no network at first. Install Pro without a key. Press Ctrl+Shift+F3 at the first OOBE screen to enter audit mode, and never sign in with a Microsoft account. **First thing:** set `HKLM\SYSTEM\CurrentControlSet\Control\BitLocker` `PreventDeviceEncryption=1` and confirm `manage-bde -status C:` says Fully Decrypted. Windows 11 can start encrypting by itself, and Sysprep then fails. Bring Windows fully up to date while holding Store app updates off: Store updates during a build are the usual Sysprep failure. Remove any policy you used for that before capture | large | task 4 |
| 7 | **The four apps.** **Chrome:** the Enterprise MSI, system-wide; never open Chrome on the reference. **UniKey:** extract it to `C:\Program Files\UniKey\` and start it for every user from an all-users Run entry (decision E). **LightShot:** install it interactively and decline any offers (decision F). **NetBird:** install the MSI, never run `netbird up`, never open its window | medium | decisions E, F |
| 8 | **Clean-up, checklist, Sysprep.** Remove OneDrive (and its Run entry for new users). Run `Dism /Online /Cleanup-Image /StartComponentCleanup`. Then, **as the very last step, with no restart afterwards**, stop the NetBird service and delete its state: `default.json`/`config.json` (PrivateKey and SSHKey), `active_profile.json` and the profile files. The service creates its keys the first time it starts, so any restart after this recreates them, and every laptop would become the same NetBird peer. Then take a Hyper-V checkpoint and run `sysprep /generalize /oobe /shutdown /unattend:<file on the separate disk>`. Do not boot the VM again | medium | task 7 |
| 9 | **Capture and verify.** Boot Rescuezilla in the VM (Secure Boot template "Microsoft UEFI Certificate Authority" for this boot only). Back up the Windows disk to a second virtual disk or a network share, then run Verify Image. To keep a Hyper-V copy as well, use `Export-VM` or merge the checkpoint after Sysprep. Copying the base `.vhdx` would give the un-generalized state | medium | task 8 |
| 10 | **Name, record, two copies.** Folder name `<OS>-<version>-<edition>-<lang>_<date>_v<n>`. Keep a `BUILD.txt` (ISO hash, build, latest update, app versions, driver pack versions, Verify result) and a `SHA256SUMS.txt` beside it. Keep one copy on the audit drive and one on a healthy drive. Keep the previous version until the new one passes task 13 | small | — |
| 11 | **Fitting different disk sizes.** Rescuezilla does not shrink partitions, and Windows keeps its recovery partition right after C:, so C: cannot simply be extended after a restore to a bigger disk. **Recommended:** a small image plus a tested post-restore script that moves the recovery partition and extends C:. The script lives with the images, **never in the kit**. It prints the layout and the planned actions first, and **stops** unless the disk is exactly EFI, MSR, Windows, then recovery. Microsoft's KB5028997 covers only part of this, so the rest is our own procedure and must be tested in the VM first. "GB" in Hyper-V means GiB: a "239 GB" disk is larger than a 240 GB SSD | medium | decision G |
| 12 | **Drivers after the restore.** One folder per model on a USB drive. Install with `pnputil /add-driver <folder>\*.inf /subdirs /install`, then check with `pnputil /enum-devices /problem`. Windows 11 has no Wi-Fi package for the T470/T470s, so run this step **before** anything that needs the network (activation, NetBird). Do not inject every pack into the image | medium | task 4 |
| 13 | **First real restore, on a spare laptop.** Restore, fill the disk (task 11), apply drivers, check activation, enrol NetBird **after** the final computer name is set (NetBird takes the hostname at enrolment), update, restart. Then run the kit and the app before updates, after updates, and after drivers: the expected result is nothing but real prep items. **Secure Boot certificates:** Microsoft's 2011 boot certificates have expired (Windows Production PCA 2011 on 2026-10-19), and Windows is moving to "Windows UEFI CA 2023". An image whose boot manager is signed by the 2023 CA may not pass Secure Boot on a T470 whose firmware lacks it. Test one restore per model with Secure Boot on before any rollout | medium | tasks 3, 9, 11, 12 |
| 14 | **Windows 10 22H2 image, only if commercial ESU is bought.** Build it at least to KB5066791, then install KB5072653 (ESU preparation). Never put the ESU key in the image: apply it per laptop after the restore and confirm "Licensed" with `slmgr /dlv`. Ask the reseller which Year activation ID to use when Years 1 and 2 are bought together. Whether later ESU updates install without the licence is unverified, so expect each restore to download a year of updates | large | decision A |
| 15 | **Keep the images current.** Every 3 months, update the pre-Sysprep checkpoint (exported as a file, not left as a long checkpoint chain), run Sysprep, capture and verify it again, and save it as `v<n+1>` | small per round | — |

## Decisions only the owner can make

Each one has the default the plan uses until it is answered.

| | Question | Default |
|---|---|---|
| A | **Licensing.** Does the company have, or will it buy, a Microsoft Commercial/Volume Licensing agreement? It covers reimaging rights and commercial ESU, and it is ordered from a reseller in Vietnam | Ask the reseller before task 6; build nothing big until then |
| B | **The T470p** (Home licence, failing SSD): a Pro upgrade, a Home image, or retire it? | Retire it, or replace the SSD and buy Pro; no Home image |
| C | **Computer names and the admin password.** What naming scheme (at most 15 characters, never the serial)? Where is each laptop's local admin password kept? | Asset-tag names; a new password per laptop in the company's password store |
| D | **Does the company use Microsoft 365 / Entra ID or Active Directory?** It decides where BitLocker recovery keys and admin passwords can live | Assume no; keep both in the company's own records |
| E | **UniKey for every user, or per user?** | Every user (an all-users Run entry) |
| F | **LightShot.** Its screenshots upload to prnt.sc, where uploads can be viewed without signing in (Kaspersky). On Windows 11 the Print Screen key opens Snipping Tool by default. Keep it as is, keep it but tell staff not to upload company screens, or swap it for Windows' Snipping Tool? | Keep it, with a one-line warning to staff |
| G | **How to fill bigger disks:** a small image plus the script (task 11), Clonezilla's proportional restore (`-k1` with `-r`), or one image sized just under the smallest SSD? | The small image plus the script |
| H | **NetBird enrolment:** a setup key typed by the technician, or the employee's own sign-in? Who holds the key, and is NetBird self-hosted (`--management-url`)? | The technician with a one-off key, entered at handover, never stored |
| I | **Standard or administrator account** for employees? | Standard account |
| J | **Regional format:** English (US) or Vietnam? | English (US) until the app's Vietnamese checks (J3) are done |

## What PC Health Check and the kit can do (repository work, after Step A)

These are planned, not built. Each one is read-only. The kit never runs a fix and never writes to
the laptop. Anything that needs admin rights, signature checks or folder walks goes in the app;
cheap identity facts go in the kit.

| # | Task | Effort |
|---|---|---|
| I1 | **Fix: BitLocker's conversion status is ignored.** `SecurityPlatformModule.cs` queries `ConversionStatus` but grades only `ProtectionStatus`. A drive encrypted with its key in the clear, the state Windows 11 device encryption leaves on local-account PCs, is reported as "not encrypted". Report the conversion state, and treat "in progress" as unfinished. **Fixed 2026-10-28, uncommitted (SEC-002)** | small |
| I2 | **Golden image check: `4 - Check the reference before capture.cmd`.** Run on the reference just before Sysprep. It answers READY TO CAPTURE, FIX BEFORE CAPTURE (with a list) or NOT ESTABLISHED (with what could not be read). It saves to `ImageChecks\`, **never** `Results\`, because the fleet summary would merge the reference into a fleet laptop's row. It always passes `--skip-benchmarks` | medium |
| I3 | **App modules for that check (`--image-check`).** Things that break Sysprep: per-user Store packages that are not provisioned, BitLocker on, a pending restart (adds `PendingFileRenameOperations`), an earlier failed Sysprep. Things that would be cloned by mistake: extra profiles (counted), a NetBird key present (never its value), Chrome profiles (counted), Windows.old, temp and update-cache sizes, the app's own folder. Things the image must contain: the four apps installed machine-wide and signed, OneDrive absent, updates current, the time zone, the language, and a disk layout that fits a 240 GB SSD | large |
| I4 | **An image stamp and catalog.** The owner writes a small, name-free stamp on the reference before Sysprep, for example `C:\ProgramData\ImageStamp\image.json` with the image name, version and build. The stick keeps `Images\catalog.json`. The handover check (Step D) asks "restored from which image?" from that list, compares the answer with the stamp, and fails on a mismatch. It shows NOT ESTABLISHED when there is no stamp. It never uses the install date | medium |
| I5 | **Clones that were not generalized.** The handover run records a truncated hash of the machine SID, taken from the latest finished run only. The fleet table notes rows that share one: "N laptops share one Windows identity". It is never used to group runs. It is a new reading, so PRIVACY.md and AUDIT-GUIDE must name it, and the owner must accept it | medium |
| I6 | **Fleet table: an Image column**, with counts per image version and each Windows version's end date | small |
| I7 | **Intake readings for imaging:** boot mode (UEFI or legacy), partition style, internal disk size against the image's size, and whether the Secure Boot database has "Windows UEFI CA 2023" (information) | small |
| I8 | **UniKey and typing in the kit.** Telex rewrites "dd" as "đ", and VNI turns digits after vowels into tone marks, so a serial "A1" can come out as "Á". The kit prints "switch UniKey to English (Ctrl+Shift)" before any typed answer. Test Telex, VNI and the elevated kit window in the VM | small |

**Order:** I1 is a fault, so it goes in right after Step A. I2 and I3 come before the first real image
build (task 8) if possible, since they turn failed Sysprep runs into a checklist. I4-I6 belong with
Step D (the handover check). I7 and I8 go with Step D's intake.

## BitLocker on request (decision 12: optional)

When an employee asks for drive encryption:
1. **Edition.** Encryption needs Pro, Enterprise or Education. Home must be upgraded first.
2. **Setting order.** On Windows 11 24H2 or later, a laptop that becomes able to encrypt can start
   device encryption **by itself, with its key in the clear**. So before switching the TPM on, either
   keep `PreventDeviceEncryption=1` (the image sets it), or encrypt deliberately in the next steps.
3. **TPM and firmware.** In ThinkPad Setup (F1): Security > Security Chip, TPM 2.0 on; UEFI only;
   Secure Boot on. TPM 2.0 does not work in legacy (CSM) mode. Do any "extend C:" work **before**
   encrypting, because partition changes trigger recovery.
4. **Turn it on.** From Settings, or `manage-bde -on C: -RecoveryPassword`. On a reused SSD, encrypt the
   whole drive, not just the used space.
5. **Check the protectors.** Run `manage-bde -protectors -get C:` and confirm both **TPM** and
   **Numerical Password** are listed.
6. **Store the 48-digit recovery password before handover.** Keep it in company records, filed by
   asset tag, separate from the laptop. Never put it in the stick's `Results\` or this repository, and
   never let it live only in an employee's personal Microsoft account.
7. **Before a BIOS or TPM firmware update,** suspend BitLocker. Clearing the TPM triggers recovery.
8. **When the laptop is reimaged later,** retire its old key record.

The kit records only the encryption state, plus the technician's yes/no for "recovery key stored".
It never reads keys.
