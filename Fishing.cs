namespace ElanciaLocal;

internal static class Fishing
{
    internal const string RodKey = "recorded-8006-b3acbdcbb4eb";
    internal const ushort RodAppearance = 0xF001;
    internal const string BaitKey = "recorded-8d44-c1f6b7b7c0ccbab428b4eb29";
    internal const string AnchovyKey = "recorded-8d4d-b8eac4a1";
    internal const string MinnowKey = "recorded-8c1f-b1ddb0adb8f0c4a1";
    internal const string TroutKey = "recorded-8c20-bbeac3b5beee";
    internal const string TropicalFishKey = "recorded-8c1d-bfadb4ebbeee";
    internal const string SandLanceKey = "recorded-8d4e-b1eeb3aab8ae";
    internal const string TunaKey = "recorded-8c21-c2fcc4a1";
    internal const ushort BottleCapacity = 200;
    internal const byte Slot = 17;
    internal const byte Reference = 5;
    internal const ushort CastFrames = 35;
    internal const int CastIntervalMs = CastFrames * 1000 / 6;
    internal const int CounterRollRange = 164;

    internal static byte Grade(Character character) => ShrineGrade.SavedRank(character, ShrineJob.FishingSkill);
    internal static ushort CounterReference(Character character) => ShrineGrade.GradedReference(Reference, Grade(character));
    internal static ushort Mastery(Character character) =>
        (ushort)Math.Min(10_000u, character.FishingPoints / CounterReference(character));
    internal static int CatchQuantity(Character character, string key) =>
        key is AnchovyKey or MinnowKey or TroutKey or TropicalFishKey or SandLanceKey or TunaKey
            ? 1 + Grade(character) : 1;

    internal static bool TryStoreCatch(Character character, ItemPlacement[] inventory, string key,
        out ItemPlacement[] updated, out ItemPlacement[] catches)
    {
        var items = inventory.ToList();
        var placed = new List<ItemPlacement>();
        var free = new Queue<byte>(character.FreeInventorySlots(inventory));
        uint capacity = EquipmentItems.Get(key).InventoryFlag1 == 1 ? uint.MaxValue : 1u;
        uint remaining = (uint)CatchQuantity(character, key);
        while (remaining > 0)
        {
            int stackIndex = capacity > 1
                ? items.FindIndex(item => item.InventorySlot != 0 && item.Key == key && item.Quantity < capacity)
                : -1;
            ushort x = (ushort)Random.Shared.Next(25, 225);
            ushort y = (ushort)Random.Shared.Next(15, 85);
            if (stackIndex >= 0)
            {
                var stack = items[stackIndex];
                uint added = Math.Min(remaining, capacity - stack.Quantity);
                var placement = stack with { Quantity = stack.Quantity + added, InventoryWord1 = x, InventoryWord2 = y };
                items[stackIndex] = placement;
                placed.Add(placement);
                remaining -= added;
                continue;
            }
            if (!free.TryDequeue(out byte slot))
            {
                updated = inventory;
                catches = [];
                return false;
            }
            uint quantity = Math.Min(remaining, capacity);
            var addedItem = new ItemPlacement(key, slot, quantity, x, y);
            items.Add(addedItem);
            placed.Add(addedItem);
            remaining -= quantity;
        }
        updated = items.ToArray();
        catches = placed.ToArray();
        return true;
    }

    // Contiguous bait debits showed 135 one-count and 29 two-count casts.
    // Use that frequency until the retail bonus conditions are identified.
    internal static uint AdvanceCounters(uint current, uint limit, int roll) =>
        current >= limit ? current : Math.Min(limit, current + (roll < 29 ? 2u : 1u));

    internal static bool HasRod(Character character) => character.Items?.Any(item =>
        item.InventorySlot == 0 && item.Key is RodKey or KondQuest.RodKey) == true;
    internal static Character LearnEquipped(Character character) =>
        !character.FishingLearned && (character.FishingPoints > 0 || HasRod(character))
            ? character with { FishingLearned = true }
            : character;
    internal static ushort Remaining(ItemPlacement item) => item.BaitRemaining ?? BottleCapacity;
    internal static string InventoryName(ItemPlacement item, string name) =>
        item.Key == BaitKey ? $"{name} [{Remaining(item)} 마리]" : name;

    internal static bool CanCast(Character character, LocalMap map)
    {
        if (character.Map is < 0 or >= 30000 || character.Map % 10000 != 2 ||
            !map.CanStand(character.X, character.Y, character.Floor))
            return false;
        var (dx, dy) = LocalMap.Delta(character.Direction);
        return map.CanSwim(character.X + dx, character.Y + dy, character.Floor);
    }

    // Nexon publishes the overall success rate. The unit value uses the
    // observed Silvia price of 596 Galrid for the recorded 200-worm bottle.
    internal static double SuccessChance(uint mastery) =>
        (10.0 + mastery * 0.0005 + (596.0 / BottleCapacity) * 0.00025 + (mastery == 10000 ? 5 : 0)) / 100;

    // Relative catch weights are local. Old rings enter the pool at mastery 20.00.
    internal static string ChooseCatch(uint mastery){
        switch(Random.Shared.Next(20)){
            case 0 when mastery>=2000:return RubyJewelry.OldIron;
            case 1 when mastery>=2000:return RubyJewelry.OldSilver;
            case 2 when mastery>=2000:return RubyJewelry.OldGold;
            case 3:return TeddyTailoring.OldShoeKey;
        }
        return Random.Shared.Next(10) switch{
            3 => MinnowKey,
            4 => TroutKey,
            5 or 6 => TropicalFishKey,
            7 or 8 => SandLanceKey,
            9 => TunaKey,
            _ => AnchovyKey
        };
    }
}
