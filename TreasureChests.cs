namespace ElanciaLocal;

internal static class TreasureChests
{
    internal const ushort Stationary=0x4019, Pirate=0x440F, ZombieSprite=0x47D0;
    internal const string ChestName="나무상자", ZombieName="상자좀비";
    internal const int ZombieChanceDenominator=6;
    internal const ushort PirateKillFame=13;
    internal static bool IsChest(string name,ushort sprite)=>name==ChestName&&sprite is Stationary or Pirate;
    // The observed stationary-chest hits retain about 18% of ordinary melee damage.
    internal static ushort StationaryMeleeDamage(ushort damage)=>(ushort)Math.Max(1,damage*18/100);

    // The chest was continuously in view between destruction and respawn.
    internal static int? ObservedRespawnSeconds(int map,int x,int y,byte floor,ushort sprite,string name)=>
        (map,x,y,floor,sprite,name) is (10005,173,51,1,Stationary,ChestName)?799:null;
    internal static int? ObservedRespawnSeconds(FieldObject at)=>ObservedRespawnSeconds(at.Map,at.X,at.Y,at.Floor,at.Sprite,at.Name);

    // Complete same-tile S07 groups immediately before the stationary chest's S5F.
    static readonly (string Key,uint Quantity)[][] StationaryLoot=[
        [(WorldSettings.CurrencyKey,1),(EquipmentItems.PotionKey(true,2),1)],
        [(WorldSettings.CurrencyKey,1),(EquipmentItems.PotionKey(false,1),1),(EquipmentItems.PotionKey(true,1),1),(EquipmentItems.PotionKey(false,3),1)],
        [(WorldSettings.CurrencyKey,1),(EquipmentItems.PotionKey(false,3),1),(EquipmentItems.PotionKey(false,4),1)],
        [(WorldSettings.CurrencyKey,1)],
        [(WorldSettings.CurrencyKey,2),(EquipmentItems.PotionKey(false,2),1)],
        [(WorldSettings.CurrencyKey,2),(EquipmentItems.PotionKey(true,3),1),(EquipmentItems.PotionKey(false,3),1),(EquipmentItems.PotionKey(true,6),1)]
    ];

    internal static (string Key,uint Quantity)[] Loot(ushort sprite)=>sprite==Stationary?
        StationaryLoot[Random.Shared.Next(StationaryLoot.Length)]:
        Random.Shared.Next(5)==0?[(EquipmentItems.PotionKey(false,4),1)]:[];
}
