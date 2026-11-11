namespace ElanciaLocal;

internal static class SenseTraining{
    internal const byte Slot=9,Category=3,TargetKind=2;
    internal const ushort Icon=27,Script=0xDC,OfferPage=324,MaxMastery=10000;
    internal const uint Price=2500;
    internal const int UseIntervalMs=1200;
    internal const string Introduction="3901006F/3A0100DC00310101/3A0100DC00A70101";
    internal const string CheckPath=Introduction+"/3A0100DC014400";
    internal const string OfferPath="grepa-sense-offer";

    internal static bool IsTeacher(LocalNpc npc)=>npc.Map%10000==210&&npc.Name=="그레파"&&npc.Sprite==0x4008;
    internal static bool Eligible(Character character)=>character.AptitudeOrder()>>4==1||
        (character.JobStats?.Int??WorldSettings.BaseAttribute)>=10;
    internal static int Range(ushort mastery)=>5+Math.Min(mastery,MaxMastery)/2000;

    internal static string Describe(FieldMonster monster,ushort mastery){
        string text=monster.Appearance.Name;
        if(mastery>=2500)text+=$"  Lv {monster.Level}";
        if(mastery>=5000)text+=$"|추격 {monster.ChaseStepMs/1000m:0.0}초/칸  HP {monster.Hp}";
        if(mastery>=7500)text+=$"|기본 공격력 {monster.ContactDamage}";
        return text;
    }
}

internal enum SenseEnrollment { Learned, AlreadyLearned, Ineligible, InsufficientGold }
