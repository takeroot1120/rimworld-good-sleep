using HarmonyLib;
using Verse;

namespace GoodSleep
{
	public class GoodSleepMod : Mod
	{
		public static Harmony Harmony;

		public GoodSleepMod(ModContentPack content) : base(content)
		{
			Harmony = new Harmony("takeroot1120.goodsleep");
			Harmony.PatchAll();
		}
	}
}
