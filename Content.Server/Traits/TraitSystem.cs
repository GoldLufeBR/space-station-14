using System.Linq;
using Content.Shared.Actions;
using Content.Server.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.Customization.Systems;
using Content.Shared.Players;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;
using Content.Server.Abilities.Psionics;
using Content.Shared.Psionics;
using Content.Shared.Mood;

namespace Content.Server.Traits;

public sealed class TraitSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly CharacterRequirementsSystem _characterRequirements = default!;
    [Dependency] private readonly SharedHandsSystem _sharedHandsSystem = default!;
    [Dependency] private readonly PlayTimeTrackingManager _playTimeTracking = default!;
    [Dependency] private readonly IConfigurationManager _configuration = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly PsionicAbilitiesSystem _psionicAbilities = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    // When the player is spawned in, add all trait components selected during character creation
    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        foreach (var traitId in args.Profile.TraitPreferences)
        {
            if (!_prototype.TryIndex<TraitPrototype>(traitId, out var traitPrototype))
            {
                DebugTools.Assert($"No trait found with ID {traitId}!");
                return;
            }

            if (!_characterRequirements.CheckRequirementsValid(
                traitPrototype.Requirements,
                _prototype.Index<JobPrototype>(args.JobId ?? _prototype.EnumeratePrototypes<JobPrototype>().First().ID),
                args.Profile, _playTimeTracking.GetTrackerTimes(args.Player), args.Player.ContentData()?.Whitelisted ?? false, traitPrototype,
                EntityManager, _prototype, _configuration,
                out _))
                continue;

            AddTrait(args.Mob, traitPrototype);
        }
    }

    /// <summary>
    ///     Adds a single Trait Prototype to an Entity.
    /// </summary>
    public void AddTrait(EntityUid uid, TraitPrototype traitPrototype)
    {
        RemoveTraitComponents(uid, traitPrototype);
        AddTraitComponents(uid, traitPrototype);
        AddTraitActions(uid, traitPrototype);
        AddTraitPsionics(uid, traitPrototype);
        AddTraitMoodlets(uid, traitPrototype);
        AddTraitGear(uid, traitPrototype);
    }

    /// <summary>
    ///     Removes all components defined by a Trait. It's not possible to validate component removals,
    ///     so if an incorrect string is given, it's basically a skill issue.
    /// </summary>
    /// <remarks>
    ///     This comes before AddTraitComponents for a good reason.
    ///     It allows for a component to optionally be fully wiped and replaced with a new component.
    /// </remarks>
    public void RemoveTraitComponents(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.ComponentRemovals is null)
            return;

        foreach (var entry in traitPrototype.ComponentRemovals)
        {
            if (!_componentFactory.TryGetRegistration(entry, out var comp))
                continue;

            EntityManager.RemoveComponent(uid, comp.Type);
        }
    }

    /// <summary>
    ///     Adds all Components included with a Trait.
    /// </summary>
    public void AddTraitComponents(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.Components is null)
            return;

        foreach (var entry in traitPrototype.Components.Values)
        {
            if (HasComp(uid, entry.Component.GetType()))
                continue;

            var comp = (Component) _serialization.CreateCopy(entry.Component, notNullableOverride: true);
            comp.Owner = uid;
            EntityManager.AddComponent(uid, comp);
        }
    }

    /// <summary>
    ///     Adds item required by the Trait.
    /// </summary>
    public void AddTraitGear(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.TraitGear != null)
        {
            if (!TryComp(uid, out HandsComponent? handsComponent))
                return;

            var coords = Transform(uid).Coordinates;
            var inhandEntity = EntityManager.SpawnEntity(traitPrototype.TraitGear, coords);
            _sharedHandsSystem.TryPickup(uid, inhandEntity, checkActionBlocker: false,
                handsComp: handsComponent);
        }
    }

    /// <summary>
    ///     Add all actions associated with a specific Trait
    /// </summary>
    public void AddTraitActions(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.Actions is null)
            return;

        foreach (var id in traitPrototype.Actions)
        {
            EntityUid? actionId = null;
            if (_actions.AddAction(uid, ref actionId, id))
            {
                _actions.StartUseDelay(actionId);
            }
        }
    }

    /// <summary>
    ///     If a trait includes any Psionic Powers, this enters the powers into PsionicSystem to be initialized.
    ///     If the lack of logic here seems startling, it's okay. All of the logic necessary for adding Psionics is handled by InitializePsionicPower.
    /// </summary>
    public void AddTraitPsionics(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.PsionicPowers is null)
            return;

        foreach (var powerProto in traitPrototype.PsionicPowers)
            if (_prototype.TryIndex<PsionicPowerPrototype>(powerProto, out var psionicPower))
                _psionicAbilities.InitializePsionicPower(uid, psionicPower, false);
    }

    /// <summary>
    ///     If a trait includes any moodlets, this adds the moodlets to the receiving entity.
    ///     While I can't stop you, you shouldn't use this to add temporary moodlets.
    /// </summary>
    public void AddTraitMoodlets(EntityUid uid, TraitPrototype traitPrototype)
    {
        if (traitPrototype.MoodEffects is null)
            return;

        foreach (var moodProto in traitPrototype.MoodEffects)
            if (_prototype.TryIndex(moodProto, out var moodlet))
                RaiseLocalEvent(uid, new MoodEffectEvent(moodlet.ID));
    }
}
