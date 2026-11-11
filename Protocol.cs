using System.Buffers.Binary;
using System.Text;

namespace ElanciaLocal;

// Reimplemented from this client's 004B4CFB / 004B5DB0 / 004B5E72.
// Chaos Crypto.cs was used as a comparison reference, not copied wholesale:
// this client has no MD5 trailer or generated per-packet key.
public sealed class LegacyCrypto(byte seed = 0, string key = "NexonInc.")
{
    private readonly byte[] keyBytes = Encoding.ASCII.GetBytes(key);
    public static bool ClientEncrypted(byte op) => op is not (0 or 0x10 or 0x48);
    public static bool ServerEncrypted(byte op) => op is not (0 or 3 or 0x40);
    public static byte Salt(int seed, int i) => unchecked((byte)(seed switch {
        0 => i, 1 => 128 + (i % 2 == 0 ? (i+1)/2 : -(i+1)/2),
        2 => 255-i, 3 => 128 + (i%2 == 0 ? (255-i)/2 : -(255-i)/2),
        4 => (i/16)*(i/16), 5 => 2*i, 6 => 255-((2*i)%256),
        7 => i<=127 ? 255-2*i : 2*i-256,
        8 => i<=127 ? 2*i : 511-2*i,
        9 => 255-(((i-128)/8)*((i-128)/8)%256),
        _ => throw new ArgumentOutOfRangeException(nameof(seed)) }));
    private void Transform(Span<byte> data, byte sequence)
    {
        if(seed>9 || keyBytes.Length is <1 or >12) throw new InvalidDataException("Invalid legacy crypto parameters");
        for(int i=0;i<data.Length;i++) {
            int block=(i/keyBytes.Length)&255;
            data[i]^=(byte)(keyBytes[i%keyBytes.Length] ^ Salt(seed,sequence));
            if(block!=sequence)data[i]^=Salt(seed,block);
        }
    }
    public byte[] Encode(ReadOnlySpan<byte> plain, byte sequence, bool fromServer)
    {
        if(plain.IsEmpty)throw new InvalidDataException("Empty packet");
        bool enc=fromServer?ServerEncrypted(plain[0]):ClientEncrypted(plain[0]);
        if(!enc)return plain.ToArray();
        byte[] result=new byte[plain.Length+1];result[0]=plain[0];result[1]=sequence;
        plain[1..].CopyTo(result.AsSpan(2));Transform(result.AsSpan(2),sequence);return result;
    }
    public byte[] Decode(ReadOnlySpan<byte> wire, bool fromServer)
    {
        if(wire.IsEmpty)throw new InvalidDataException("Empty packet");
        bool enc=fromServer?ServerEncrypted(wire[0]):ClientEncrypted(wire[0]);
        if(!enc)return wire.ToArray();
        if(wire.Length<2)throw new InvalidDataException("Missing sequence");
        byte[] result=new byte[wire.Length-1];result[0]=wire[0];
        wire[2..].CopyTo(result.AsSpan(1));Transform(result.AsSpan(1),wire[1]);return result;
    }
}

public static class Framing
{
    public const int MaxBody=65535;
    public static byte[] Wrap(ReadOnlySpan<byte> body) {
        if(body.Length is <1 or >MaxBody)throw new InvalidDataException("Packet length outside limits");
        byte[] r=new byte[body.Length+3];r[0]=0xAA;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(1),(ushort)body.Length);body.CopyTo(r.AsSpan(3));return r;
    }
    public static async Task<byte[]?> ReadAsync(Stream stream,CancellationToken ct) {
        byte[] header=new byte[3];int n=await stream.ReadAsync(header.AsMemory(0,1),ct);if(n==0)return null;
        await stream.ReadExactlyAsync(header.AsMemory(1),ct);
        if(header[0]!=0xAA)throw new InvalidDataException("Invalid AA signature");
        int size=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if(size is <1 or >MaxBody)throw new InvalidDataException("Packet length outside limits");
        byte[] body=new byte[size];await stream.ReadExactlyAsync(body,ct);return body;
    }
}

public sealed class PacketWriter(byte op)
{
    private readonly List<byte> data=[op];
    public PacketWriter U8(int v){data.Add(checked((byte)v));return this;}
    public PacketWriter U16(int v){U8((v>>8)&255);U8(v&255);return this;}
    public PacketWriter U32(uint v){U16((int)(v>>16));U16((int)(v&65535));return this;}
    public PacketWriter Bytes(ReadOnlySpan<byte> b){data.AddRange(b);return this;}
    public PacketWriter Str8(string s){var b=Encoding.GetEncoding(949).GetBytes(s);return U8(b.Length).Bytes(b);}
    public PacketWriter Str16(string s){var b=Encoding.GetEncoding(949).GetBytes(s);return U16(b.Length).Bytes(b);}
    public byte[] Build()=>data.ToArray();
}
