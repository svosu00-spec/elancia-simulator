namespace ElanciaLocal;

internal enum PotionBuff { Speed, Health }

internal static class PotionBuffs{
    internal const int DurationMs=84_000;
    internal const ushort ShownSeconds=83;
    internal static PotionBuff? For(EquipmentItem item)=>item switch {
        {Sprite:0x8F08,Name:"스피드포션(1ml)"}=>PotionBuff.Speed,
        {Sprite:0x8F18,Name:"체력향상포션(1ml)"}=>PotionBuff.Health,
        _=>null
    };
    internal static long Until(Character character,PotionBuff buff)=>
        buff==PotionBuff.Speed?character.SpeedPotionUntil:character.HealthPotionUntil;
    internal static TimedSpellData Timer(PotionBuff buff,ushort seconds)=>
        buff==PotionBuff.Speed?new(0x85,"스피드1",seconds):new(0x6E,"HP증가1",seconds);
    internal static ushort Remaining(long until,long now)=>
        (ushort)Math.Clamp((until-now)/1000,1,ShownSeconds);
    // The matched retail character's maximum HP rose from 2420 to 2855.
    internal static uint HealthBonus(uint maximum)=>(uint)((ulong)maximum*18/100);
}

internal static class ElBlessing{
    internal const string ItemKey="recorded-8f94-bfa4c0c7c3e0bab9c6f7bcc7283330c0cf29";
    internal const string ItemName="엘의축복포션(30일)";
    internal const ushort ItemSprite=0x8F94;
    internal const string WrappedItemKey="wrapped-el-blessing-30d";
    internal const string WrappedItemName="포장된엘의축복포션(30일)";
    internal const ushort WrappedItemSprite=0x8F70;
    internal const int DurationSeconds=86_400;
    internal const long ItemLifetimeMs=30L*DurationSeconds*1000;
    internal const ushort Effect=25;
    internal static bool IsItem(EquipmentItem item)=>item.Sprite==ItemSprite&&item.Name.TrimEnd()==ItemName;
    internal static long? NewItemDeadline(string key,long now)=>key==ItemKey?now+ItemLifetimeMs:null;
    // S3A exposes the low 16 bits of the real remaining seconds.
    internal static TimedSpellData Timer(long seconds)=>new(22,"엘의축복1일",unchecked((ushort)seconds));
    internal static long Remaining(Character character,long now)=>Math.Max(0,(character.ElBlessingUntil-now)/1000);
}
