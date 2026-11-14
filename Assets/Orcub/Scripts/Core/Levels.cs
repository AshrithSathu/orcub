namespace Orcub.Core
{
    public static class Levels
    {
        /// <summary>
        /// Starter board: 10 gear-rings arranged as a hub with six spokes plus three outliers.
        /// Link a -> b means a holder welded on ring a clamps ring b, so ring a stays locked until b's gap frees it.
        /// Intended solve order: 8, 9, 4, 0, 5, 6, 3, 1, then 2 and 7.
        /// </summary>
        public static LevelDef Basic10()
        {
            var level = new LevelDef { Name = "Basic 10" };
            level.Rings.Add(new RingDef(0.0f, 0.0f, 1.4f, 0));    // 0 hub
            level.Rings.Add(new RingDef(0.0f, 3.6f, 1.2f, 1));    // 1 north
            level.Rings.Add(new RingDef(3.1f, 1.8f, 1.2f, 2));    // 2 north-east
            level.Rings.Add(new RingDef(3.1f, -1.8f, 1.2f, 3));   // 3 south-east
            level.Rings.Add(new RingDef(0.0f, -3.6f, 1.2f, 4));   // 4 south
            level.Rings.Add(new RingDef(-3.1f, -1.8f, 1.2f, 5));  // 5 south-west
            level.Rings.Add(new RingDef(-3.1f, 1.8f, 1.2f, 6));   // 6 north-west
            level.Rings.Add(new RingDef(0.0f, 6.6f, 1.0f, 7));    // 7 crown
            level.Rings.Add(new RingDef(-2.6f, -5.6f, 1.0f, 8));  // 8 left foot
            level.Rings.Add(new RingDef(2.6f, -5.6f, 1.0f, 9));   // 9 right foot

            level.Links.Add(new LinkDef(7, 1));
            level.Links.Add(new LinkDef(1, 0));
            level.Links.Add(new LinkDef(1, 6));
            level.Links.Add(new LinkDef(6, 5));
            level.Links.Add(new LinkDef(5, 0));
            level.Links.Add(new LinkDef(2, 1));
            level.Links.Add(new LinkDef(2, 3));
            level.Links.Add(new LinkDef(3, 0));
            level.Links.Add(new LinkDef(0, 4));
            level.Links.Add(new LinkDef(4, 8));
            level.Links.Add(new LinkDef(4, 9));
            return level;
        }
    }
}
