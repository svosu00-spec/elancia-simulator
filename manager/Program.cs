using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ElanciaAccountManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        int port = args is ["--port", var value] && int.TryParse(value, out int selected) ? selected : 7500;
        Application.Run(new ManagerForm(port));
    }
}

sealed class ManagerForm : Form
{
    sealed record Choice(string Key, string Label);
    readonly string pipeName;
    readonly ListBox accounts = new() { Dock=DockStyle.Fill, DisplayMember=nameof(Choice.Label) };
    readonly ListBox characters = new() { Dock=DockStyle.Fill, DisplayMember=nameof(Choice.Label) };
    readonly TextBox search = new() { Width=200, PlaceholderText="아이디 또는 캐릭터 이름" };
    readonly TextBox login = new() { Width=155, MaxLength=24 };
    readonly TextBox password = new() { Width=180, MaxLength=128, UseSystemPasswordChar=true, PlaceholderText="영문·숫자 4자 이상" };
    readonly ComboBox owner = new() { Width=175, DropDownStyle=ComboBoxStyle.DropDownList, DisplayMember=nameof(Choice.Label), ValueMember=nameof(Choice.Key) };
    readonly NumericUpDown gold = new() { Maximum=uint.MaxValue, Width=118, ThousandsSeparator=true };
    readonly Label location = new() { AutoSize=true };
    readonly Label status = new() { AutoSize=true, Padding=new Padding(8,5,0,0) };
    readonly Button saveAccount = new() { Text="계정 저장", AutoSize=true };
    readonly Button saveCharacter = new() { Text="캐릭터 저장", AutoSize=true };
    readonly DataGridView bag = ItemsGrid();
    readonly DataGridView bank = ItemsGrid();
    JsonArray allAccounts = new(), allCharacters = new();
    Choice[] catalog = [];
    bool filling;
    bool busy;

    public ManagerForm(int port)
    {
        pipeName = $"Elancia.AccountManager.{port}";
        Text = "일랜시아 계정 관리기";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 580);
        ClientSize = new Size(1120, 690);
        Font = new Font("맑은 고딕", 9);
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=3, Padding=new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
        Controls.Add(layout);
        var refresh = new Button { Text="새로고침", AutoSize=true };
        refresh.Click += async (_,_) => await Run(LoadState);
        layout.Controls.Add(Line(new Label { Text="검색", AutoSize=true, Padding=new Padding(0,7,0,0) },search,refresh),0,0);
        layout.Controls.Add(status,0,2);

        var split = new SplitContainer { Dock=DockStyle.Fill, Width=1050, SplitterDistance=245, FixedPanel=FixedPanel.Panel1 };
        layout.Controls.Add(split,0,1);
        var lists = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=4, Padding=new Padding(0,0,7,0) };
        lists.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent,48));
        lists.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent,52));
        lists.Controls.Add(new Label { Text="계정", AutoSize=true },0,0);
        lists.Controls.Add(accounts,0,1);
        lists.Controls.Add(new Label { Text="캐릭터", AutoSize=true, Padding=new Padding(0,4,0,0) },0,2);
        lists.Controls.Add(characters,0,3);
        split.Panel1.Controls.Add(lists);

        var details = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=4, Padding=new Padding(7,0,0,0) };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
        details.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        details.Controls.Add(Line(Caption("아이디"),login,Caption("새 비밀번호"),password,saveAccount),0,0);
        details.Controls.Add(Line(Caption("소유 계정"),owner,Caption("갈리드"),gold,saveCharacter),0,1);
        details.Controls.Add(location,0,2);
        var tabs = new TabControl { Dock=DockStyle.Fill };
        tabs.TabPages.Add(ItemsTab("인벤토리 · 장비",bag));
        tabs.TabPages.Add(ItemsTab("은행",bank));
        details.Controls.Add(tabs,0,3);
        split.Panel2.Controls.Add(details);

        search.TextChanged += (_,_) => RebuildLists();
        accounts.SelectedIndexChanged += (_,_) => { if(!filling){ShowAccount();RebuildCharacters();} };
        characters.SelectedIndexChanged += (_,_) => { if(!filling)ShowCharacter(); };
        saveAccount.Click += async (_,_) => await Run(SaveAccount);
        saveCharacter.Click += async (_,_) => await Run(SaveCharacter);
        Shown += async (_,_) => await Run(LoadState);
    }

    static Label Caption(string text) => new() { Text=text, AutoSize=true, Padding=new Padding(0,6,0,0) };

    static FlowLayoutPanel Line(params Control[] children)
    {
        var line = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false };
        line.Controls.AddRange(children);
        return line;
    }

    static DataGridView ItemsGrid()
    {
        var grid = new DataGridView {
            Dock=DockStyle.Fill, RowHeadersVisible=false, AllowUserToAddRows=false, AllowUserToDeleteRows=false,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect, MultiSelect=false, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add(new DataGridViewComboBoxColumn {
            Name="key", HeaderText="아이템", DisplayMember=nameof(Choice.Label), ValueMember=nameof(Choice.Key), FillWeight=250
        });
        grid.Columns.Add("slot","슬롯");
        grid.Columns.Add("quantity","수량");
        grid.Columns.Add("durability","내구도");
        return grid;
    }

    static TabPage ItemsTab(string label, DataGridView grid)
    {
        var tab = new TabPage(label);
        var table = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2 };
        table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
        table.Controls.Add(grid,0,0);
        var add = new Button { Text="아이템 추가", AutoSize=true };
        var remove = new Button { Text="선택 삭제", AutoSize=true };
        add.Click += (_,_) => {
            if(grid.ReadOnly)return;
            int index=grid.Rows.Add();
            grid.Rows[index].Cells[1].Value=1;
            grid.Rows[index].Cells[2].Value=1;
            grid.Rows[index].Tag=new JsonObject();
            grid.CurrentCell=grid.Rows[index].Cells[0];
            grid.BeginEdit(true);
        };
        remove.Click += (_,_) => {if(!grid.ReadOnly&&grid.CurrentRow is {} row)grid.Rows.Remove(row);};
        table.Controls.Add(Line(add,remove,Caption("슬롯 0 = 착용 장비 · 내구도 빈칸 = 기본값")),0,1);
        tab.Controls.Add(table);
        return tab;
    }

    async Task Run(Func<Task> action)
    {
        if(busy)return;
        busy=true;
        UseWaitCursor=true;
        try { await action(); }
        catch(Exception error) when(error is IOException or TimeoutException or OperationCanceledException or
            InvalidDataException or FormatException or JsonException or InvalidOperationException) {
            status.Text=error is TimeoutException or OperationCanceledException
                ? "계정 관리 연결에 실패했습니다. 같은 Windows 계정에서 공개 서버가 실행 중인지 확인하세요."
                : error.Message;
        } finally { busy=false;UseWaitCursor=false; }
    }

    async Task<JsonObject> Request(object request)
    {
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct=time.Token;
        await using var pipe = new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] header=new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
        try {
            await pipe.WriteAsync(header,ct);
            await pipe.WriteAsync(payload,ct);
        } finally { CryptographicOperations.ZeroMemory(payload); }
        await pipe.ReadExactlyAsync(header,ct);
        int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <1 or >64*1024*1024)throw new InvalidDataException("계정 관리 응답의 길이가 올바르지 않습니다.");
        byte[] response=new byte[length];
        await pipe.ReadExactlyAsync(response,ct);
        var result=JsonNode.Parse(response) as JsonObject??throw new InvalidDataException("계정 관리 응답을 읽을 수 없습니다.");
        if(result["ok"]?.GetValue<bool>()!=true)
            throw new InvalidDataException(result["error"]?.GetValue<string>()??"요청을 처리할 수 없습니다.");
        return result;
    }

    static string Value(JsonNode? row,string key) => row?[key]?.GetValue<string>()??"";
    static string? Selected(ListBox list) => (list.SelectedItem as Choice)?.Key;
    static void Fill(ListBox list, IEnumerable<Choice> values, string? selection)
    {
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange(values.Cast<object>().ToArray());
        for(int index=0;index<list.Items.Count;index++)
            if(list.Items[index] is Choice entry&&entry.Key==selection){list.SelectedIndex=index;break;}
        if(list.SelectedIndex<0&&list.Items.Count>0)list.SelectedIndex=0;
        list.EndUpdate();
    }

    async Task LoadState()
    {
        var data=await Request(new {action="state"});
        allAccounts=data["accounts"]!.AsArray();
        allCharacters=data["characters"]!.AsArray();
        catalog=data["catalog"]!.AsArray().Select(item=>new Choice(Value(item,"key"),$"{Value(item,"name")}  [{Value(item,"key")}]"))
            .OrderBy(item=>item.Label).ToArray();
        foreach(var grid in new[]{bag,bank}){
            grid.Rows.Clear();
            ((DataGridViewComboBoxColumn)grid.Columns[0]).DataSource=catalog;
        }
        owner.DataSource=allAccounts.Select(row=>new Choice(Value(row,"id"),Value(row,"login"))).ToArray();
        RebuildLists();
        status.Text=$"계정 {allAccounts.Count}개 · 캐릭터 {allCharacters.Count}개";
    }

    bool Matches(string value) => value.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase);

    void RebuildLists()
    {
        var choices=new List<Choice>{new("","전체 계정")};
        choices.AddRange(allAccounts.Where(row=>Matches(Value(row,"login"))||
            allCharacters.Any(character=>Value(character,"account")==Value(row,"id")&&Matches(Value(character,"name"))))
            .Select(row=>new Choice(Value(row,"id"),Value(row,"login")+(row?["enabled"]?.GetValue<bool>()==true?"":"  (비밀번호 설정 필요)"))));
        filling=true;
        Fill(accounts,choices,Selected(accounts));
        filling=false;
        ShowAccount();
        RebuildCharacters();
    }

    void RebuildCharacters()
    {
        string? account=Selected(accounts);
        bool ownerMatches=Matches(Value(allAccounts.FirstOrDefault(row=>Value(row,"id")==account),"login"));
        var choices=allCharacters.Where(row=>(string.IsNullOrEmpty(account)||Value(row,"account")==account)&&
            (ownerMatches||Matches(Value(row,"name"))||Matches(Value(allAccounts.FirstOrDefault(a=>Value(a,"id")==Value(row,"account")),"login"))))
            .Select(row=>new Choice(row!["id"]!.ToString(),Value(row,"name"))).ToArray();
        filling=true;
        Fill(characters,choices,Selected(characters));
        filling=false;
        ShowCharacter();
    }

    void ShowAccount()
    {
        string? id=Selected(accounts);
        var row=allAccounts.FirstOrDefault(entry=>Value(entry,"id")==id);
        login.Text=Value(row,"login");
        password.Clear();
        saveAccount.Enabled=login.Enabled=password.Enabled=row!=null;
    }

    void ShowCharacter()
    {
        string? id=Selected(characters);
        var row=allCharacters.FirstOrDefault(entry=>entry?["id"]?.ToString()==id);
        bool editable=row!=null&&row["online"]!.GetValue<bool>()==false;
        owner.SelectedValue=Value(row,"account");
        gold.Value=row?["gold"]?.GetValue<uint>()??0;
        location.Text=row==null?"캐릭터를 선택하세요.":$"{Value(row,"name")} · 맵 {row["map"]} · ({row["x"]}, {row["y"]}) · 층 {row["floor"]} · {(editable?"로그아웃":"접속 중")}";
        FillItems(bag,row?["items"] as JsonArray);
        FillItems(bank,row?["bankItems"] as JsonArray);
        saveCharacter.Enabled=owner.Enabled=gold.Enabled=editable;
        bag.ReadOnly=bank.ReadOnly=!editable;
    }

    static void FillItems(DataGridView grid,JsonArray? items)
    {
        grid.Rows.Clear();
        if(items==null)return;
        var available=((DataGridViewComboBoxColumn)grid.Columns[0]).DataSource as Choice[]??[];
        foreach(var item in items.OfType<JsonObject>()){
            string key=Value(item,"key");
            int index=grid.Rows.Add();
            var row=grid.Rows[index];
            row.Cells[1].Value=item["inventorySlot"]?.ToString();
            row.Cells[2].Value=item["quantity"]?.ToString();
            row.Cells[3].Value=item["durability"]?.ToString();
            row.Tag=item.DeepClone();
            if(!available.Any(choice=>choice.Key==key)&&row.Cells[0] is DataGridViewComboBoxCell combo)
                combo.DataSource=available.Append(new Choice(key,key)).ToArray();
            row.Cells[0].Value=key;
        }
    }

    static JsonArray ReadItems(DataGridView grid)
    {
        grid.EndEdit();
        var items=new JsonArray();
        foreach(DataGridViewRow row in grid.Rows){
            if(row.Cells[0].Value is not string key||!byte.TryParse(Convert.ToString(row.Cells[1].Value),out byte slot)||
                !uint.TryParse(Convert.ToString(row.Cells[2].Value),out uint quantity)||quantity==0)
                throw new InvalidDataException("아이템, 슬롯과 수량을 확인하세요.");
            var item=(JsonObject)((JsonObject)row.Tag!).DeepClone();
            item["key"]=key;
            item["inventorySlot"]=slot;
            item["quantity"]=quantity;
            string? durability=Convert.ToString(row.Cells[3].Value);
            if(string.IsNullOrWhiteSpace(durability))item["durability"]=null;
            else if(ushort.TryParse(durability,out ushort value))item["durability"]=value;
            else throw new InvalidDataException("내구도는 숫자로 입력하세요.");
            items.Add(item);
        }
        return items;
    }

    async Task SaveAccount()
    {
        string? newPassword=password.Text.Length==0?null:password.Text;
        password.Clear();
        await Request(new {action="account",accountId=Selected(accounts),login=login.Text,password=newPassword});
        await LoadState();
        status.Text="계정 정보를 저장했습니다.";
    }

    async Task SaveCharacter()
    {
        if(!uint.TryParse(Selected(characters),out uint id)||owner.SelectedValue is not string ownerId)return;
        await Request(new {action="character",characterId=id,accountId=ownerId,gold=(uint)gold.Value,
            items=ReadItems(bag),bankItems=ReadItems(bank)});
        await LoadState();
        status.Text="캐릭터와 아이템을 저장했습니다.";
    }
}
