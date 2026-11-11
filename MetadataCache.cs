using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
namespace ElanciaLocal;
public sealed class MetadataCache {
    public sealed record Entry(string Name,uint Crc,byte[] Compressed);
    internal sealed record ItemDefaults(ushort MaximumDurability,ushort AttackMinimum,ushort AttackMaximum);
    readonly Dictionary<string,Entry> entries;
    public MetadataCache(string path,string? overlayPath=null) {
        entries=Read(path);
        if(overlayPath!=null&&File.Exists(overlayPath)){
            foreach(var (name,entry) in Read(overlayPath)){
                if(!entries.ContainsKey(name))throw new InvalidDataException($"Unknown metadata overlay entry: {name}");
                entries[name]=entry;
            }
        }
    }
    static Dictionary<string,Entry> Read(string path)=>Read(File.ReadAllBytes(path));
    static Dictionary<string,Entry> Read(byte[] file) {
        var entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        if(file.Length<8)throw new InvalidDataException("Truncated Meta.dat");
        int count=BinaryPrimitives.ReadInt32LittleEndian(file);int indexSize=BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        if(count is <0 or >4096||indexSize<0||indexSize>file.Length-8)throw new InvalidDataException("Invalid metadata index");
        int index=8,data=8+indexSize;
        for(int i=0;i<count;i++) {
            if(index>=8+indexSize)throw new InvalidDataException("Truncated metadata index");
            int n=file[index++];if(index+n*2+4>8+indexSize)throw new InvalidDataException("Invalid metadata name");
            string name=Encoding.Unicode.GetString(file,index,n*2);index+=n*2;
            int size=checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(index)));index+=4;
            if(size<1||size>60000||data+size>file.Length)throw new InvalidDataException("Invalid metadata body");
            byte[] compressed=file.AsSpan(data,size).ToArray();data+=size;
            using var z=new ZLibStream(new MemoryStream(compressed),CompressionMode.Decompress);
            using var raw=new MemoryStream();byte[] buffer=new byte[4096];int read;
            while((read=z.Read(buffer))>0){if(raw.Length+read>1024*1024)throw new InvalidDataException("Oversized metadata");raw.Write(buffer,0,read);}
            entries.Add(name,new Entry(name,Crc32(raw.ToArray()),compressed));
        }
        if(index!=8+indexSize||data!=file.Length)throw new InvalidDataException("Trailing metadata bytes");
        return entries;
    }
    internal static IReadOnlyDictionary<string,ItemDefaults> NativeItemDefaults(){
        using var metadata=typeof(MetadataCache).Assembly.GetManifestResourceStream("ElanciaLocal.NativeMeta.dat");
        if(metadata==null){
            var client=Environment.GetEnvironmentVariable("ELANCIA_CLIENT_DIR")??@"C:\Nexon\Elancia";
            return ReadItemDefaults(Read(Path.Combine(client,"Profiles","Meta.dat")).Values);
        }
        using var bytes=new MemoryStream();
        metadata.CopyTo(bytes);
        return ReadItemDefaults(Read(bytes.ToArray()).Values);
    }
    internal IReadOnlyDictionary<string,ItemDefaults> ItemData()=>ReadItemDefaults(entries.Values);
    static IReadOnlyDictionary<string,ItemDefaults> ReadItemDefaults(IEnumerable<Entry> entries){
        var result=new Dictionary<string,ItemDefaults>(StringComparer.Ordinal);
        var conflicts=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in entries){
            const string prefix="CharicInfo";
            if(!entry.Name.StartsWith(prefix,StringComparison.Ordinal)||
                !int.TryParse(entry.Name.AsSpan(prefix.Length),out _))continue;
            using var compressed=new ZLibStream(new MemoryStream(entry.Compressed),CompressionMode.Decompress);
            using var raw=new MemoryStream();
            compressed.CopyTo(raw);
            var reader=new PacketReader(raw.ToArray());
            int count=reader.U16();
            for(int row=0;row<count;row++){
                string name=reader.Str8();
                int fields=reader.U16();
                ushort maximum=0;
                string? attack=null;
                for(int field=0;field<fields;field++){
                    string value=reader.Str16();
                    if(field==4)ushort.TryParse(value,out maximum);
                    if(field==20)attack=value;
                }
                if(maximum==0)continue;
                ushort minimumAttack=0,maximumAttack=0;
                if(attack?.Split('m') is [var minimumText,var maximumText]&&
                    ushort.TryParse(minimumText,out ushort parsedMinimum)&&ushort.TryParse(maximumText,out ushort parsedMaximum)&&
                    parsedMinimum<=parsedMaximum){
                    minimumAttack=parsedMinimum;maximumAttack=parsedMaximum;
                }
                var defaults=new ItemDefaults(maximum,minimumAttack,maximumAttack);
                if(!result.TryAdd(name,defaults)&&result[name]!=defaults)conflicts.Add(name);
            }
            reader.End();
        }
        foreach(string name in conflicts)result.Remove(name);
        return result;
    }
    public int Count=>entries.Count;
    internal IReadOnlyDictionary<string,byte> EmotionCommands(){
        if(!entries.TryGetValue("Emotions",out var entry))return new Dictionary<string,byte>();
        using var compressed=new ZLibStream(new MemoryStream(entry.Compressed),CompressionMode.Decompress);
        using var raw=new MemoryStream();
        compressed.CopyTo(raw);
        var reader=new PacketReader(raw.ToArray());
        var commands=new Dictionary<string,byte>(StringComparer.Ordinal);
        int count=reader.U16();
        for(int i=0;i<count;i++){
            string command=reader.Str8();
            int fields=reader.U16();
            if(fields!=1||!byte.TryParse(reader.Str16(),System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,out byte motion)||!commands.TryAdd(command,motion))
                throw new InvalidDataException("Invalid emotion command metadata");
        }
        reader.End();
        return commands;
    }
    public byte[] List(){var p=new PacketWriter(0x6F).U8(1).U16(entries.Count);foreach(var e in entries.Values)p.Str8(e.Name).U32(e.Crc);return p.Build();}
    public byte[]? FilePacket(string name)=>entries.TryGetValue(name,out var e)?new PacketWriter(0x6F).U8(0).Str8(e.Name).U32(e.Crc).U16(e.Compressed.Length).Bytes(e.Compressed).Build():null;
    public static uint Crc32(ReadOnlySpan<byte> data){uint crc=0xFFFFFFFF;foreach(byte b in data){crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xEDB88320u:0);}return ~crc;}
}
