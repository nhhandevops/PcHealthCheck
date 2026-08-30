# Trying it on several machines

For the stage this project is actually at: not publishing anything, just running the app on
whatever PCs are to hand and comparing what it said. Publishing, code signing and the winget
manifest are deferred — see `RELEASING.md` when that changes.

## What to do on each machine

1. Copy the whole extracted folder over (USB stick, network share, whatever is easiest). It needs
   nothing installed — the .NET runtime travels inside the exe.
2. Windows will show a blue **SmartScreen** box, because the app is not code-signed yet. Click
   **More info** → **Run anyway**. That warning is expected and is itself worth noting: it is what
   anyone you eventually give this to will see.
3. Right-click **`collect-results.ps1`** → **Run with PowerShell**.
4. Send me the zip it leaves on the Desktop.

That is the whole loop. One zip per machine.

```powershell
# From a terminal instead, if you prefer, or to name the machine yourself:
.\collect-results.ps1 -Label "laptop-cua-me"

# To also run the administrator checks (raises exactly one UAC prompt):
.\collect-results.ps1 -IncludeAdmin
```

## What is in the zip

Look inside before sending — that is what it is for. It holds:

- the HTML and JSON reports the app wrote
- the console output and the exit code
- a summary of the machine: Windows version, CPU, memory, drives, display language, laptop or not
- which of the three optional tools were already installed

Those reports **name this PC**, its drives, the programs that start with Windows, and your browser
extensions. They contain **no** file contents, passwords, product keys, BitLocker keys or browsing
history — the app never reads those. The HTML report opens in any browser with no internet
connection.

## Which machines are worth the trouble

Not all machines tell us the same amount. In rough order of value:

| Machine | Why it is worth finding |
|---|---|
| **Windows with the display language set to Tiếng Việt** | The highest-value test there is. This project has already shipped two locale bugs, and a decimal-comma Windows is where a speed or a temperature silently comes out 1000× wrong. The collector records the decimal separator and warns you when it is not a dot. |
| **A desktop** (no battery) | The battery check should say *"No battery detected — desktop system"* and report `Skipped`. If it says anything about wear, or if the amber banner appears, that is a finding. |
| **A PC with a third-party antivirus** (Norton, ESET, Kaspersky…) | Defender then runs in passive mode legitimately. The security section must not call that a red flag. |
| **A PC with several drives**, especially an external USB one | External drives must be reported without a grade, and the enclosure mentioned. |
| **A machine with no .NET SDK ever installed** | Proves the runtime really travels with the app. This laptop no longer qualifies — the SDK was installed here to build it. |
| **A PC where you decline the UAC prompt** | Run with `-IncludeAdmin`, then click **No**. The report must show an amber *"Some checks could not finish"* box and mark the headline **INCOMPLETE**. Getting **GOOD** there is the single worst failure this app can produce, and it has happened before. |

## What to look for yourself

You do not have to analyse anything — send the zip and I will. But two things are worth a glance,
because you can spot them faster than I can:

- **A number that is obviously wrong.** A disk at 4 MB/s, a temperature of 45000°C, a battery at
  6000%. That is the locale bug, and it is why the Vietnamese machine matters.
- **An amber banner on a PC where nothing is wrong.** The box says *"Some checks could not
  finish"*. If it appears on a perfectly healthy machine, the app is crying wolf — which is a real
  bug, and a worse one than the reverse, because a warning that is always on teaches you to ignore
  it.

Both are more obvious to someone looking at their own PC than to me reading JSON.

## Running it twice on one machine

Fine, and useful — the second run compares against the first and reports what moved. The zip
filename carries a timestamp so nothing is overwritten. If you would rather the app remembered
nothing between runs, the app's own **Delete history** button clears it.
