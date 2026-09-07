using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Characters;

namespace StardewDS
{
    /// <summary>
    /// Crops a single-frame portrait PNG for a farm animal *breed*
    /// straight out of the game's own loaded animal spritesheet, and
    /// caches it by <see cref="FarmAnimal.type"/> (e.g. "White Chicken",
    /// "Dairy Cow") for <see cref="CompanionServer"/>'s
    /// `GET /animal-sprite` to serve — the Animals screen's per-row
    /// portrait.
    ///
    /// Keyed by breed rather than by individual animal: every animal of
    /// the same type shares the exact same texture and crop rect (only
    /// coat-color variants get their own `type` string, e.g. "White
    /// Chicken" vs "Brown Chicken", so this still gives each visually
    /// distinct breed its own icon) — same one-crop-per-visual-identity
    /// approach <see cref="SpriteCache"/> already uses for items (keyed
    /// by qualified item id, not by inventory slot).
    ///
    /// Both the <see cref="FarmAnimal"/> crop and the <see cref="Pet"/>
    /// crop (<see cref="EnsureCachedForPet"/>) now come straight out of
    /// the real, decompiled <c>StardewValley.Menus.AnimalPage</c> —
    /// vanilla 1.6's own "Animals" `GameMenu` page, which turns out to
    /// be almost exactly what this app's Animals screen is reproducing
    /// (see <c>UiIconCache</c>'s class doc comment for the fuller story
    /// of finding it, and the project README's Animals risk-area note
    /// for this crop's own multi-attempt history before it). Its
    /// <c>AnimalEntry</c> constructor computes each row's portrait rect
    /// itself, and neither formula is a simple "frame (0, 0)" idle-pose
    /// crop the way earlier rounds here assumed:
    /// - <b>FarmAnimal</b>: tall breeds (cows/pigs/sheep/goats/etc. —
    ///   <c>Sprite.SourceRect.Height &gt; 16</c>) crop
    ///   <c>Rectangle(0, SourceRect.Height * 2 - 28, SourceRect.Width, 28)</c>,
    ///   except Ostrich specifically, which uses <c>* 2 - 32</c> instead
    ///   of <c>* 2 - 28</c> (a taller crop for its taller portrait
    ///   frame). Short breeds (chickens/ducks/rabbits —
    ///   <c>Height &lt;= 16</c>) instead crop the fixed
    ///   <c>Rectangle(0, 16, 16, 16)</c> — the second of the texture's
    ///   two 16px rows, not the first.
    /// - <b>Pet</b>: always
    ///   <c>Rectangle(0, SourceRect.Height * 2 - 24, SourceRect.Width, 24)</c>
    ///   — see <see cref="EnsureCachedForPet"/>'s doc comment for why
    ///   this superseded a wiki-sourced "frame 0 is idle" guess that
    ///   looked plausible but wasn't what this specific menu draws.
    ///
    /// The Animals nav tab's own icon no longer lives here — it used to
    /// be a raw "White Chicken" crop cached by <c>EnsureTabIconCached</c>
    /// (removed in an earlier round), since replaced by the real vanilla
    /// <c>GameMenu</c> "animals" tab icon vanilla 1.6 itself added,
    /// which lives in <c>UiIconCache</c> instead (a fixed UI crop, not
    /// tied to any live animal, so it fits that class's existing
    /// pattern better than this one).
    /// </summary>
    internal static class AnimalIconCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached PNG bytes for the breed named <paramref name="type"/> (e.g. "White Chicken"), or null if it hasn't been cropped yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet(string type) =>
            Cache.TryGetValue(type, out byte[]? bytes) ? bytes : null;

        /// <summary>
        /// Crops and caches <paramref name="animal"/>'s breed portrait if
        /// its <see cref="FarmAnimal.type"/> isn't already cached. A
        /// no-op otherwise, so it's cheap to call once per farm animal
        /// every tick — main-thread only (touches the graphics device),
        /// same constraint as <see cref="SpriteCache.EnsureCached"/>.
        ///
        /// The crop rect is <c>AnimalPage</c>'s own real
        /// <c>AnimalEntry</c> formula (see this class's doc comment) —
        /// not a hardcoded/guessed "idle frame" crop.
        /// </summary>
        public static void EnsureCached(FarmAnimal animal, GraphicsDevice device)
        {
            string type = animal.type.Value;
            if (string.IsNullOrEmpty(type))
                return;

            Texture2D? sourceTexture = animal.Sprite?.Texture;
            if (sourceTexture is null)
                return;

            Rectangle spriteRect = animal.Sprite!.SourceRect;
            if (spriteRect.Width <= 0 || spriteRect.Height <= 0)
                return;

            Rectangle cropRect = spriteRect.Height > 16
                ? new Rectangle(0, spriteRect.Height * 2 - (type == "Ostrich" ? 32 : 28), spriteRect.Width, 28)
                : new Rectangle(0, 16, 16, 16);

            CropAndCache(type, sourceTexture, cropRect, device);
        }

        /// <summary>
        /// A cache key for <paramref name="pet"/>'s breed portrait —
        /// shared between <see cref="EnsureCachedForPet"/> (to crop and
        /// cache it) and <see cref="GameStateSnapshot.CollectPets"/> (to
        /// report the same string as the DTO's <c>Type</c>, so
        /// `GET /animal-sprite?type=` resolves it), so the two can't
        /// drift out of sync with each other.
        ///
        /// CORRECTED twice now from the version that first shipped, both
        /// times by a real build/run against the actual installed 1.6
        /// game rather than caught here: (1) an initial pass was
        /// written against decompiled *1.5.6* source, where
        /// <c>Pet.whichBreed</c> was a plain <c>NetInt</c> and
        /// <c>Cat</c>/<c>Dog</c> were real subclasses to type-check
        /// against — a `dotnet build` against 1.6 failed with CS0029
        /// (<c>Cat</c> is now <c>[Obsolete]</c>, and
        /// <c>Pet.whichBreed</c> is a <c>NetString</c>). (2) the
        /// follow-up fix kept deriving the source *texture* from
        /// <c>pet.Sprite</c>'s frame (0, 0), same as a FarmAnimal — but
        /// unlike FarmAnimal (where the real crop rect is now confirmed
        /// via <c>AnimalPage</c>, see <see cref="EnsureCachedForPet"/>'s
        /// doc comment), nothing confirmed frame 0 was a pet's portrait
        /// pose, and it turned out not to be. This method only derives
        /// the *cache key* now, independent of that history: real,
        /// confirmed 1.6 fields — <c>petType.Value</c> ("Cat"/"Dog", or
        /// a modded pet type ID) plus <c>whichBreed.Value</c> when it
        /// isn't the default "0" — e.g. "Cat", "Dog-1" — collision-free
        /// against every vanilla <see cref="FarmAnimal.type"/> string
        /// (none of those are "Cat"/"Dog" or start with
        /// "Cat-"/"Dog-") and still a readable breed label.
        /// </summary>
        public static string GetPetCacheKey(Pet pet)
        {
            string species = string.IsNullOrEmpty(pet.petType.Value) ? "Dog" : pet.petType.Value;
            string breed = string.IsNullOrEmpty(pet.whichBreed.Value) ? "0" : pet.whichBreed.Value;

            return breed == "0" ? species : $"{species}-{breed}";
        }

        /// <summary>
        /// Crops and caches <paramref name="pet"/>'s breed portrait,
        /// keyed by <see cref="GetPetCacheKey"/>, if not already
        /// cached. A no-op otherwise, so it's cheap to call once per
        /// pet every tick — main-thread only (touches the graphics
        /// device), same constraint as the <see cref="FarmAnimal"/>
        /// overload.
        /// </summary>
        public static void EnsureCachedForPet(Pet pet, GraphicsDevice device)
        {
            // Fourth attempt at this crop, and the first backed by the
            // exact same real menu this app is reproducing rather than
            // a decompile inference, a misapplied real API, or a
            // documented-but-generic spritesheet layout:
            //  1. frame (0, 0) — an unconfirmed guess that frame 0 is a
            //     pet's portrait pose. Reported wrong-looking in-app.
            //  2. Pet.GetPetIcon(out assetName, out sourceRect) — a real
            //     API, but its own doc comment gives away why it still
            //     looked wrong: "The 16x16 pixel area within the
            //     texture for the icon" is a small menu-list thumbnail
            //     (a pet-customization-list scale), not a portrait-scale
            //     crop. Also reported wrong-looking.
            //  3. pet.Sprite.SourceRect — the pet's *live* current
            //     animation frame. Real and citable, but
            //     non-deterministic (this cache is write-once — see
            //     CropAndCache), and also reported wrong-looking.
            //  4. Rectangle(0, 0, SpriteWidth, SpriteHeight) — backed by
            //     the Stardew Valley Wiki's modding docs
            //     (stardewvalleywiki.com/Modding:Pets, "Spritesheet
            //     Layout": frames 0-3 are the "move down" cycle, so
            //     frame 0 is the standing-still, facing-camera idle
            //     pose). This looked much closer to the user, but was
            //     still reported wrong ("I think pet sprites using
            //     (move left 1) in the menu") — the wiki's *general*
            //     spritesheet layout doesn't describe what this
            //     *specific* menu actually draws.
            //
            // That report led to finding and reading the real,
            // decompiled StardewValley.Menus.AnimalPage.cs directly —
            // vanilla 1.6's own Animals page, the exact menu this app's
            // Animals screen reproduces. Its AnimalEntry constructor
            // computes a pet's portrait rect with a fixed pixel formula
            // rather than reading any live/animated frame:
            // Rectangle(0, pet.Sprite.SourceRect.Height * 2 - 24,
            // pet.Sprite.SourceRect.Width, 24) — a deterministic crop
            // that (per the wiki's own 32px-frame/4-frames-per-row
            // layout) lands in the *bottom* 24px of the sheet's third
            // row ("move up") rather than row 0 ("move down") — which
            // is consistent with the user's own observation that the
            // rendered result looked like a left/side-facing pose
            // rather than the idle front-facing one attempt 4 assumed.
            // This is the real, authoritative answer: it's what
            // vanilla's own Animals menu draws for a pet, not an
            // inference about which named animation frame "should" be
            // the portrait.
            Texture2D? sourceTexture = pet.Sprite?.Texture;
            if (sourceTexture is null)
                return;

            Rectangle spriteRect = pet.Sprite!.SourceRect;
            if (spriteRect.Width <= 0 || spriteRect.Height <= 0)
                return;

            Rectangle cropRect = new Rectangle(0, spriteRect.Height * 2 - 24, spriteRect.Width, 24);

            CropAndCache(GetPetCacheKey(pet), sourceTexture, cropRect, device);
        }

        /// <summary>
        /// Shared crop-and-cache step behind both
        /// <see cref="EnsureCached(FarmAnimal, GraphicsDevice)"/> and
        /// <see cref="EnsureCachedForPet"/> — both now resolve their own
        /// source texture and their own <c>AnimalPage</c>-verified crop
        /// rect first (see each method's doc comment), so this step
        /// just does the actual pixel copy/PNG-encode/cache-write,
        /// keyed by <paramref name="cacheKey"/> — a no-op if that key
        /// is already cached.
        /// </summary>
        private static void CropAndCache(string cacheKey, Texture2D? sourceTexture, Rectangle sourceRect, GraphicsDevice device)
        {
            if (Cache.ContainsKey(cacheKey))
                return;

            if (sourceTexture is null)
                return;

            int width = sourceRect.Width;
            int height = sourceRect.Height;
            if (width <= 0 || height <= 0)
                return;

            var pixels = new Color[width * height];
            sourceTexture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, width, height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, width, height);

            Cache[cacheKey] = ms.ToArray();
        }

    }
}
