namespace ElanciaLocal;

public sealed record GuardianProgress(uint? GuardianId=null,uint Pending=0,uint Points=0,
    int RewardDay=0,uint RewardsToday=0,long StrengthUntil=0,long CounterUntil=0);

internal enum GuardianResult { Applied, Invalid, InventoryFull, InsufficientPoints, AlreadyActive }
internal enum GuardianReward : byte { Speed=2, Strength=3, Counters=4 }

internal static class GuardianQuest{
    internal const string Certificate="recorded-8d17-bcf6c8a3c3b5bbe7c0d3b8edc0e5";
    internal const string Badge="guardian-angel-badge", Pencil="guardian-angel-pencil";
    internal const ushort Script=0x1A4, Duration=3600;
    internal const uint SpeakerId=0x8F14;
    internal static readonly LocalNpc CertificateSpeaker=new(SpeakerId,"수호천사임명장",0,0,0,0,0x8D17,0,0,"");
    internal static readonly LocalNpc BadgeSpeaker=CertificateSpeaker with {Name="수호천사의증표",Sprite=0x8033};

    internal static uint LevelPoints(int oldLevel,int newLevel){
        uint points=0;
        for(int level=oldLevel+1;level<=newLevel;level++){
            uint tier=(uint)((level-1)/10+1);
            points+=tier*tier;
        }
        return points;
    }
    internal static Character Accrue(Character previous,Character trained){
        if(previous.Guardian is not {GuardianId:not null} progress)return trained;
        int before=CombatAbilities.Level(CombatAbilities.Experience(previous));
        int after=CombatAbilities.Level(CombatAbilities.Experience(trained));
        return before<after?trained with {Guardian=progress with {Pending=progress.Pending+LevelPoints(before,after)}}:trained;
    }
    internal static bool StrengthActive(Character c,long now)=>c.Guardian?.StrengthUntil>now;
    internal static bool CountersActive(Character c,long now)=>c.Guardian?.CounterUntil>now;
    internal static int Strength(Character? c,int equipmentStrength){
        int strength=(c?.JobStats?.Str??WorldSettings.BaseAttribute)+equipmentStrength;
        if(c!=null&&StrengthActive(c,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))strength*=2;
        return strength-PlayerDebuff.CursePenalty(c);
    }
    internal static TimedSpellData Timer(GuardianReward reward,ushort seconds)=>reward switch {
        GuardianReward.Strength=>new(0x501,"수호천사 힘",seconds),
        GuardianReward.Counters=>new(0x502,"수호천사 카운트",seconds),
        _=>PotionBuffs.Timer(PotionBuff.Speed,seconds)
    };
    internal static ushort Remaining(long until,long now)=>(ushort)Math.Clamp((until-now+999)/1000,1,Duration);
}
