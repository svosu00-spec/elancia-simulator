namespace ElanciaLocal;

internal static class CastleOffice
{
    internal const int Map=912;
    internal const uint ResignationFee=10_000, BattleChargeFee=1_000, StartingBattlePoints=5;
    internal const string Confirm="/3A01000000020101", Decline="/3A01000000020102";
    internal const string ResignationOffer="39010408"+Confirm+Confirm;
    internal const string BattleChargeOffer="39010406";

    internal static bool IsClerk(LocalNpc npc)=>npc.Map==Map&&npc.Sprite==0x40AC&&npc.Name=="젠";
}
