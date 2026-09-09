using System.Linq;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;

namespace Content.Shared._RMC14.Xenonids.Evolution;

/// <summary>
/// Enforces caste caps across both the base caste and its strains while
/// retaining the existing per-strain XenoEvolutionCappedComponent limits.
/// </summary>
public sealed class XenoEvolutionCapSystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly Dictionary<EntityUid, List<(List<EntProtoId> List, EntProtoId Choice, int Index)>> _blockedEvolutions = new();
    private readonly Dictionary<EntityUid, List<(EntProtoId Choice, int Index)>> _blockedStrains = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoEvolutionComponent, XenoEvolveBuiMsg>(OnXenoEvolveBui, before: new[] { typeof(XenoEvolutionSystem) });
        SubscribeLocalEvent<XenoEvolutionComponent, XenoEvolveBuiMsg>(OnXenoEvolveBuiAfter, after: new[] { typeof(XenoEvolutionSystem) });
        SubscribeLocalEvent<XenoEvolutionComponent, XenoEvolutionDoAfterEvent>(OnXenoEvolutionDoAfter, before: new[] { typeof(XenoEvolutionSystem) });
        SubscribeLocalEvent<XenoEvolutionComponent, XenoEvolutionDoAfterEvent>(OnXenoEvolutionDoAfterAfter, after: new[] { typeof(XenoEvolutionSystem) });
        SubscribeLocalEvent<XenoEvolutionComponent, XenoStrainBuiMsg>(OnXenoStrainBui, before: new[] { typeof(XenoEvolutionSystem) });
        SubscribeLocalEvent<XenoEvolutionComponent, XenoStrainBuiMsg>(OnXenoStrainBuiAfter, after: new[] { typeof(XenoEvolutionSystem) });
    }

    private void OnXenoEvolveBui(Entity<XenoEvolutionComponent> xeno, ref XenoEvolveBuiMsg args)
    {
        if (!IsAtOverallCap(xeno, args.Choice))
            return;

        if (!TryBlockEvolution(xeno.Comp, args.Choice, out var blocked))
            return;

        AddBlocked(xeno.Owner, blocked);
        PopupCapReached(xeno, args.Choice);
    }

    private void OnXenoEvolveBuiAfter(Entity<XenoEvolutionComponent> xeno, ref XenoEvolveBuiMsg args)
    {
        RestoreBlocked(xeno.Owner);
    }

    private void OnXenoEvolutionDoAfter(Entity<XenoEvolutionComponent> xeno, ref XenoEvolutionDoAfterEvent args)
    {
        if (!IsAtOverallCap(xeno, args.Choice))
            return;

        if (!TryBlockEvolution(xeno.Comp, args.Choice, out var blocked))
            return;

        AddBlocked(xeno.Owner, blocked);
        PopupCapReached(xeno, args.Choice);
    }

    private void OnXenoEvolutionDoAfterAfter(Entity<XenoEvolutionComponent> xeno, ref XenoEvolutionDoAfterEvent args)
    {
        RestoreBlocked(xeno.Owner);
    }

    private void OnXenoStrainBui(Entity<XenoEvolutionComponent> xeno, ref XenoStrainBuiMsg args)
    {
        // A strain change replaces the current family member, so the overall
        // family cap must not prevent the swap. The target strain cap still applies.
        if (!IsAtStrainCap(xeno, args.Choice))
            return;

        var index = xeno.Comp.Strains.IndexOf(args.Choice);
        if (index < 0)
            return;

        xeno.Comp.Strains.RemoveAt(index);
        if (!_blockedStrains.TryGetValue(xeno.Owner, out var blocked))
        {
            blocked = new();
            _blockedStrains[xeno.Owner] = blocked;
        }

        blocked.Add((args.Choice, index));
        PopupCapReached(xeno, args.Choice);
    }

    private void OnXenoStrainBuiAfter(Entity<XenoEvolutionComponent> xeno, ref XenoStrainBuiMsg args)
    {
        if (!_blockedStrains.Remove(xeno.Owner, out var blocked))
            return;

        foreach (var (choice, index) in blocked.OrderByDescending(x => x.Index))
        {
            var insert = Math.Min(index, xeno.Comp.Strains.Count);
            xeno.Comp.Strains.Insert(insert, choice);
        }
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

    private bool TryBlockEvolution(XenoEvolutionComponent comp, EntProtoId choice,
        out (List<EntProtoId> List, EntProtoId Choice, int Index) blocked)
    {
        var index = comp.EvolvesTo.IndexOf(choice);
        if (index >= 0)
        {
            comp.EvolvesTo.RemoveAt(index);
            blocked = (comp.EvolvesTo, choice, index);
            return true;
        }

        index = comp.EvolvesToWithoutPoints.IndexOf(choice);
        if (index >= 0)
        {
            comp.EvolvesToWithoutPoints.RemoveAt(index);
            blocked = (comp.EvolvesToWithoutPoints, choice, index);
            return true;
        }

        index = comp.EarlyEvolvesTo.IndexOf(choice);
        if (index >= 0)
        {
            comp.EarlyEvolvesTo.RemoveAt(index);
            blocked = (comp.EarlyEvolvesTo, choice, index);
            return true;
        }

        blocked = default;
        return false;
    }

    private void AddBlocked(EntityUid uid, (List<EntProtoId> List, EntProtoId Choice, int Index) blocked)
    {
        if (!_blockedEvolutions.TryGetValue(uid, out var entries))
        {
            entries = new();
            _blockedEvolutions[uid] = entries;
        }

        entries.Add(blocked);
    }

    private void RestoreBlocked(EntityUid uid)
    {
        if (!_blockedEvolutions.Remove(uid, out var blocked))
            return;

        foreach (var (list, choice, index) in blocked.OrderByDescending(x => x.Index))
        {
            if (!list.Contains(choice))
                list.Insert(Math.Min(index, list.Count), choice);
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
