using PcHealth.Core.Modules;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Base for Windows modules. The timing, cancellation and failure contract live in
/// <see cref="HealthModuleBase"/> in Core, shared with the Linux modules.
/// </summary>
internal abstract class WindowsModuleBase : HealthModuleBase
{
}
