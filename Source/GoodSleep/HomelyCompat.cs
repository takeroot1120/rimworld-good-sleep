using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GoodSleep
{
	// 互換性パッチ: 「Intimacy - Homely」(Steam Workshop, PillowTalkWithEuterpe.dll)。
	//
	// バニラの Core/Defs/ThinkTreeDefs/SubTrees_Misc.xml (MainColonistBehaviorCore) では
	// JobGiver_GetFood と JobGiver_GetRest が同じ ThinkNode_PrioritySorter の直下にあり、
	// Homely はここへ独自の PillowTalkWithEuterpe.JobGiver_GetDressed を挿入する
	// (自MODのXMLパッチで JobGiver_GetFood の位置に差し込む)。
	// その GetPriority は「服を脱いで待機中」であれば一切のクールダウンなしに固定値 999 を
	// 返すため、このMOD(あるいはバニラの本来睡眠中のポーンも同様)がスケジュール睡眠中
	// ずっと優先度8を主張し続けている間、服を脱いで横になった直後にほぼ即座に
	// 「服を着る」ジョブへ割り込まれてしまい、脱衣→着衣を延々繰り返す不具合が確認された。
	//
	// 対策として、まず GetDressed の優先度自体を「まだ LayDown 中」なら抑える。
	// それでも、他の欲求(空腹など)を満たすために一旦起きた場合、Homely 側は
	// LayDown ジョブが終わるたびに(GetPriorityを経由せず)無条件で「服を着る」ジョブを
	// jobQueue に直接積む作りになっており、そこから服を着て用事を済ませ、戻ってきて
	// また寝る、という一連の流れ全体をこなすだけの時間、こちらの強制睡眠(優先度8)を
	// 差し控えないと、用事の途中で寝かせようとして再び割り込んでしまう。
	// そのため、対象ポーンが Homely 自身の「服を着る/脱ぐ」ジョブを実行している間は
	// GoodSleepUtility のクールダウン(Pawn_MindState.canSleepTick)を都度延長し、
	// 一連の流れが自然に完了するまで強制睡眠を差し挟まないようにする。
	//
	// Intimacy - Homely への直接参照は持たず、導入されている場合のみ実行時にリフレクションで
	// 検出してパッチを当てる。
	[StaticConstructorOnStartup]
	internal static class HomelyCompat
	{
		private static bool active;

		static HomelyCompat()
		{
			try
			{
				Type getDressedType = AccessTools.TypeByName("PillowTalkWithEuterpe.JobGiver_GetDressed");
				if (getDressedType == null)
				{
					return; // Intimacy - Homely は導入されていない
				}

				MethodInfo getPriority = AccessTools.Method(getDressedType, "GetPriority");
				if (getPriority != null)
				{
					GoodSleepMod.Harmony.Patch(getPriority,
						postfix: new HarmonyMethod(typeof(HomelyCompat), nameof(SuppressGetDressedPriority)));
				}

				GoodSleepMod.Harmony.Patch(AccessTools.Method(typeof(JobDriver), "DriverTick"),
					postfix: new HarmonyMethod(typeof(HomelyCompat), nameof(ExtendCooldownWhileHandlingClothes)));

				active = true;
				Log.Message("[GoodSleep] Intimacy - Homely を検出したため、就寝中の服の着替えループを防ぐ互換パッチを適用しました。");
			}
			catch (Exception ex)
			{
				Log.Warning("[GoodSleep] Intimacy - Homely 互換パッチの適用に失敗しました: " + ex);
			}
		}

		private static void SuppressGetDressedPriority(Pawn pawn, ref float __result)
		{
			if (__result <= 0f)
			{
				return;
			}
			if (pawn?.CurJob != null && pawn.CurJob.def == JobDefOf.LayDown &&
				GoodSleepUtility.IsScheduledSleepNow(pawn))
			{
				__result = 0f;
			}
		}

		private const int CheckIntervalTicks = 60;

		private static void ExtendCooldownWhileHandlingClothes(JobDriver __instance)
		{
			if (!active)
			{
				return;
			}
			Job job = __instance.job;
			Pawn pawn = __instance.pawn;
			if (job == null || pawn == null || !IsHomelyClothesJob(job.def))
			{
				return;
			}
			if (!pawn.IsHashIntervalTick(CheckIntervalTicks))
			{
				return;
			}
			if (pawn.mindState == null || pawn.timetable == null ||
				pawn.timetable.CurrentAssignment != TimeAssignmentDefOf.Sleep)
			{
				return;
			}

			int resumeAt = Find.TickManager.TicksGame + GoodSleepUtility.ForceSleepCooldownTicks;
			if (resumeAt > pawn.mindState.canSleepTick)
			{
				pawn.mindState.canSleepTick = resumeAt;
			}
		}

		// Intimacy - Homely 自身の JobDef(SEX_Undress / SEX_GetDressed /
		// SEX_GetDressedFromWardrobe)かどうかを、型参照ではなく defName で判定する。
		// これらは同MODのXML(JobDefs/Jobs_Garments.xml)で定義される。
		private static bool IsHomelyClothesJob(JobDef def)
		{
			if (def == null)
			{
				return false;
			}
			return def.defName == "SEX_Undress" ||
				def.defName == "SEX_GetDressed" ||
				def.defName == "SEX_GetDressedFromWardrobe";
		}
	}
}
