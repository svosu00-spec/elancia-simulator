namespace ElanciaLocal;

internal sealed record CombatAbility(byte Slot,string Name,ushort HeaderWord,ushort IconIndex,ushort Reference,ushort LegacyReference)
{
    internal bool UsesBow=>Slot==2;
    internal ushort AttackMotion=>Slot switch {1=>10,2 or 10=>14,CombatAbilities.GreatswordSlot=>15,11=>16,_=>12};
    internal (ushort Motion,ushort SwingSound,ushort ImpactSound) MeleeVisual(Character attacker,EquipmentItem? weapon,bool hit)=>
        (weapon?.Sprite,weapon?.Name) switch {
            (0xA7A7,"나무꾼도끼")=>(WoodcuttingAxeMotion(hit),9,1),
            (0x82D3,"티스배틀액스") when hit=>(Random.Shared.Next(3)==0?(ushort)13:(ushort)26,15,1),
            (0x8003,"롱소드") when CombatAbilities.Mastery(attacker,this)==10000=>(MasterLongswordMotion(hit),9,1),
            // Retail 1:30736 and 1:30753 use the same greatsword and mastery.
            (0x800B,"그레이트소드") when EquipmentItems.Agility(attacker,EquipmentItems.EquippedEffects(attacker))==5=>(16,9,1),
            _=>Slot switch {
                3=>(AxeMotion(hit),19,12),
                4=>(DaggerMotion(hit),15,14),
                _=>(AttackMotion,9,1)
            }
        };
    // Use the per-weapon hit and miss samples in combat-experience-death.md.
    static ushort WoodcuttingAxeMotion(bool hit)=>
        (hit?Random.Shared.Next(2)==0:Random.Shared.Next(7)<3)?(ushort)8:(ushort)13;
    static ushort MasterLongswordMotion(bool hit){
        int roll=Random.Shared.Next(hit?21:12);
        if(roll<(hit?6:5))return 4;
        return roll<(hit?16:10)?(ushort)6:(ushort)8;
    }
    static ushort AxeMotion(bool hit)=>hit||Random.Shared.Next(3)==0?(ushort)13:(ushort)8;
    static ushort DaggerMotion(bool hit){
        if(!hit)return 7;
        return Random.Shared.Next(4) switch {0=>11,1=>7,_=>5};
    }
}

internal static class CombatAbilities
{
    internal const int CounterVersion=1;
    // Two immediate maximum-HP increases across 374 recorded attacks with a training update.
    // The retail roll itself is server-side. Use the observed rate until more samples are available.
    internal const int TrainingHealthRoll=187;
    // Map 297: 26 increases during 527 positive self hits on the fixed target.
    internal const int FixedDollHealthRoll=20;
    // Historic reports place the free target at 5.00. Current captures show
    // continued gains at 0.39 and no gains on the same target at 16.39.
    internal const int FixedDollTrainingLimit=500;
    internal const int LastRecordedBeginnerDollMastery=39;
    internal const int FixedDollStrikeSamples=559;
    internal const int StoneGolemStrikeSamples=54;
    internal const ushort DefaultHitCounter=120;
    internal const byte BeginnerUnarmedReference=18;
    internal const ushort UnarmedReferenceBoundary=2000;
    internal const ushort UnarmedReferenceBeforeBoundary=50;
    internal const ushort UnarmedReferenceAfterBoundary=52;
    internal const byte GreatswordSlot=7;
    internal const ushort BastardSwordReference=6;
    internal static readonly CombatAbility Greatsword=new(GreatswordSlot,"대검",15,4,2,2);
    // Header and icon from retail S2C 1:122318. Keep the local entry-level counter.
    internal static readonly CombatAbility Harp=new(8,"현금",9,8,25,25);
    // Local slots 10 and 11 avoid the existing Sense slot. S2C headers, icons and references are from capture 1:122318.
    internal static readonly CombatAbility Spear=new(10,"창, 특수창",14,9,293,293);
    internal static readonly CombatAbility Staff=new(11,"봉, 해머",16,6,81,81);
    // Official ability icon 40002 and the reported entry-level two-count reference.
    internal static readonly CombatAbility GreaterBlade=new(12,"대도",12,2,2,2);
    internal static readonly CombatAbility[] Optional=[Greatsword,Harp,Spear,Staff,GreaterBlade];
    internal static readonly CombatAbility[] All=[
        new(1,"격투",10,0,26,147), new(2,"활",14,7,22,165),
        new(3,"도끼",13,5,36,224), new(4,"단검",11,1,40,320),
        new(5,"검",12,3,20,111), new(6,"도",12,66,20,111)
    ];

    internal static CombatAbility For(Character character){
        var weapon=EquipmentItems.EquippedWeapon(character) is {} equipped?EquipmentItems.Get(equipped.Key):null;
        if(weapon?.Sprite is 0x8007 or 0x800B or 0x8349 or 0x834A or 0x84CA)return Greatsword;
        if(weapon?.Sprite==0x8017)return Harp;
        if(weapon?.Sprite is 0x8004 or 0x8009 or 0x834D or 0x834E)return Spear;
        if(weapon?.Sprite is 0x8002 or 0x8350)return Staff;
        if(weapon?.Sprite is 0x8016 or 0x834C)return GreaterBlade;
        if(weapon?.Sprite==0xA7A7&&weapon.Name=="나무꾼도끼")return All[2];
        string name=weapon?.Name??"";
        return name switch {
            _ when name.Contains("시미터",StringComparison.Ordinal)=>All[5],
            _ when name.Contains("보우",StringComparison.Ordinal)=>All[1],
            _ when name.Contains("액스",StringComparison.Ordinal)=>All[2],
            _ when name.Contains("대거",StringComparison.Ordinal)=>All[3],
            _ when weapon!=null=>All[4],
            _=>All[0]
        };
    }

    internal static bool Has(Character character,CombatAbility ability)=>
        ability==All[0]||character.Abilities?.ContainsKey(ability.Slot)==true||
        character.UnarmedCounterReference==0&&All.Contains(ability)||!Fishing.HasRod(character)&&For(character)==ability;

    internal static Character LearnEquipped(Character character){
        if(Fishing.HasRod(character))return character;
        var ability=For(character);
        if(ability==All[0]||character.UnarmedCounterReference==0&&!Optional.Contains(ability)||
            character.Abilities?.ContainsKey(ability.Slot)==true)return character;
        var counters=character.Abilities is {} saved?new Dictionary<byte,uint>(saved):[];
        counters[ability.Slot]=0;
        var learned=character with {Abilities=counters,AbilityCountsVersion=CounterVersion};
        if(ability!=Greatsword)return learned;
        // Pick the observed reference for the first sword and persist it with the skill.
        var weapon=EquipmentItems.Get(EquipmentItems.EquippedWeapon(character)!.Key);
        return learned with {GreatswordCounterReference=weapon.Sprite is 0x8007 or 0x8349
            ?BastardSwordReference:Greatsword.Reference};
    }

    // Convert old counters proportionally and retain the character's experience.
    internal static Character UpgradeCounters(Character character){
        if(character.AbilityCountsVersion!=0||character.Abilities is not {Count:>0} saved)return character;
        var counters=new Dictionary<byte,uint>(saved);
        foreach(var ability in All)
            if(saved.TryGetValue(ability.Slot,out uint points))
                counters[ability.Slot]=(uint)((ulong)points*Reference(character,ability)/ability.LegacyReference);
        return character with {Abilities=counters,AbilityCountsVersion=CounterVersion,CombatExperience=Experience(character)};
    }

    internal static byte Grade(Character character,CombatAbility ability)=>
        ShrineGrade.SavedRank(character,ability.Name,ability==Harp?character.HarpGrade:(byte)0);

    internal static uint Points(Character character,CombatAbility ability)=>
        character.Abilities?.GetValueOrDefault(ability.Slot)??0;
    internal static ushort Reference(Character character,CombatAbility ability){
        ushort reference=ability==All[0]&&character.UnarmedCounterReference!=0?character.UnarmedCounterReference:
            ability==Greatsword&&character.GreatswordCounterReference==BastardSwordReference?BastardSwordReference:ability.Reference;
        // Preexisting harp ranks were saved with the original reference. Preserve their counters.
        int ranks=Grade(character,ability)-(ability==Harp?Math.Min(ShrineGrade.MaxRank,character.HarpGrade):0);
        return ShrineGrade.GradedReference(reference,Math.Max(0,ranks));
    }
    internal static ushort Mastery(Character character,CombatAbility ability)=>
        (ushort)Math.Min(10000u,Points(character,ability)/Reference(character,ability));
    internal static uint CountersAdded(Character before,Character after,CombatAbility ability){
        uint gain=Points(after,ability)-Points(before,ability);
        if(ability==All[0]&&Grade(before,ability)==0&&Reference(before,ability)==UnarmedReferenceBeforeBoundary&&
            Reference(after,ability)==UnarmedReferenceAfterBoundary)
            gain-=UnarmedReferenceBoundary*(uint)(UnarmedReferenceAfterBoundary-UnarmedReferenceBeforeBoundary);
        return gain;
    }
    internal static ushort Progress(Character character,CombatAbility ability)=>
        (ushort)(Points(character,ability)%Reference(character,ability));

    // Local scaling fitted to the captured physical hits. Weapon ranges and
    // monster defense come from item descriptions and the monster catalog.
    internal static ushort PhysicalDamage(Character attacker,CombatAbility ability,EquipmentItem? weapon,ushort defense){
        if(ability==Harp)return (ushort)Math.Max(1,20*Grade(attacker,ability));
        int strength=Math.Clamp(GuardianQuest.Strength(attacker,EquipmentItems.EquippedEffects(attacker).Sum(e=>e.Str)),0,ushort.MaxValue);
        int skill=Mastery(attacker,ability)/100;
        int strengthDamage=strength+Math.Min(strength,10);
        int attack=weapon==null&&skill==0
            ?Random.Shared.Next(1,4)+Math.Max(0,strengthDamage-10)
            :strengthDamage+skill+3*Math.Min(skill,20)+EquipmentItems.RollWeaponAttack(weapon);
        int retainedPercent=Math.Max(25,100-2*defense);
        int damage=Math.Max(1,attack*retainedPercent/100);
        if(ability.Slot==4&&attacker.DaggerPoisonActive())damage*=2;
        return (ushort)Math.Min(damage+20*Grade(attacker,ability),ushort.MaxValue-1);
    }

    internal static ushort HiredMerchantDamage(ushort unmitigated,int roll,bool bastardSword=false,bool weakStrike=false){
        // Fit the observed miss/hit groups. The original defense and accuracy formulas are unknown.
        ushort displayed=(ushort)Math.Max(1,unmitigated/4);
        int accuracy=bastardSword?82:displayed switch {1=>11,<=7=>30,_=>46};
        if(roll>=accuracy)return 0;
        return weakStrike?(ushort)Math.Max(1,displayed/4):displayed;
    }

    // The four outcomes in map 297 keep the damage and counter results paired.
    internal static (ushort Damage,ushort Counters) FixedDollStrike(int roll)=>roll switch {
        <29=>(0,0), <32=>(0,1), <381=>(1,1), _=>(1,2)
    };

    internal static bool UsesRecordedBeginnerDollProfile(Character character,CombatAbility ability)=>
        character.Map==297&&Grade(character,ability)==0&&Mastery(character,ability)<=FixedDollTrainingLimit&&
        character.Items?.Any(item=>item.InventorySlot==0)!=true&&GuardianQuest.Strength(character,0)==5;

    // End bytes on the four response packets in the reference-18 beginner capture.
    internal static (bool FirstProgress,bool StatusMarker,bool Mastery,bool FinalProgress) FixedDollMasteryPadding(ushort mastery)=>(
        mastery is 4 or 21 or 27 or 36,
        mastery is 2 or 4 or 13 or 20 or 28,
        mastery is not (9 or 17 or 30 or 31 or 35 or 38),
        mastery is not (1 or 2 or 10 or 12 or 13 or 27 or 31 or 33 or 37));

    internal static ushort? ObservedTrainedDollDamage(Character attacker,CombatAbility ability,int map,int? roll=null){
        if(!WorldSettings.IsArcherTrainingRoom(map)||Grade(attacker,ability)!=0||Mastery(attacker,ability)!=1639)return null;
        var effects=EquipmentItems.EquippedEffects(attacker).ToArray();
        if(GuardianQuest.Strength(attacker,effects.Sum(effect=>effect.Str))!=37||
            EquipmentItems.Agility(attacker,effects)!=55)return null;
        // 533 self strikes in rooms 297 and 298: 30 misses, 234 for 9 damage and 269 for 10.
        return (roll??Random.Shared.Next(533)) switch {<30=>0,<264=>9,_=>10};
    }

    internal static (ushort Damage,ushort Counters)? ObservedStoneGolemStrike(
        Character attacker,CombatAbility ability,FieldObject target,int? roll=null){
        if(target is not {Sprite:0x47EB,Name:"스톤고렘"}||target.Map%10000!=10||ability!=All[0]||
            Grade(attacker,ability)!=0||Mastery(attacker,ability) is <2000 or >2030)return null;
        var effects=EquipmentItems.EquippedEffects(attacker).ToArray();
        if(GuardianQuest.Strength(attacker,effects.Sum(effect=>effect.Str))!=37||
            EquipmentItems.Agility(attacker,effects)!=55)return null;
        // Keep damage and the immediate S2C gain paired in the 54 recorded strikes.
        return (roll??Random.Shared.Next(StoneGolemStrikeSamples)) switch {
            <3=>(0,0), <7=>(0,1), <8=>(0,2),
            <12=>(100,2), <13=>(100,3), <16=>(102,2), <24=>(104,2), <25=>(104,3),
            <38=>(106,2), <46=>(108,2), <48=>(108,3), <49=>(110,2), _=>(112,2)
        };
    }

    // C03 packs the race into the body word and stores the creation sex index
    // separately. Use the official warrior bonus odds for the combo counter.
    internal static ushort ComboCounterAward(Character character,ushort bonus,int roll){
        if(bonus==0||character.Appearance.Length!=24)return bonus;
        var appearance=Convert.FromHexString(character.Appearance);
        int chance=(appearance[4]>>4,appearance[6]) switch {
            (1,1)=>35,(1,2)=>10,
            (2,1)=>50,(2,2)=>25,
            (3,1)=>75,(3,2)=>50,
            _=>0
        };
        return roll<chance?(ushort)(bonus*2):bonus;
    }

    internal static uint? ObservedMasteryExperience(CombatAbility ability,ushort before,ushort after){
        // Retail EXP and S2C samples in combat-experience-death.md.
        if(ability==Greatsword){
            uint award=Award(40,70,10);
            // The recorded first equip through 0.71 only awarded EXP at these boundaries.
            if(award>0||after<=71)return award;
        }else if(ability==All[0]){
            uint award=Award(2010,2030,400);
            if(award>0)return award;
        }
        return null;

        uint Award(int first,int last,uint amount){
            int low=Math.Max(first/10,before/10+1),high=Math.Min(last/10,after/10);
            return (uint)Math.Max(0,high-low+1)*amount;
        }
    }

    internal static uint Experience(Character? character)=>
        character?.CombatExperience??(uint)Math.Min(uint.MaxValue,character?.Abilities?.Values.Sum(points=>(long)points)??0);

    // Older local saves used the same balance for level XP and the S08 Fame field.
    internal static uint Fame(Character? character)=>character?.Fame??Experience(character);

    // Fit the recorded S39 level and progress samples and retain the local
    // level-2 threshold. The retail level-up boundaries are unobserved.
    internal static byte Level(uint experience){
        int level=1;
        while(level<byte.MaxValue&&experience>=LevelStartExperience(level+1))level++;
        return (byte)level;
    }
    static uint LevelStartExperience(int level){
        uint completed=(uint)(level-1);
        return completed*completed*(completed*completed+7*completed+92);
    }
    internal static byte LevelProgress(uint experience,byte level){
        if(level==byte.MaxValue)return 100;
        uint start=LevelStartExperience(level),end=LevelStartExperience(level+1);
        return (byte)(((ulong)experience-start)*100/(end-start));
    }
}
