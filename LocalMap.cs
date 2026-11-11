namespace ElanciaLocal;
public sealed class LocalMap {
    readonly byte[] bytes;
    public int Width=>bytes[2];public int Height=>bytes[3];
    public ushort ClientChecksum { get; }

    public LocalMap(string path)
    {
        bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || Width == 0 || Height == 0 || bytes.Length < 4 + 4 * Width * Height * 5)
            throw new InvalidDataException("Invalid map header");

        // 0047458A hashes only the four base tile layers. Its byte enters
        // after the table lookup, unlike standard CRC-16/CCITT updates.
        ushort checksum = 0;
        foreach (byte tileByte in bytes.AsSpan(4, 4 * Width * Height * 5))
            checksum = (ushort)((checksum << 8) ^ ChecksumTable[checksum >> 8] ^ tileByte);
        ClientChecksum = checksum;
    }

    static readonly ushort[] ChecksumTable = BuildChecksumTable();

    static ushort[] BuildChecksumTable()
    {
        var table = new ushort[256];
        for (int index = 0; index < table.Length; index++)
        {
            ushort entry = (ushort)(index << 8);
            for (int bit = 0; bit < 8; bit++)
                entry = (ushort)((entry << 1) ^ ((entry & 0x8000) != 0 ? 0x1021 : 0));
            table[index] = entry;
        }
        return table;
    }
    public bool ContainsTile(int x,int y,int floor)=>floor>=0&&floor<4&&x>=0&&y>=0&&x<Width&&y<Height;
    public bool CanWalk(int x,int y,int floor=0)=>Flags(x,y,floor)==0;
    // The client's 0047F1B9 water test checks the high bit. The confirmed
    // swimming tiles have no directional or stair flags set.
    internal bool CanSwim(int x,int y,int floor)=>Flags(x,y,floor)==0x80;
    internal bool IsOpenDungeonFloor(int x,int y,int floor)=>Flags(x,y,floor)==0x40;
    int TileIndex(int x,int y,int floor)=>4+(floor*Width*Height+y*Width+x)*5;
    ushort Terrain(int x,int y,int floor)=>ContainsTile(x,y,floor)
        ?System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(TileIndex(x,y,floor),2)):(ushort)0;
    internal int? PairedRolanciaDoor(int x,int y,int floor){
        // Both halves are one doorway when the threshold and its front tiles match.
        int row=Terrain(x,y,floor) is 900 or 901?y-1:y;
        int left=Terrain(x,row,floor) switch {867=>x,868=>x-1,_=>-1};
        if(left<0||!CanWalk(left,y,floor)||!CanWalk(left+1,y,floor))return null;
        if(Terrain(left,row,floor)!=867||Terrain(left+1,row,floor)!=868||
            Terrain(left,row+1,floor)!=900||Terrain(left+1,row+1,floor)!=901)return null;
        return x==left?left+1:left;
    }
    internal int? PairedIndoorExit(int x,int y,int floor){
        if(!CanWalk(x,y,floor)||!CanWalk(x,y-1,floor)||CanStand(x,y+1,floor))return null;
        int other=!CanStand(x-1,y,floor)?x+1:x-1;
        return CanWalk(other,y,floor)&&CanWalk(other,y-1,floor)&&!CanStand(other,y+1,floor)&&
            !CanStand(2*x-other,y,floor)&&!CanStand(2*other-x,y,floor)?other:null;
    }
    // The recorded sewer route crosses 0x40 tiles on the same floor.
    public bool CanStand(int x,int y,int floor){
        int flags=Flags(x,y,floor);
        return (flags&0x80)==0&&(flags&0x0f)!=0x0f;
    }
    public (int X,int Y,byte Floor)? FindSpawn(Func<int,int,byte,bool>? available=null){for(byte floor=0;floor<4;floor++)for(int y=1;y<Height-1;y++)for(int x=1;x<Width-1;x++)if(CanWalk(x,y,floor)&&CanWalk(x+1,y,floor)&&CanWalk(x,y+1,floor)&&(available==null||available(x,y,floor)))return(x,y,floor);return null;}
    // 00444A51 direction vector table.
    public static (int X,int Y) Delta(byte direction)=>direction switch{0=>(0,-1),1=>(1,0),2=>(0,1),3=>(-1,0),4=>(1,-1),5=>(1,1),6=>(-1,1),7=>(-1,-1),_=>(0,0)};

    internal bool ClearAttackPath(int x,int y,byte floor,int targetX,int targetY){
        int dx=Math.Abs(targetX-x),dy=Math.Abs(targetY-y);
        int horizontal=Math.Sign(targetX-x),vertical=Math.Sign(targetY-y);
        int error=dx-dy;
        while(x!=targetX||y!=targetY){
            int nextX=x,nextY=y,decision=2*error;
            if(decision> -dy){error-=dy;nextX+=horizontal;}
            if(decision<dx){error+=dx;nextY+=vertical;}
            byte direction=(nextX-x,nextY-y) switch {
                (0,-1)=>0,(1,0)=>1,(0,1)=>2,(-1,0)=>3,
                (1,-1)=>4,(1,1)=>5,(-1,1)=>6,_=>7
            };
            if(!TryStep(x,y,floor,direction,out var next,allowSwimming:true)||next.Floor!=floor)return false;
            x=nextX;y=nextY;
        }
        return true;
    }

    internal (int X,int Y)? GuildFlagTile(){
        (int,int)? found=null;
        // Recorded flags occupy the single recessed tile approached from the north.
        for(int y=1;y<Height-1;y++)for(int x=1;x<Width-1;x++){
            if(!CanWalk(x,y)||!CanWalk(x-1,y-1)||!CanWalk(x,y-1)||!CanWalk(x+1,y-1)||
                !CanWalk(x-1,y+1)||!CanWalk(x+1,y+1)||(Flags(x-1,y,0)&15)!=15||
                (Flags(x+1,y,0)&15)!=15||(Flags(x,y+1,0)&15)!=15)continue;
            if(found!=null)return null;
            found=(x,y);
        }
        return found;
    }

    // Native 0047AD5B checks entry masks, and accepts either route around a corner.
    // 004777B4 then applies the destination stair flag to the source floor.
    static readonly byte[] EntryMasks = [1, 8, 4, 2];
    int Flags(int x,int y,int floor) => ContainsTile(x,y,floor)
        ? bytes[TileIndex(x,y,floor)+4] : 0xff;

    public bool TryStep(int x,int y,byte floor,byte direction,out (int X,int Y,byte Floor) destination,bool allowSwimming=false){
        destination = (x,y,floor);
        if(direction>7)return false;
        var (dx,dy) = Delta(direction);
        int nx=x+dx, ny=y+dy;
        int target = Flags(nx,ny,floor);
        if(direction<4){
            if((target & EntryMasks[direction])!=0)return false;
        }else{
            int horizontal = EntryMasks[dx>0 ? 1 : 3];
            int vertical = EntryMasks[dy>0 ? 2 : 0];
            bool horizontalFirst = (Flags(nx,y,floor)&horizontal)==0 && (target&vertical)==0;
            bool verticalFirst = (Flags(x,ny,floor)&vertical)==0 && (target&horizontal)==0;
            if(!horizontalFirst && !verticalFirst)return false;
        }
        int nextFloor = floor + ((target&0x10)!=0 ? 1 : (target&0x20)!=0 ? -1 : 0);
        int destinationFlags=Flags(nx,ny,nextFloor);
        if((destinationFlags&0x80)!=0&&(!allowSwimming||destinationFlags!=0x80))return false;
        destination = (nx,ny,(byte)nextFloor);
        return true;
    }
}
