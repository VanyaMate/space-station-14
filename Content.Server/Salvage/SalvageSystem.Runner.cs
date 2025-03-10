using Content.Server.Salvage.Expeditions;
using Content.Server.Salvage.Expeditions.Structure;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Shared.Chat;
using Content.Shared.Salvage;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    /*
     * Handles actively running a salvage expedition.
     */

    private void InitializeRunner()
    {
        SubscribeLocalEvent<FTLRequestEvent>(OnFTLRequest);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);
    }

    /// <summary>
    /// Announces status updates to salvage crewmembers on the state of the expedition.
    /// </summary>
    private void Announce(EntityUid mapUid, string text)
    {
        var mapId = Comp<MapComponent>(mapUid).MapId;

        // I love TComms and chat!!!
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapId),
            ChatChannel.Radio,
            text,
            text,
            _mapManager.GetMapEntityId(mapId),
            false,
            true,
            null);
    }

    private void OnFTLRequest(ref FTLRequestEvent ev)
    {
        if (!HasComp<SalvageExpeditionComponent>(ev.MapUid) ||
            !TryComp<FTLDestinationComponent>(ev.MapUid, out var dest))
        {
            return;
        }

        // Only one shuttle can occupy an expedition.
        dest.Enabled = false;
        _shuttleConsoles.RefreshShuttleConsoles();
    }

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        if (!TryComp<SalvageExpeditionComponent>(args.MapUid, out var component))
            return;

        // Someone FTLd there so start announcement
        if (component.Stage != ExpeditionStage.Added)
            return;

        Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", (component.EndTime - _timing.CurTime).Minutes)));

        if (component.DungeonLocation != Vector2.Zero)
            Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-dungeon", ("direction", component.DungeonLocation.GetDir())));

        component.Stage = ExpeditionStage.Running;
        // At least for now stop them FTLing back until the mission is over.
        EnsureComp<PreventPilotComponent>(args.Entity);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        // Started a mining mission so work out exempt entities
        if (TryComp<SalvageMiningExpeditionComponent>(
                _mapManager.GetMapEntityId(ev.TargetCoordinates.ToMap(EntityManager, _transform).MapId),
                out var mining))
        {
            var ents = new List<EntityUid>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            MiningTax(ents, ev.Entity, mining, xformQuery);
            mining.ExemptEntities = ents;
        }

        if (!TryComp<SalvageExpeditionComponent>(ev.FromMapUid, out var expedition) ||
            !TryComp<SalvageExpeditionDataComponent>(expedition.Station, out var station))
        {
            return;
        }

        // Let them pilot again when they get back.
        RemCompDeferred<PreventPilotComponent>(ev.Entity);

        // Check if any shuttles remain.
        var query = EntityQueryEnumerator<ShuttleComponent, TransformComponent>();

        while (query.MoveNext(out _, out var xform))
        {
            if (xform.MapUid == ev.FromMapUid)
                return;
        }

        // Last shuttle has left so finish the mission.
        QueueDel(ev.FromMapUid.Value);
    }

    // Runs the expedition
    private void UpdateRunner()
    {
        // Generic missions
        var query = EntityQueryEnumerator<SalvageExpeditionComponent>();

        // Run the basic mission timers (e.g. announcements, auto-FTL, completion, etc)
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Completed)
                continue;

            var remaining = comp.EndTime - _timing.CurTime;

            if (comp.Stage < ExpeditionStage.FinalCountdown && remaining < TimeSpan.FromSeconds(30))
            {
                comp.Stage = ExpeditionStage.FinalCountdown;
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-seconds", ("duration", TimeSpan.FromSeconds(30).Seconds)));
            }
            else if (comp.Stage < ExpeditionStage.MusicCountdown && remaining < TimeSpan.FromMinutes(2))
            {
                // TODO: Some way to play audio attached to a map for players.
               comp.Stream = _audio.PlayGlobal(comp.Sound,
                    Filter.BroadcastMap(Comp<MapComponent>(uid).MapId), true);
                comp.Stage = ExpeditionStage.MusicCountdown;
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", TimeSpan.FromMinutes(2).Minutes)));
            }
            else if (comp.Stage < ExpeditionStage.Countdown && remaining < TimeSpan.FromMinutes(5))
            {
                comp.Stage = ExpeditionStage.Countdown;
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", TimeSpan.FromMinutes(5).Minutes)));
            }
            // Auto-FTL out any shuttles
            else if (remaining < TimeSpan.FromSeconds(ShuttleSystem.DefaultStartupTime) + TimeSpan.FromSeconds(0.5))
            {
                var ftlTime = (float) remaining.TotalSeconds;

                if (remaining < TimeSpan.FromSeconds(ShuttleSystem.DefaultStartupTime))
                {
                    ftlTime = MathF.Max(0, (float) remaining.TotalSeconds - 0.5f);
                }

                ftlTime = MathF.Min(ftlTime, ShuttleSystem.DefaultStartupTime);
                var shuttleQuery = AllEntityQuery<ShuttleComponent, TransformComponent>();

                if (TryComp<StationDataComponent>(comp.Station, out var data))
                {
                    foreach (var member in data.Grids)
                    {
                        while (shuttleQuery.MoveNext(out var shuttleUid, out var shuttle, out var shuttleXform))
                        {
                            if (shuttleXform.MapUid != uid || HasComp<FTLComponent>(shuttleUid))
                                continue;

                            _shuttle.FTLTravel(shuttleUid, shuttle, member, ftlTime);
                        }

                        break;
                    }
                }
            }
        }

        // Mining missions: NOOP

        // Structure missions
        var structureQuery = EntityQueryEnumerator<SalvageStructureExpeditionComponent, SalvageExpeditionComponent>();

        while (structureQuery.MoveNext(out var uid, out var structure, out var comp))
        {
            if (comp.Completed)
                continue;

            var structureAnnounce = false;

            for (var i = 0; i < structure.Structures.Count; i++)
            {
                var objective = structure.Structures[i];

                if (Deleted(objective))
                {
                    structure.Structures.RemoveSwap(i);
                    structureAnnounce = true;
                }
            }

            if (structureAnnounce)
            {
                Announce(uid, Loc.GetString("salvage-expedition-structure-remaining", ("count", structure.Structures.Count)));
            }

            if (structure.Structures.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }
    }
}
