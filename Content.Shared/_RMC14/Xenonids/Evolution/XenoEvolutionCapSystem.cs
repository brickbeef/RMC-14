using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;

namespace Content.Shared._RMC14.Xenonids.Evolution;

/// <summary>
/// Provides cap checks for xeno evolution and strains. The actual UI/do-after
/// handlers live in XenoEvolutionSystem so this system does not duplicate its
/// BUI event subscriptions.
/// </summary>
public sealed class XenoEvolutionCapSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public bool CanEvolve(Entity<XenoEvolutionComponent> xeno, EntProtoId choice)
    {
        if (!IsAtOverallCap(xeno, choice))
            return true;

        PopupCapReached(xeno, choice);
        return false;
    }

    public bool CanStrain(Entity<XenoEvolutionComponent> xeno, EntProtoId choice)
    {
        // A strain swap replaces the current Praetorian-family member rather
        // than adding one, so the six-member family cap must not prevent a
        // swap. The target strain's configured cap still applies.
        if (IsAtStrainCap(xeno, choice))
        {
            PopupCapReached(xeno, choice);
            return false;
        }

        return true;
    }

    private bool IsAtStrainCap(Entity<XenoEvolutionComponent> xeno, EntProtoId choice)
    {
        if (!_prototypes.TryIndex(choice, out var prototype) ||
            !prototype.TryGetComponent(out XenoEvolutionCappedComponent? cap, _compFactory))
            return false;

        var living = 0;
        var caps = EntityQueryEnumerator<XenoEvolutionCappedComponent>();
        while (caps.MoveNext(out var uid, out var existingCap))
        {
            if (uid == xeno.Owner || _mobState.IsDead(uid) || existingCap.Id != cap.Id)
                continue;

            living++;
            if (living >= cap.Max)
                return true;
        }

        return false;
    }

    private bool IsAtOverallCap(Entity<XenoEvolutionComponent> xeno, EntProtoId choice)
    {
        if (!_prototypes.TryIndex(choice, out var choicePrototype) ||
            !choicePrototype.TryGetComponent(out XenoEvolutionCappedComponent? choiceCap, _compFactory))
            return false;

        XenoEvolutionCappedComponent? overallCap = null;
        var strainIds = new HashSet<EntProtoId>();

        if (choicePrototype.TryGetComponent(out XenoEvolutionComponent? choiceEvolution, _compFactory) &&
            choiceEvolution.Strains.Count > 0)
        {
            overallCap = choiceCap;
            AddStrainCapIds(choiceEvolution.Strains, strainIds);
        }
        else if (xeno.Comp.Strains.Contains(choice) &&
                 TryComp(xeno.Owner, out XenoEvolutionCappedComponent? currentCap))
        {
            overallCap = currentCap;
            AddStrainCapIds(xeno.Comp.Strains, strainIds);
        }
        else
        {
            // Preserve the original behavior for capped castes that are not
            // part of a strain group: their own cap is the overall cap.
            overallCap = choiceCap;
        }

        if (overallCap == null)
            return false;

        var trackedIds = new HashSet<EntProtoId> { overallCap.Id };
        trackedIds.UnionWith(strainIds);

        var living = 0;
        var caps = EntityQueryEnumerator<XenoEvolutionCappedComponent>();
        while (caps.MoveNext(out var uid, out var cap) && living < overallCap.Max)
        {
            if (uid == xeno.Owner || _mobState.IsDead(uid) || !trackedIds.Contains(cap.Id))
                continue;

            living++;
        }

        return living >= overallCap.Max;
    }

    private void AddStrainCapIds(IEnumerable<EntProtoId> strains, HashSet<EntProtoId> ids)
    {
        foreach (var strain in strains)
        {
            if (_prototypes.TryIndex(strain, out var prototype) &&
                prototype.TryGetComponent(out XenoEvolutionCappedComponent? cap, _compFactory))
            {
                ids.Add(cap.Id);
            }
        }
    }

    private void PopupCapReached(Entity<XenoEvolutionComponent> xeno, EntProtoId choice)
    {
        if (!_prototypes.TryIndex(choice, out var prototype))
            return;

        _popup.PopupEntity(
            Loc.GetString("cm-xeno-evolution-failed-already-have", ("prototype", prototype.Name)),
            xeno,
            xeno,
            PopupType.MediumCaution);
    }
}
