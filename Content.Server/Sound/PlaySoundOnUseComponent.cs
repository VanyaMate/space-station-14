namespace Content.Server.Sound
{
    [RegisterComponent]
    public class PlaySoundOnUseComponent : Component
    {
        [DataField("sound")] public string Sound = string.Empty;
    }
}
