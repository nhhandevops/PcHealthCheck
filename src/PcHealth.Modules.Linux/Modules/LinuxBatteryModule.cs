using PcHealth.Core.Metrics;
using PcHealth.Core.Modules;
using PcHealth.Modules.Linux.Support;

namespace PcHealth.Modules.Linux.Modules;

/// <summary>
/// Battery wear from the kernel's own power-supply class. Unlike Windows, this needs no
/// report file and no helper program: the battery's figures are plain files.
/// </summary>
internal sealed class LinuxBatteryModule(SystemFiles files) : HealthModuleBase
{
    private const string PowerSupplyDir = "/sys/class/power_supply";

    public override ModuleManifest Manifest { get; } = new()
    {
        Id = "linux.battery",
        Name = "Battery health",
        Description = "Battery wear (full-charge vs design capacity) and cycle count. Skipped automatically on desktops.",
        Reads =
        [
            "Battery and charger attributes (/sys/class/power_supply): type, status, charge level, full-charge and design " +
            "capacity, cycle count, maker, model, chemistry and charge limit; never the serial number",
        ],
        NeedsAdmin = false,
        EstimatedDuration = TimeSpan.FromSeconds(1),
    };

    protected override Task<ModuleStatus> ExecuteAsync(
        ModuleContext context, List<Metric> metrics, List<string> notes,
        IProgress<ModuleProgress>? progress, CancellationToken ct)
    {
        var failures = new ModuleFailures(notes);

        // As on Windows: when the list itself cannot be read, this machine's kind is unknown, and
        // the module must not claim the skip a desktop earns.
        // A kernel built without power-supply support has /sys/class but no power_supply in it,
        // and cannot run on a battery at all: that is a real "no battery", not a failed read.
        var names = files.TryListDirectories(PowerSupplyDir, out var error)
                    ?? (files.TryListDirectories("/sys/class", out _) is { } classes && !classes.Contains("power_supply")
                        ? []
                        : null);
        if (names is null)
        {
            failures.Record("Power supply list", error ?? "unreadable");
            failures.Note("Whether this PC has a battery at all could not be established, so battery wear was NOT checked.");
            return Task.FromResult(failures.Resolve(gotAnything: false));
        }

        var entries = names.Select(Read).ToList();
        var peripherals = entries.Where(PowerSupply.IsPeripheralBattery).Select(e => e.Name).ToList();
        if (peripherals.Count > 0)
            failures.Note($"Left out: the batteries of connected devices such as a wireless mouse ({string.Join(", ", peripherals)}).");

        var batteries = entries.Where(PowerSupply.IsSystemBattery).ToList();
        foreach (var empty in batteries.Where(b => !PowerSupply.IsPresent(b)))
            failures.Note($"{empty.Name}: the battery slot is empty.");
        batteries = [.. batteries.Where(PowerSupply.IsPresent)];
        if (batteries.Count == 0)
        {
            // A real answer: the list was read and holds no battery. It must not degrade the
            // status, or every healthy desktop scan would say some checks could not finish.
            failures.Note("No battery detected — desktop system, module skipped.");
            return Task.FromResult(ModuleStatus.Skipped);
        }

        foreach (var battery in batteries)
        {
            var subject = batteries.Count > 1 ? battery.Name : "Battery";

            if (PowerSupply.Identity(battery) is { } identity)
                metrics.Add(new Metric("battery.identity", "Battery reports itself as", identity, HealthCategory.Battery,
                    Subject: subject, InfoOnly: true,
                    Detail: "The maker and model the battery's own chip reports. Compare them with the label on the battery " +
                            "and your laptop's official battery list: a model from a different laptop usually means a third-party pack."));

            if (PowerSupply.Wear(battery) is { } wear)
                metrics.Add(new Metric("battery.capacity_pct", "Battery capacity vs design", wear.Pct, HealthCategory.Battery,
                    Unit: "%", Subject: subject,
                    Detail: $"Full charge {wear.Full:0.#} {wear.Unit} of {wear.Design:0.#} {wear.Unit} design. " +
                            "If the battery is swollen, stop using the device immediately."));
            else
                failures.Record($"Battery wear ({battery.Name})",
                    "the battery reports no full-charge and design capacity, so its wear was NOT checked");

            if (PowerSupply.ChargeState(battery) is { } state)
                metrics.Add(new Metric("battery.charge_state", "Charge right now", state, HealthCategory.Battery,
                    Subject: subject, InfoOnly: true,
                    Detail: "Not charging on mains power is usually deliberate: most laptops stop at 95-100%, and many hold " +
                            "a lower limit to slow wear (often 80%; Lenovo's conservation mode holds 55-60%). It only " +
                            "points at the battery or charger when no such limit is set."));

            if (PowerSupply.Cycles(battery) is { } cycles)
                metrics.Add(new Metric("battery.cycles", "Charge cycles", (double)cycles, HealthCategory.Battery,
                    Subject: subject, InfoOnly: true, Detail: "Most consumer batteries are rated for 300–1000 cycles."));
        }

        return Task.FromResult(failures.Resolve(gotAnything: metrics.Count > 0));
    }

    private PowerSupplyEntry Read(string name)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in PowerSupply.AttributesRead)
        {
            if (files.TryRead($"{PowerSupplyDir}/{name}/{attribute}") is { } value)
                attributes[attribute] = value.Trim();
        }
        return new PowerSupplyEntry(name, attributes);
    }
}
