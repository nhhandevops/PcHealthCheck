# Acceptance test — PC Health Check

Run this before giving the app to anyone else. It takes about 45 minutes on one machine.

**How to use it.** Work top to bottom. Record the result of every row, including the ones
that pass. A blank row is a row nobody checked, and this whole project exists on the
principle that unchecked is not the same as fine.

**Rows marked 🔴 are blocking.** One failure there means do not distribute, regardless of how
the rest went. They are not the hardest tests — they are the ones where being wrong causes
harm rather than annoyance.

| Field | Value |
|---|---|
| Build version | |
| SHA-256 of the zip | |
| Tester | |
| Date | |
| Windows edition & build | |
| Drives (model / bus / internal or external) | |
| Second Windows account present? | |
| Display language | |

---

## A. It installs and starts like a normal program

| # | Step | Expected | Result |
|---|---|---|---|
| A1 | Verify the zip's SHA-256 against `SHA256SUMS.txt` | Matches exactly | |
| A2 | Extract to a folder that has never held the app | Extracts without warnings | |
| A3 | Run `PcHealthCheck.exe`, click through SmartScreen | Window opens within ~10 s | |
| A4 | Look at the taskbar and the exe's Properties | Icon shows; Product, version, Company, Copyright all filled in | |
| A5 | Check the version shown in an exported report | Matches the build exactly, pre-release suffix included | |
| A6 | 🔴 Confirm no UAC prompt appeared at any point so far | None | |
| A7 | Resize the window smaller and larger | Check list stays scrollable, nothing clipped or overlapping | |

## B. Consent — nothing runs unasked

| # | Step | Expected | Result |
|---|---|---|---|
| B1 | 🔴 On first launch, before touching anything | Every checkbox unticked; Scan disabled | |
| B2 | Read the summary line as you tick boxes | Counts of admin / temp-file / internet checks update and are correct | |
| B3 | Find the check list groups | "Runs without administrator" and "Needs administrator" with counts, admin group clearly marked | |
| B4 | Tick only non-admin checks and scan | 🔴 No UAC prompt at all | |
| B5 | Tick one admin check and scan | Exactly **one** UAC prompt, and only after pressing Scan | |
| B6 | Decline that UAC prompt | App continues, says the admin checks did not run, does not crash | |
| B7 | Close and reopen the app | Your ticks are remembered | |

## C. The promises it makes about your data

The reason this section exists: these are the claims that would make the app harmful rather
than merely wrong.

| # | Step | Expected | Result |
|---|---|---|---|
| C1 | 🔴 Run a full scan with a network monitor open (Wireshark, GlassWire, or Resource Monitor's Network tab) | **Zero** connections from PcHealthCheck.exe | |
| C2 | 🔴 Open the receipt after the scan | Lists every external program run with its full command line, and states 0 network connections | |
| C3 | 🔴 Without ticking Disk speed, check every drive root and your Documents folder | No file created or modified by the app anywhere | |
| C4 | Note the app's own folder locations | Only `%APPDATA%\PcHealthCheck` (consent, and cards hidden with "Don't show again") and `%LOCALAPPDATA%\PcHealthCheck` (history) | |
| C5 | Press Delete history, then look at the folder | History file gone | |
| C6 | 🔴 Export an HTML report and read it end to end | No passwords, no product keys, no BitLocker keys, no file contents | |
| C7 | Open the exported HTML with no internet connection | Renders fully — no external fonts, scripts or images | |

## D. The disk benchmark — the one check that writes

| # | Step | Expected | Result |
|---|---|---|---|
| D1 | Read its consent row before running | States the exact path, size, and that it is deleted afterwards | |
| D2 | Run it and watch each drive root during the scan | A `PcHealthCheck-bench` folder appears and disappears | |
| D3 | 🔴 After it finishes, check every drive root | No leftover folder or `bench.tmp` anywhere | |
| D4 | 🔴 Run it again and press Cancel mid-way | Still no leftover file — cleanup happens on cancel too | |
| D5 | Check the receipt | Names each test file with the size actually written, marked deleted | |
| D6 | Compare the speeds against what you know of the hardware | Plausible: HDD 80–200 MB/s, SATA SSD 450–560, NVMe 2,000+ | |
| D7 | If any drive is external, read its row | Reported without a grade, and the enclosure is mentioned | |
| D8 | Run the scan twice and compare | Numbers within ~10% of each other | |

## E. Honesty when something cannot be checked

The failure this app has made three times. Worth testing deliberately.

| # | Step | Expected | Result |
|---|---|---|---|
| E1 | Scan with an admin check selected but the UAC prompt declined | 🔴 Amber "Some checks could not finish" box appears above the results, naming each one and why | |
| E2 | Run "Startup for other accounts" on a PC with a second account that is not signed in | Says their registry could not be read; the count does **not** claim nothing is there | |
| E3 | Run a scan where everything succeeds | 🔴 The amber box does **not** appear | |
| E4 | Look at any check reported as Skipped | The note says why (no battery, tool not installed) — never silence | |
| E5 | Read the INFO rows | No grade, and each is understandable on its own — a plain fact needs no explanation, but anything that *could* be mistaken for an omission must say why it is not graded | |

## F. Trends

| # | Step | Expected | Result |
|---|---|---|---|
| F1 | Scan on a PC with no history | Says it is the first recorded scan, nothing to compare | |
| F2 | Scan twice within a few minutes | "Nothing has moved", and **no** false regression warning | |
| F3 | Untick "Remember results", scan | Receipt says nothing was recorded | |

## G. On-demand actions

| # | Step | Expected | Result |
|---|---|---|---|
| G1 | Read the Quick scan consent | States the duration, that Defender may contact its cloud, and what it will do if it finds something | |
| G2 | Run a Quick scan | Completes; result reported honestly (never "clean" if the exit code was unrecognised) | |
| G3 | Run "Scan a folder" on a folder you choose | 🔴 Nothing in that folder is moved, renamed or deleted | |
| G4 | Press "Check startup items" with no VirusTotal key | Explains a key is needed; sends nothing | |
| G5 | With a key, read the consent dialog | Lists every fingerprint that would be sent, and says files are never uploaded | |
| G6 | Decline it | 🔴 Nothing sent — confirm with the network monitor | |
| G7 | Accept it | Only virustotal.com is contacted; receipt reports the lookup count | |

## H. Optional tools

| # | Step | Expected | Result |
|---|---|---|---|
| H1 | With a tool missing, read the banner | Names the tool, why it helps, and the exact install command | |
| H2 | Accept the install | One UAC prompt; tool installs; the related check works on the next scan | |
| H3 | Close the app | Offers to remove what **it** installed, showing the removal command | |
| H4 | Choose "Keep it", reopen and close again | Offers again; nothing removed without a yes | |
| H5 | 🔴 With a tool that was already on the PC before the app | Never offered for removal | |
| H6 | Accept a removal, then check `dependency-history.txt` | Logged with the command to reinstall it | |

## I. Behaviour under stress

| # | Step | Expected | Result |
|---|---|---|---|
| I1 | Cancel a scan mid-way | Stops promptly; says nothing was changed | |
| I2 | Close the window during a scan | Exits without hanging or leaving files behind | |
| I3 | Run two copies at once | Neither corrupts the other's history or reports | |
| I4 | Run on a PC with an unplugged external drive selected earlier | Reports it as unavailable rather than failing the scan | |
| I5 | 🔴 Unplug an external drive **during** a benchmark of it | Reports the failure; no crash; no leftover file on the other drives | |

## J. Somewhere other than the machine that built it

The row most likely to be skipped, and the one that catches packaging faults.

| # | Step | Expected | Result |
|---|---|---|---|
| J1 | 🔴 Run on a PC with no .NET SDK installed | Works — the runtime travels with it | |
| J2 | Run on a laptop | Battery check reports wear; on a desktop it says Skipped, no battery | |
| J3 | 🔴 Run on a non-English Windows (Vietnamese if you have one) | All numbers correct — no speed or temperature off by a factor of 1000 from decimal-comma parsing | |
| J4 | Run as a Windows user without administrator rights at all | Non-admin checks work; admin ones say plainly they cannot run | |

## K. What you can do

The section is worked out from the scan's own grades, and its rules have unit tests. The window,
"Don't show again" and the wording against a real Windows do not, so they are checked here.
K5-K10 need a card on demand: on a **test PC** (not anyone's work laptop, and not on a public
network), switch off Windows Firewall for the Public network in Windows Security, then scan. That
gives the "Security settings are switched off" card. Turn it back on at the end (K10).

| # | Step | Expected | Result |
|---|---|---|---|
| K1 | Run a full scan with every check ticked | "What you can do" appears below the amber box (if there is one) and above "What changed since your last scan". A healthy PC shows no card, only "Nothing to suggest from the checks that ran." | |
| K2 | 🔴 Tick **Drive SMART health** (an admin check), scan, and decline the UAC prompt. Not another admin check alone: that leaves drive health out of the scan, which is K3's grey note | The section shows an amber "Drive health was not fully checked: … did not finish" line. With no card, "Nothing to suggest" is followed by "… did not finish (listed above), so this is not an all-clear." It never says "Nothing to suggest" alone | |
| K3 | Scan with only the checks that need no administrator | A grey "Drive health was not part of this scan: its checks need administrator rights." and **no** amber text in the section. (In a window started with Run as administrator, the grey line ends "its checks were not selected." instead.) | |
| K4 | If a scan shows more than five cards, press "N more" (otherwise write N/A and why) | It expands to the rest; the first five do not move | |
| K5 | With the firewall card showing, press "Don't show again" on it | The card goes; "1 hidden — Show again" appears; the status line says it comes back if it gets worse | |
| K6 | Close and reopen the app, then scan again with nothing changed | The card stays hidden, and "1 hidden" is still shown. `%APPDATA%\PcHealthCheck\dismissed-advice.json` holds only 16-character codes and dates | |
| K7 | 🔴 With the card hidden, export the HTML report and the JSON | Both still list "Security settings are switched off" | |
| K8 | Switch off the firewall for a second network too (Private), then scan | The card is back by itself, listing both networks | |
| K9 | Hide it again, then press "Show again" | Every hidden card is back, and `dismissed-advice.json` is gone | |
| K10 | Follow the card's own steps to turn both firewalls back on, then scan | The steps match this Windows (Windows Security, "Firewall & network protection"), and the card is gone | |
| K11 | Read a card that has lines for IT: a failing or worn drive, a hot and slow processor, or the Windows 11 card (if no PC shows one, write N/A) | Those lines carry a **FOR IT** badge; the HTML report writes "For IT:" before them | |
| K12 | Start the app with "Run as administrator" and scan | Cards show without a "Don't show again" link, and no `dismissed-advice.json` is written | |

---

## Sign-off

| | |
|---|---|
| Blocking rows (🔴) passed | ___ / 19 |
| Non-blocking failures, listed | |
| Machines tested on | |
| Decision | ⬜ Release ⬜ Fix and re-test |
| Signed | |

**A non-blocking failure is not automatically acceptable.** Write down what it was and why
shipping it is a reasonable trade. An unrecorded known fault becomes a surprise for whoever
uses this next.
