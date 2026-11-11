namespace ElanciaLocal;

internal enum SpellResult { Applied, Unavailable, AlreadyLearned, MissingMana, MissingStone }

internal sealed record LocalSpell(byte Slot,string Name,string AratKey,ushort Icon,byte ManaCost,
    string StoneKey,byte StoneIndex,string FocusName,ushort Reference,CombatAbility Element,byte TargetMode=2,byte StoneCost=1,
    string? AlternateAratKey=null,ushort CastDelayMs=1000){
    internal const int TrainingManaRoll=187;
    // S2C 1:122318 supplies the elemental ability headers, icons and counter references.
    internal static readonly CombatAbility Danu=new(18,"다뉴",10,14,202,202);
    internal static readonly CombatAbility El=new(19,"엘",10,17,306,306);
    internal static readonly CombatAbility Tess=new(20,"테스",10,18,202,202);
    internal static readonly CombatAbility Brigid=new(21,"브리깃드",10,13,226,226);
    internal static readonly CombatAbility[] Elements=[Danu,El,Tess,Brigid];
    internal static readonly LocalSpell Dara=new(DaraSpell.Slot,DaraSpell.Name,ErenoaQuest.AratKey,DaraSpell.Icon,DaraSpell.ManaCost,
        RubyJewelry.Danium,1,"다니온링",DaraSpell.Reference,Danu);
    internal static readonly LocalSpell Rimedium=new(RimediumSpell.Slot,"리메듐",MetiniQuest.AratKey,RimediumSpell.Effect,6,
        RubyJewelry.Elsaria,4,"엘사리온링",1200,El);
    internal static readonly LocalSpell Braema=new(BraemaSpell.Slot,"브레마",TorionQuest.BraemaKey,
        BraemaSpell.Effect,0,RubyJewelry.Danium,1,"다니온링",600,Danu,TargetMode:5);
    internal static readonly LocalSpell Brel=new(BrelSpell.Slot,"브렐",RanicheQuest.BrelKey,
        BrelSpell.Effect,0,RubyJewelry.Danium,1,"다니온링",1200,Danu,TargetMode:5);
    internal static readonly LocalSpell Slow=new(PlayerDebuff.SlowSlot,"감속","mishat-slow",40,100,
        RubyJewelry.Danium,1,"다니온링",1200,Danu,StoneCost:4,AlternateAratKey:"lumit-slow");
    internal static readonly LocalSpell Curse=new(PlayerDebuff.CurseSlot,"저주","mishat-curse",37,20,
        RubyJewelry.Tessarat,5,"테사리온링",1200,Tess,StoneCost:4,AlternateAratKey:"lumit-curse");
    internal static readonly LocalSpell Bris=new(BrisSpell.Id,"브리스",BrisSpell.AratKey,35,7,
        RubyJewelry.Bridium,0,"브리디온링",2800,Brigid,CastDelayMs:3800);
    internal static readonly LocalSpell[] All=[Dara,Rimedium,Braema,Brel,Slow,Curse,Bris];

    internal byte WireSlot(Character? character)=>character?.SpellSlots?.GetValueOrDefault(Slot,Slot)??Slot;
    internal ushort Counter(Character character)=>Math.Min(Reference,character.SpellCounters?.GetValueOrDefault(Slot)??0);

    internal bool Teaches(string itemKey)=>itemKey==AratKey||itemKey==AlternateAratKey;

    internal bool IsKnown(Character character)=>Slot switch {
        DaraSpell.Slot=>character.DaraLearned,
        RimediumSpell.Slot=>character.RimediumLearned,
        BraemaSpell.Slot=>character.BraemaLearned,
        BrelSpell.Slot=>character.BrelLearned,
        PlayerDebuff.SlowSlot=>character.SlowLearned,
        PlayerDebuff.CurseSlot=>character.CurseLearned,
        BrisSpell.Id=>character.BrisLearned,
        _=>false
    };
    internal bool IsStone(ItemPlacement item){
        var stone=EquipmentItems.Get(StoneKey);
        return NpcMaterials.Matches(item,stone.Sprite,stone.Name);
    }
    internal bool HasFocus(Character character)=>(character.Items??[]).Any(item=>
        item.InventorySlot==0&&(EquipmentItems.Get(item.Key).Name==FocusName||
            Slot==RimediumSpell.Slot&&item.Key==TorionQuest.LenaRingKey));
}

internal static class DaraSpell {
    internal const string Name="다라";
    internal const byte Slot=1, ManaCost=4;
    internal const ushort Icon=36, Reference=2800;
    internal const int Range=8;
    internal static LocalSpell Data=>LocalSpell.Dara;

    internal static ushort RollDamage()=>(ushort)Random.Shared.Next(28,58);
}

internal static class BrisSpell {
    internal const byte Id=8;
    internal const string AratKey="arat-bris";
    internal static ushort RollDamage()=>(ushort)Random.Shared.Next(36,83);
}

internal static class RimediumSpell {
    internal const byte Slot=2;
    internal const ushort Effect=39;
    internal static LocalSpell Data=>LocalSpell.Rimedium;
    internal static uint RollHealing()=>(uint)Random.Shared.Next(10,16);
}

internal static class BraemaSpell {
    internal const byte Slot=4;
    internal const ushort Effect=50;
    // The retail conversion rate has not been captured.
    internal const uint MaxConversion=10;
}

internal static class BrelSpell {
    internal const byte Slot=5;
    internal const ushort Effect=49;
}
