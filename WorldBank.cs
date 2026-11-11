using System.Security.Cryptography;

namespace ElanciaLocal;

public sealed record BankPassword(string Salt,string Hash);

internal enum BankLoginStage { NewPassword, Password, OpenStorage }

internal enum StorageAction : byte { Deposit, Withdraw, Rearrange, Quote }

internal sealed record StorageMove(ItemPlacement[] Inventory,ItemPlacement[] Storage,ItemPlacement? Remaining,ItemPlacement Destination);

internal static class WorldBank {
    internal const int Slots=InventoryStorage.Slots;
    internal const ushort NewPassword=0x82, NewPasswordRetry=0x83, Login=0x84, Welcome=0x85, LoginPrompt=0x86, Storage=0x87, Close=0x96;
    const int PasswordIterations=210_000;

    internal static bool IsBanker(LocalNpc npc)=>npc.Map%10000==209&&npc.Name=="스텔라"&&npc.Sprite==0x4071;

    internal static bool IsPasswordRequest(ReadOnlySpan<byte> packet)=>packet.Length>8&&packet[0]==0x39&&packet[6]==0&&
        packet[7] is (byte)NewPassword or (byte)Login;

    internal static bool TryPassword(ReadOnlySpan<byte> packet,out byte[] password){
        password=[];
        if(packet.Length<10||packet[8] is <1 or >8||packet.Length!=9+packet[8])return false;
        foreach(byte digit in packet[9..])if(digit is <(byte)'0' or >(byte)'9')return false;
        password=packet[9..].ToArray();
        return true;
    }

    internal static BankPassword Protect(byte[] password){
        byte[] salt=RandomNumberGenerator.GetBytes(16);
        return new(Convert.ToHexString(salt),Convert.ToHexString(Hash(password,salt)));
    }

    internal static bool Verify(BankPassword saved,byte[] password){
        try{
            byte[] salt=Convert.FromHexString(saved.Salt),expected=Convert.FromHexString(saved.Hash);
            return salt.Length==16&&expected.Length==32&&CryptographicOperations.FixedTimeEquals(Hash(password,salt),expected);
        }catch(FormatException){return false;}
    }

    static byte[] Hash(byte[] password,byte[] salt)=>Rfc2898DeriveBytes.Pbkdf2(password,salt,PasswordIterations,HashAlgorithmName.SHA256,32);

    internal static byte[] Dialog(LocalNpc banker,ushort action,string? prompt=null){
        if(action==Welcome&&banker.DialogReplies?.FirstOrDefault(reply=>reply.Flags==1&&reply.Menu==Login&&
            reply.Dialog is {Mode:12,ActionCode:Welcome}) is {} observed)
            return GamePackets.NpcDialog(banker,observed.Dialog);
        var dialog=new NpcDialogData(12,1,1,banker.Sprite,0,0x01000000U|((uint)banker.Sprite<<8),
            action==Welcome?banker.Name:"",ActionCode:action,ActionText:prompt);
        return GamePackets.NpcDialog(banker,dialog);
    }

    internal static StorageMove? Move(Character character,StorageAction action,byte sourceSlot,byte destinationSlot,ushort x,ushort y,uint quantity)=>
        InventoryStorage.Move(character,character.BankItems??[],action,sourceSlot,destinationSlot,x,y,quantity);
}

internal static class InventoryStorage {
    internal const int Slots=100;

    internal static StorageMove? Move(Character character,ItemPlacement[] stored,StorageAction action,
        byte sourceSlot,byte destinationSlot,ushort x,ushort y,uint quantity){
        if(sourceSlot==0||quantity==0||action>StorageAction.Rearrange)return null;
        var inventory=(character.Items??[]).ToList();
        var storage=stored.ToList();
        var sourceItems=action==StorageAction.Deposit?inventory:storage;
        var destinationItems=action==StorageAction.Withdraw?inventory:storage;
        var source=sourceItems.SingleOrDefault(item=>item.InventorySlot==sourceSlot);
        if(source==null||quantity>source.Quantity)return null;
        var definition=EquipmentItems.Get(source.Key);
        bool stackable=definition.EquipmentSlot==0&&definition.InventoryFlag1!=0;
        if(!stackable&&(quantity!=1||destinationSlot!=0))return null;

        if(action==StorageAction.Rearrange&&quantity==source.Quantity&&(destinationSlot==0||destinationSlot==sourceSlot)){
            var moved=source with {InventoryWord1=x,InventoryWord2=y};
            storage[storage.IndexOf(source)]=moved;
            return new(inventory.ToArray(),storage.ToArray(),null,moved);
        }
        if(action==StorageAction.Rearrange&&destinationSlot==sourceSlot)return null;

        ItemPlacement destination;
        if(destinationSlot==0){
            byte free=action==StorageAction.Withdraw?character.FreeInventorySlots(inventory).FirstOrDefault():
                Enumerable.Range(1,Slots).Select(slot=>(byte)slot).FirstOrDefault(slot=>storage.All(item=>item.InventorySlot!=slot));
            if(free==0)return null;
            destination=source with {InventorySlot=free,Quantity=quantity,InventoryWord1=x,InventoryWord2=y};
            destinationItems.Add(destination);
        }else{
            int index=destinationItems.FindIndex(item=>item.InventorySlot==destinationSlot);
            if(index<0)return null;
            var existing=destinationItems[index];
            if(existing.Key!=source.Key||source.Key==Fishing.BaitKey||
                existing.Durability!=source.Durability||existing.ExpiresAt!=source.ExpiresAt||existing.Quantity>uint.MaxValue-quantity)return null;
            destination=existing with {Quantity=existing.Quantity+quantity};
            destinationItems[index]=destination;
        }
        ItemPlacement? remaining=null;
        if(quantity==source.Quantity)sourceItems.Remove(source);
        else{
            remaining=source with {Quantity=source.Quantity-quantity};
            sourceItems[sourceItems.IndexOf(source)]=remaining;
        }
        return new(inventory.ToArray(),storage.ToArray(),remaining,destination);
    }
}
