using System;
namespace Orcub.Core
{
    public sealed class InfiniteChapter
    {
        static readonly string[] Adjectives = { "Amber", "Quiet", "Twilight", "Hidden", "Silver", "Golden", "Winding", "Velvet", "Moonlit", "Jade", "Misty", "Coral" };
        static readonly string[] Places = { "Grove", "Harbour", "Garden", "Valley", "Isles", "Meadow", "Canopy", "Lagoon", "Summit", "Orchard", "Hollow", "Oasis", "Falls" };
        static readonly string[] CuratedNames = { "The Workshop", "Clockworks", "Tool shed", "Music box", "Copper coast", "Tinker town", "Ratchet ridge", "Cog canyon", "Brass bay", "The grand mechanism" };
        public int Chapter { get; }
        public string Name { get; }
        public string[] Mechanics { get; }
        public bool Curated => Chapter < 10;
        public int FirstIndex => Chapter * 20;
        public long LastIndex => (long)Chapter * 20 + 19;
        public bool IsBoss(int index) => index / 20 == Chapter && index % 20 == 19;
        public InfiniteChapter(int chapter)
        {
            if (chapter < 0 || chapter > (int.MaxValue - 1) / 20) throw new ArgumentOutOfRangeException(nameof(chapter));
            Chapter = chapter;
            var rng = new LevelPcg((uint)chapter);
            Name = Curated ? CuratedNames[chapter] : Adjectives[rng.Range(Adjectives.Length)] + " " + Places[rng.Range(Places.Length)];
            var mechanics = new System.Collections.Generic.List<string>();
            if (Curated)
            {
                for (int i = FirstIndex; i <= LastIndex; i++)
                    foreach (var data in LevelLibrary.GetVariants(i))
                        foreach (string name in data.mechanics ?? Array.Empty<string>())
                            if (!mechanics.Contains(name)) mechanics.Add(name);
            }
            else mechanics.Add(InfiniteGenerator.MechanicNames[(chapter - 10) % 7]);
            Mechanics = mechanics.ToArray();
        }
    }
}
