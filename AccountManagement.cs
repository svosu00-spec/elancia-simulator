using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace ElanciaLocal;

internal static class AccountManagement
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    sealed record Request(string Action, string? AccountId, uint? CharacterId, string? Login, string? Password,
        uint Gold, ItemPlacement[]? Items, ItemPlacement[]? BankItems);

    internal static async Task Run(AccountDatabase database, CharacterStore characters, WorldHub hub,
        SemaphoreSlim worldGate, SecureGateway gateway, int port, CancellationToken stop)
    {
        string pipeName = $"Elancia.AccountManager.{port}";
        Console.WriteLine($"Account manager pipe {pipeName}");
        try {
            while (!stop.IsCancellationRequested) {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var time = CancellationTokenSource.CreateLinkedTokenSource(stop);
                time.CancelAfter(TimeSpan.FromSeconds(10));
                try {
                    var ct = time.Token;
                    byte[] header = new byte[4];
                    await pipe.ReadExactlyAsync(header, ct);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is <1 or >65536) continue;
                    byte[] bytes = new byte[length];
                    Request? request;
                    try {
                        await pipe.ReadExactlyAsync(bytes, ct);
                        request = JsonSerializer.Deserialize<Request>(bytes, Json);
                    } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                    object result = request == null ? Fail("잘못된 관리 요청입니다.") : await Apply(request, ct);
                    byte[] response = JsonSerializer.SerializeToUtf8Bytes(result, Json);
                    BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
                    await pipe.WriteAsync(header, ct);
                    await pipe.WriteAsync(response, ct);
                    await pipe.FlushAsync(ct);
                } catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException) { }
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        async Task<object> Apply(Request request, CancellationToken ct)
        {
            switch (request.Action) {
                case "state":
                    return new {
                        ok=true, accounts=database.Accounts(),
                        characters=characters.All().Select(c=>new {
                            c.Id,c.Account,c.Name,c.Gold,c.Map,c.X,c.Y,c.Floor,c.Job,
                            Items=c.Items??[],BankItems=c.BankItems??[],Online=hub.Attached(c.Id)!=null
                        }).ToArray(),
                        catalog=EquipmentItems.ManagedCatalog().Select(entry=>new {
                            key=entry.Key,name=entry.Value.Name,equipmentSlot=entry.Value.EquipmentSlot,
                            maximumDurability=entry.Value.EquipmentSlot==0?(ushort?)null:EquipmentItems.MaximumDurability(entry.Value)
                        }).ToArray()
                    };
                case "account":
                    return request.AccountId!=null&&request.Login!=null&&gateway.UpdateAccount(request.AccountId,request.Login,request.Password)
                        ? new {ok=true} : Fail("아이디 또는 비밀번호 규칙을 확인하세요. 이미 등록된 아이디는 사용할 수 없습니다.");
                case "character":
                    await worldGate.WaitAsync(ct);
                    try {
                        if(request.CharacterId is not {} id||characters.Find(id) is not {} current)
                            return Fail("캐릭터를 찾을 수 없습니다.");
                        if(hub.Attached(id)!=null)return Fail("수정하려면 캐릭터를 먼저 로그아웃하세요.");
                        if(request.AccountId is not {} owner||request.Gold>WorldSettings.MaxCarriedGold||!database.Accounts().Any(a=>a.Id==owner)||
                            !ValidItems(request.Items,WorldSettings.InventorySlots,true,current.WalletSlot)||
                            !ValidItems(request.BankItems,WorldBank.Slots,false,0))
                            return Fail("소유 계정, 갈리드, 슬롯, 수량과 내구도를 확인하세요.");
                        characters.EditManaged(id,owner,request.Gold,request.Items!,request.BankItems!);
                        return new {ok=true};
                    } finally { worldGate.Release(); }
                default:
                    return Fail("지원하지 않는 관리 요청입니다.");
            }
        }
    }

    static object Fail(string message) => new {ok=false,error=message};

    static bool ValidItems(ItemPlacement[]? items,int maxSlots,bool equipped,byte wallet)
    {
        if(items is null||items.Length>maxSlots+(equipped?32:0))return false;
        var slots=new HashSet<byte>();
        var gear=new HashSet<byte>();
        foreach(var item in items){
            if(item is null||item.Key is null||!EquipmentItems.HasKey(item.Key)||item.Key==WorldSettings.CurrencyKey||
                item.Quantity==0||item.InventorySlot>maxSlots||item.Durability>EquipmentItems.FullDurability)return false;
            var definition=EquipmentItems.Get(item.Key);
            if(definition.EquipmentSlot!=0&&item.Quantity!=1)return false;
            if(item.InventorySlot==0){
                if(!equipped||definition.EquipmentSlot==0||!gear.Add(definition.EquipmentSlot))return false;
            }else if(item.InventorySlot==wallet||!slots.Add(item.InventorySlot))return false;
        }
        var weapon=items.FirstOrDefault(i=>i.InventorySlot==0&&EquipmentItems.Get(i.Key).EquipmentSlot==1);
        return !gear.Contains(3)||weapon==null||!EquipmentItems.UsesBothHands(EquipmentItems.Get(weapon.Key));
    }
}
