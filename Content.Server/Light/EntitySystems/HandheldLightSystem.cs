using Content.Server.Popups;
using Content.Server.PowerCell;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Light;
using Content.Shared.Rounding;
using Content.Shared.Toggleable;
using Content.Shared.Verbs;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Light.EntitySystems
{
    [UsedImplicitly]
    public sealed class HandheldLightSystem : SharedHandheldLightSystem
    {
        [Dependency] private readonly PopupSystem _popup = default!;
        [Dependency] private readonly PowerCellSystem _powerCell = default!;
        [Dependency] private readonly IPrototypeManager _proto = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

        // TODO: Ideally you'd be able to subscribe to power stuff to get events at certain percentages.. or something?
        // But for now this will be better anyway.
        private readonly HashSet<HandheldLightComponent> _activeLights = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<HandheldLightComponent, ComponentGetState>(OnGetState);

            SubscribeLocalEvent<HandheldLightComponent, ExaminedEvent>(OnExamine);
            SubscribeLocalEvent<HandheldLightComponent, GetVerbsEvent<ActivationVerb>>(AddToggleLightVerb);

            SubscribeLocalEvent<HandheldLightComponent, ActivateInWorldEvent>(OnActivate);

            SubscribeLocalEvent<HandheldLightComponent, ToggleActionEvent>(OnToggleAction);
        }

        private void OnToggleAction(EntityUid uid, HandheldLightComponent component, ToggleActionEvent args)
        {
            if (args.Handled)
                return;

            if (component.Activated)
                TurnOff(uid, component);
            else
                TurnOn(args.Performer, uid, component);

            args.Handled = true;
        }

        private void OnGetState(EntityUid uid, HandheldLightComponent component, ref ComponentGetState args)
        {
            args.State = new HandheldLightComponent.HandheldLightComponentState(component.Activated, GetLevel(uid, component));
        }

        private byte? GetLevel(EntityUid uid, HandheldLightComponent component)
        {
            // Curently every single flashlight has the same number of levels for status and that's all it uses the charge for
            // Thus we'll just check if the level changes.

            if (!_powerCell.TryGetBatteryFromSlot(uid, out var battery))
                return null;

            if (MathHelper.CloseToPercent(battery.CurrentCharge, 0) || component.Wattage > battery.CurrentCharge)
                return 0;

            return (byte?) ContentHelpers.RoundToNearestLevels(battery.CurrentCharge / battery.MaxCharge * 255, 255, HandheldLightComponent.StatusLevels);
        }

        private void OnActivate(EntityUid uid, HandheldLightComponent component, ActivateInWorldEvent args)
        {
            if (args.Handled)
                return;

            if (ToggleStatus(args.User, uid, component))
                args.Handled = true;
        }

        /// <summary>
        ///     Illuminates the light if it is not active, extinguishes it if it is active.
        /// </summary>
        /// <returns>True if the light's status was toggled, false otherwise.</returns>
        public bool ToggleStatus(EntityUid user, EntityUid uid, HandheldLightComponent component)
        {
            return component.Activated ? TurnOff(uid, component) : TurnOn(user, uid, component);
        }

        private void OnExamine(EntityUid uid, HandheldLightComponent component, ExaminedEvent args)
        {
            args.PushMarkup(component.Activated
                ? Loc.GetString("handheld-light-component-on-examine-is-on-message")
                : Loc.GetString("handheld-light-component-on-examine-is-off-message"));
        }

        private void AddToggleLightVerb(EntityUid uid, HandheldLightComponent component, GetVerbsEvent<ActivationVerb> args)
        {
            if (!args.CanAccess || !args.CanInteract)
                return;

            ActivationVerb verb = new()
            {
                Text = Loc.GetString("verb-common-toggle-light"),
                Icon = new SpriteSpecifier.Texture(new ("/Textures/Interface/VerbIcons/light.svg.192dpi.png")),
                Act = component.Activated
                    ? () => TurnOff(uid, component)
                    : () => TurnOn(args.User, uid,  component)
            };

            args.Verbs.Add(verb);
        }

        public bool TurnOff(EntityUid uid, HandheldLightComponent component, bool makeNoise = true)
        {
            if (!component.Activated || !TryComp<PointLightComponent>(uid, out var pointLightComponent))
            {
                return false;
            }

            pointLightComponent.Enabled = false;
            SetActivated(uid, false, component, makeNoise);
            component.Level = null;
            _activeLights.Remove(component);
            return true;
        }

        public bool TurnOn(EntityUid user, EntityUid uid, HandheldLightComponent component)
        {
            if (component.Activated || !TryComp<PointLightComponent>(uid, out var pointLightComponent))
            {
                return false;
            }

            if (!_powerCell.TryGetBatteryFromSlot(uid, out var battery) &&
                !TryComp(uid, out battery))
            {
                _audio.PlayPvs(_audio.GetSound(component.TurnOnFailSound), uid);
                _popup.PopupEntity(Loc.GetString("handheld-light-component-cell-missing-message"), uid, user);
                return false;
            }

            // To prevent having to worry about frame time in here.
            // Let's just say you need a whole second of charge before you can turn it on.
            // Simple enough.
            if (component.Wattage > battery.CurrentCharge)
            {
                _audio.PlayPvs(_audio.GetSound(component.TurnOnFailSound), uid);
                _popup.PopupEntity(Loc.GetString("handheld-light-component-cell-dead-message"), uid, user);
                return false;
            }

            pointLightComponent.Enabled = true;
            SetActivated(uid, true, component, true);
            _activeLights.Add(component);

            return true;
        }

        public void TryUpdate(EntityUid uid, HandheldLightComponent component, float frameTime)
        {
            if (!_powerCell.TryGetBatteryFromSlot(uid, out var battery) &&
                !TryComp(uid, out battery))
            {
                TurnOff(uid, component, false);
                return;
            }

            var appearanceComponent = EntityManager.GetComponentOrNull<AppearanceComponent>(uid);

            var fraction = battery.CurrentCharge / battery.MaxCharge;
            if (fraction >= 0.30)
            {
                _appearance.SetData(uid, HandheldLightVisuals.Power, HandheldLightPowerStates.FullPower, appearanceComponent);
            }
            else if (fraction >= 0.10)
            {
                _appearance.SetData(uid, HandheldLightVisuals.Power, HandheldLightPowerStates.LowPower, appearanceComponent);
            }
            else
            {
                _appearance.SetData(uid, HandheldLightVisuals.Power, HandheldLightPowerStates.Dying, appearanceComponent);
            }

            if (component.Activated && !battery.TryUseCharge(component.Wattage * frameTime))
                TurnOff(uid, component, false);

            UpdateLevel(uid, component);
        }

        private void UpdateLevel(EntityUid uid, HandheldLightComponent comp)
        {
            var level = GetLevel(uid, comp);

            if (level == comp.Level)
                return;

            comp.Level = level;
            Dirty(comp);
        }
    }
}
