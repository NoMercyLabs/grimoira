namespace Grimora.Hooks.Tools;

/// <summary>What <see cref="HookDoctorTool.Audit"/> found: same fields as hook-doctor.mjs's
/// <c>auditHooks</c> return object (RESTRUCTURE.md slice 22, "Hooks, part 3").</summary>
public sealed record HookAuditResult(
    bool PluginEnabledSetting,
    int PluginHooks,
    int DirectHooks,
    IReadOnlyList<string> Overlap,
    IReadOnlyList<string> DirectOnly,
    IReadOnlyList<string> DirectDuplicates,
    bool Unsafe);
