using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace GoodSleep
{
	internal static class GoodSleepUtility
	{
		// GetPriority 側で使う軽量チェック。ワークギバーの走査を含まないため、
		// 思考ツリーの優先度計算(頻繁に呼ばれる)で使っても重くならない。
		public static bool IsScheduledSleepNow(Pawn pawn)
		{
			if (pawn?.timetable == null || pawn.timetable.CurrentAssignment != TimeAssignmentDefOf.Sleep)
			{
				return false;
			}
			if (!pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState)
			{
				return false;
			}
			// 「既に LayDown 中なら対象外」という判定は入れない。休息ゲージが満タンになると
			// バニラの GetPriority は 0 に落ちるため、寝ている間もこの強制付与を効かせ続けないと
			// 休息100%到達と同時に他の(優先度の低い)ジョブに上書きされて起きてしまう。
			if (pawn.health?.capacities == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Consciousness))
			{
				return false;
			}
			return true;
		}

		// TryGiveJob 側で使う最終チェック。実際にジョブを発行してよいかどうかは
		// 優先度1の作業が今すぐ着手可能かどうかまで見て決める(こちらはやや重い)。
		public static bool ShouldForceSleepNow(Pawn pawn)
		{
			return IsScheduledSleepNow(pawn) && !HasPendingPriority1Work(pawn);
		}

		// 「優先度1の作業がなければ」という要件のため、ワークタブで優先度1に設定されている
		// WorkTypeDef を持つ WorkGiver に、今すぐ着手できるジョブが実際にあるかを確認する。
		// バニラの JobGiver_Work が後で行うのと同じスキャンを先取りして行う形になるが、
		// 優先度1に設定される作業種別は通常ごく少数なのでコストは限定的。
		private static bool HasPendingPriority1Work(Pawn pawn)
		{
			Pawn_WorkSettings workSettings = pawn.workSettings;
			if (workSettings == null || !workSettings.EverWork)
			{
				return false;
			}

			// 既に優先度1の作業に取り掛かっている最中なら、それを中断させない。
			WorkGiverDef curGiverDef = pawn.CurJob?.workGiverDef;
			if (curGiverDef != null && workSettings.GetPriority(curGiverDef.workType) == 1)
			{
				return true;
			}

			foreach (WorkTypeDef workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (workSettings.GetPriority(workType) != 1)
				{
					continue;
				}
				foreach (WorkGiverDef giverDef in workType.workGiversByPriority)
				{
					if (WorkGiverHasAvailableJob(pawn, giverDef))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool WorkGiverHasAvailableJob(Pawn pawn, WorkGiverDef giverDef)
		{
			WorkGiver giver = giverDef.Worker;
			if (giver == null)
			{
				return false;
			}

			try
			{
				if (giver.ShouldSkip(pawn, false))
				{
					return false;
				}
				if (giver.MissingRequiredCapacity(pawn) != null)
				{
					return false;
				}

				if (giver is WorkGiver_Scanner scanner)
				{
					if (giverDef.scanThings)
					{
						foreach (Thing t in scanner.PotentialWorkThingsGlobal(pawn))
						{
							if (scanner.HasJobOnThing(pawn, t, false))
							{
								return true;
							}
						}
					}
					if (giverDef.scanCells)
					{
						foreach (IntVec3 cell in scanner.PotentialWorkCellsGlobal(pawn))
						{
							if (scanner.HasJobOnCell(pawn, cell, false))
							{
								return true;
							}
						}
					}
					return false;
				}

				return giver.NonScanJob(pawn) != null;
			}
			catch (Exception)
			{
				// 他MOD由来の WorkGiver が例外を投げても、就寝判定そのものは壊さない。
				return false;
			}
		}
	}
}
