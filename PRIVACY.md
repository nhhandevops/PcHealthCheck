# Privacy

**PcHealthCheck has no telemetry, no crash reporting, and no phone-home. Ever.**

- The app works fully offline. It contains no network code: the CI build fails if any shipped assembly other than the opt-in `PcHealth.Net.Optional` references `System.Net`.
- Scan results exist only in memory until **you** export them, via a save dialog, to a location you choose (HTML or JSON). Nothing is uploaded anywhere.
- The app never reads the contents of your documents, photos, or personal files. Startup-item checks read program binaries only (to verify signatures and compute hashes).
- The app never modifies your system. The only file it writes during an M1 scan is a temporary battery report inside its own folder, which is deleted after parsing — and shown in the post-scan receipt.
- Future internet features (VirusTotal hash lookups, ClamAV signature downloads, update checks) will each require their own explicit, clearly-labeled consent every time their capability changes, and are opt-in — never defaults.
- Consent choices are stored locally in `%APPDATA%\PcHealthCheck\consent.json`. Delete it anytime to reset every choice to OFF.
- Verify us: run a scan with Wireshark or GlassWire open — you will see zero connections from this app. The receipt panel after each scan lists every process spawned and every file written.
