using System.Text.Json.Serialization;
using PcHealth.Core.Modules;
using PcHealth.Core.Processes;

namespace PcHealth.Ipc;

/// <summary>
/// Messages exchanged between the unprivileged UI (pipe server) and the elevated
/// worker (pipe client). Deliberately tiny: the UI asks for a set of module ids,
/// the worker streams back progress and results, then exits.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(RunModulesRequest), "runModules")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(ModuleCompletedMessage), "moduleCompleted")]
[JsonDerivedType(typeof(ScanCompletedMessage), "scanCompleted")]
[JsonDerivedType(typeof(WorkerErrorMessage), "error")]
public abstract record PipeMessage;

/// <summary>UI -> worker. The worker treats these ids as a REQUEST, not authorization:
/// it intersects them with its own compiled-in registry of admin modules.</summary>
public sealed record RunModulesRequest(string[] ModuleIds) : PipeMessage;

public sealed record ProgressMessage(string ModuleId, string ModuleName, int Index, int Total, string Message) : PipeMessage;

public sealed record ModuleCompletedMessage(ModuleResult Result) : PipeMessage;

/// <summary>Final message. Carries the worker's receipt so the UI can show one
/// combined audit trail covering both processes.</summary>
public sealed record ScanCompletedMessage(List<ReceiptEntry> Receipt, List<string> RejectedModuleIds) : PipeMessage;

public sealed record WorkerErrorMessage(string Message) : PipeMessage;
