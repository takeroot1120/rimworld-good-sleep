using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GoodSleep
{
	// バニラの JobGiver_GetRest.GetPriority は「疲れていて、かつ入眠可能な状態」でなければ
	// 0 を返す(Need_Rest を持たない種族は rest == null の時点で即 0、疲れていない通常種族も
	// RestUtility.CanFallAsleep が rest.CurLevel < しきい値 を要求するため 0 になる)。
	// ThinkNode_PrioritySorter は GetPriority が 0 以下のノードを最初から候補にすら入れないため、
	// このガードを通さない限り TryGiveJob 自体が一切呼ばれない。
	// そのためスケジュールが睡眠の間は、疲労状態に関わらずバニラと同じ 8 という優先度を
	// 強制的に与えて、ノードが候補に入るようにする。実際に寝かせるかどうかの最終判断
	// (優先度1の作業が無いか等)は TryGiveJob 側のパッチで行う。
	[HarmonyPatch(typeof(JobGiver_GetRest), "GetPriority")]
	public static class Patch_JobGiver_GetRest_GetPriority_ForceScheduledSleep
	{
		private const float VanillaSleepScheduledPriority = 8f;

		private static void Postfix(Pawn pawn, ref float __result)
		{
			if (__result > 0f)
			{
				return; // バニラが既にこのノードを候補にしているなら触らない
			}
			if (GoodSleepUtility.IsScheduledSleepNow(pawn))
			{
				__result = VanillaSleepScheduledPriority;
			}
		}
	}

	// GetPriority のパッチでノードが候補入りした後、実際にジョブを発行する側。
	// バニラが「今は休ませなくていい」と判断した(__result == null の)場合に限って、
	// 優先度1の作業が今すぐ着手可能でないことを確認した上で、強制的に睡眠ジョブを割り込ませる。
	[HarmonyPatch(typeof(JobGiver_GetRest), "TryGiveJob")]
	public static class Patch_JobGiver_GetRest_TryGiveJob_ForceScheduledSleep
	{
		// バニラの「ベッドが無い場合に地面の寝床を探す」ロジックをそのまま再利用するための
		// private メソッド参照。壁の中や作業台の上のような不適切な位置で寝かせないため。
		private static readonly MethodInfo TryFindGroundSleepSpotMethod =
			AccessTools.Method(typeof(JobGiver_GetRest), "TryFindGroundSleepSpotFor");

		private static void Postfix(JobGiver_GetRest __instance, Pawn pawn, ref Job __result)
		{
			if (__result != null)
			{
				return; // バニラが既に休憩ジョブを出しているならそれを尊重する
			}
			if (!GoodSleepUtility.ShouldForceSleepNow(pawn))
			{
				return;
			}

			Building_Bed bed = RestUtility.FindBedFor(pawn);
			if (bed != null)
			{
				__result = JobMaker.MakeJob(JobDefOf.LayDown, bed);
			}
			else
			{
				object[] args = { pawn, null };
				bool foundGroundSpot = (bool)TryFindGroundSleepSpotMethod.Invoke(__instance, args);
				IntVec3 spot = foundGroundSpot ? (IntVec3)args[1] : pawn.Position;
				__result = JobMaker.MakeJob(JobDefOf.LayDown, spot);
			}

			// forceSleep を立てることで、Need_Rest が無い/減らない種族でも
			// JobDriver_LayDown 側で「疲れていないから寝ない」判定をスキップして即座に入眠させる。
			__result.forceSleep = true;
		}
	}
}
