using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// The Feast of the Winter Star "secret friend" (the NPC the player
    /// has to bring a gift for), and the mugshot crop of them that
    /// vanilla draws in the bottom-right corner of the Skills page —
    /// served at <c>GET /secret-friend</c>.
    ///
    /// Mirrors the decompiled 1.6 <c>StardewValley.Menus.SkillsPage.draw</c>
    /// exactly (read before writing): the recipient is only shown while
    /// <c>Game1.IsWinter</c>, the player has read that year's invitation
    /// letter (<c>mailReceived</c> contains <c>"sawSecretSanta" + year</c>,
    /// added by <c>LetterViewerMenu</c>), and it's winter 18–24 or winter
    /// 25 before 3pm. The NPC is <c>Utility.GetRandomWinterStarParticipant()</c>
    /// — deterministic for a given save/year/player, since it seeds its
    /// RNG from <c>Game1.uniqueIDForThisGame / 2</c>, the year and the
    /// player's multiplayer ID — so it's resolved once per year here
    /// rather than every tick (it runs a GameStateQuery per NPC). The
    /// sprite comes from <c>Characters\{Name}_Winter</c>, falling back to
    /// the NPC's regular sprite sheet, cropped at
    /// <c>getMugShotSourceRect()</c> with 5px trimmed off the bottom —
    /// the same crop the Skills page draws.
    /// </summary>
    internal static class SecretFriendCache
    {
        private static readonly object Lock = new();
        private static int _resolvedYear = -1;
        private static string? _name;
        private static string? _displayName;
        private static byte[]? _png;

        /// <summary>The cached mugshot PNG for the current recipient, or null when there's no recipient to show right now. Safe to call from any thread.</summary>
        public static byte[]? TryGet()
        {
            lock (Lock)
                return _png;
        }

        /// <summary>Returns the recipient's display name when vanilla's Skills page would show it right now (see class doc comment), cache-warming its mugshot first; null otherwise. Main-thread only (touches the graphics device).</summary>
        public static string? Refresh(Farmer player, GraphicsDevice device)
        {
            bool visible = Game1.IsWinter
                && player.mailReceived.Contains("sawSecretSanta" + Game1.year)
                && ((Game1.dayOfMonth >= 18 && Game1.dayOfMonth < 25) || (Game1.dayOfMonth == 25 && Game1.timeOfDay < 1500));
            if (!visible)
                return null;

            if (_resolvedYear == Game1.year && _name is not null)
                return _displayName;

            NPC? npc = Utility.GetRandomWinterStarParticipant();
            if (npc is null)
                return null;

            Texture2D texture;
            try
            {
                texture = Game1.content.Load<Texture2D>("Characters\\" + npc.Name + "_Winter");
            }
            catch
            {
                texture = npc.Sprite.Texture;
            }

            Rectangle src = npc.getMugShotSourceRect();
            src.Height -= 5;

            var pixels = new Color[src.Width * src.Height];
            texture.GetData(0, src, pixels, 0, pixels.Length);
            using Texture2D cropped = new(device, src.Width, src.Height);
            cropped.SetData(pixels);
            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, src.Width, src.Height);

            lock (Lock)
            {
                _resolvedYear = Game1.year;
                _name = npc.Name;
                _displayName = npc.displayName;
                _png = ms.ToArray();
            }
            return _displayName;
        }
    }
}
