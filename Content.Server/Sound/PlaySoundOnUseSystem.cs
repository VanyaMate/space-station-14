using Content.Shared.Interaction.Events;

namespace Content.Server.Sound;

public class PlaySoundOnUseSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlaySoundOnUseComponent, UseInHandEvent>(OnUseInHand);
    }

    private void OnUseInHand(EntityUid uid, PlaySoundOnUseComponent component, UseInHandEvent args)
    {
        _audio.PlayPvs(component.Sound, uid);
    }
}
