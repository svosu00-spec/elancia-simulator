namespace ElanciaLocal;

internal static class MeditationTraining{
    internal const string Name="명상";
    internal const string Introduction="3901006F/3A0100DC00310101/3A0100DC00A70105";
    internal const string OfferPath=Introduction+"/3A0100DC00AF00";
    internal const string NextOfferPath="3901006F/3A0100DC00310101/3A0100DC00A700/3A0100DC00AF00";
    internal const string LowIntPath=OfferPath+"/3A0100DC00B000";
    internal const ushort Script=0xDC,OfferPage=175,Reference=132;
    // The local slot is free. Category, action kind, header and icon are from S2C 1:122318.
    internal const byte Slot=13,Category=3,ActionKind=5;
    internal const ushort Header=65,Icon=32;
    internal const uint Price=200_000;
    internal const int UseIntervalMs=1200;

    internal static int PureIntelligence(Character character)=>character.JobStats?.Int??WorldSettings.BaseAttribute;
    internal static ushort Mastery(uint counters)=>(ushort)Math.Min(10_000u,counters/Reference);
    internal static uint ManaRecovery(ushort mastery)=>1u+(uint)mastery/100;
}

internal enum MeditationEnrollment{Learned,AlreadyLearned,WrongJob,LowInt,InsufficientGold}
