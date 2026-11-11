namespace ElanciaLocal;

internal sealed class CalasMusicLesson(uint npcId){
    internal const string HarpKey="recorded-8017-bfecb5e7c7cfc7c1";
    internal const ushort MinimumMastery=500;
    internal const int NoteIntervalMs=1100;
    internal static readonly string[] Notes=["도","레","미","파","솔","라","시"];
    internal static readonly ushort[] NoteSounds=[86,87,88,89,90,91,92];
    internal static readonly string[] Chords=["도미솔","파라도","솔시라"];
    internal static readonly ushort[] ChordSounds=[71,100,101];

    internal uint NpcId {get;}=npcId;
    internal CalasStage Stage {get;set;}
    internal int Note {get;}=Random.Shared.Next(Notes.Length);
    internal int Chord {get;}=Random.Shared.Next(Chords.Length);

    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==205&&npc.Name=="칼라스"&&npc.Sprite==0x4077;
}

internal enum CalasStage { Intro, Scale, Note, Chord }
