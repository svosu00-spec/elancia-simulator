namespace ElanciaLocal;

internal sealed record PlayerDebuff(LocalSpell Spell,ushort TimerId,ushort Motion,ushort BaseSeconds){
    internal const byte SlowSlot=6, CurseSlot=7;
    internal static readonly PlayerDebuff Slow=new(LocalSpell.Slow,0x6D,2000,10);
    internal static readonly PlayerDebuff Curse=new(LocalSpell.Curse,0x74,1000,60);
    internal static readonly PlayerDebuff[] All=[Slow,Curse];

    internal static bool SlowActive(Character? character)=>character?.SlowUntil>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static int CursePenalty(Character? character)=>character?.CurseUntil>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()?5:0;
    internal long Deadline(Character character)=>Spell.Slot==SlowSlot?character.SlowUntil:character.CurseUntil;
    internal Character WithDeadline(Character character,long deadline)=>Spell.Slot==SlowSlot
        ?character with {SlowUntil=deadline}:character with {CurseUntil=deadline};
    internal ushort Duration(Character caster)=>(ushort)(BaseSeconds+2*CombatAbilities.Mastery(caster,Spell.Element));
    internal TimedSpellData Timer(ushort seconds)=>new(TimerId,Spell.Name,seconds);
    internal ushort Remaining(Character character,long now)=>(ushort)Math.Clamp((Deadline(character)-now+999)/1000,0,ushort.MaxValue);
}
