using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GoodSleep
{
	// Intimacy - Homely のように、着替え完了後に ThinkNode を経由せず
	// pawn.jobs.StartJob(new Job(JobDefOf.LayDown, bed), ...) を直接呼ぶMODがある場合、
	// そのジョブは forceSleep=false で始まる。このMODの TryGiveJob 側の forceSleep 付与は
	// 次に JobGiver_GetRest.TryGiveJob が呼ばれるタイミング(最悪、Toils_LayDown 自身の
	// 211 tick ごとの定期チェックまで)まで間に合わない。もしそれより早く別の欲求に
	// 割り込まれると、forceSleep が一度も true にならないまま中断されてしまい、
	// Patch_Toils_LayDown_FinalizeLayingJob_CooldownAfterInterruptedForceSleep の
	// クールダウンも発動しない。この結果、着替え→一瞬だけ入眠→即中断→着替え…を
	// 高速に繰り返してしまう不具合を実機ログで確認した(「強制睡眠ジョブが中断されました」
	// のログが一度も出ないまま現象だけが再現していた)。
	// LayDown ジョブが開始されたその瞬間(Notify_Starting)に forceSleep を付与することで、
	// 発行元を問わず必ず初回 tick から forceSleep=true の状態にし、このギャップを無くす。
	[HarmonyPatch(typeof(JobDriver_LayDown), nameof(JobDriver_LayDown.Notify_Starting))]
	public static class Patch_JobDriver_LayDown_Notify_Starting_ForceSleepImmediately
	{
		private static void Postfix(JobDriver_LayDown __instance)
		{
			Job job = __instance.job;
			Pawn pawn = __instance.pawn;
			if (job == null || pawn == null || job.def != JobDefOf.LayDown || job.forceSleep)
			{
				return;
			}
			if (!GoodSleepUtility.ShouldForceSleepNow(pawn))
			{
				return;
			}
			job.forceSleep = true;
		}
	}

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
			if (GoodSleepUtility.ShouldForcePriorityNow(pawn))
			{
				__result = VanillaSleepScheduledPriority;
			}
		}
	}

	// GetPriority のパッチでノードが候補入りした後、実際にジョブを発行する側。
	// バニラが「今は休ませなくていい」と判断した(__result == null の)場合に限って、
	// 優先度1の作業が今すぐ着手可能でないことを確認した上で、強制的に睡眠ジョブを割り込ませる。
	//
	// 互換性メモ: 「Intimacy - Homely」など、就寝前の着替え(脱衣)を挟むMODも同じ
	// JobGiver_GetRest.TryGiveJob を Postfix しており、それらは「__result が既に
	// null でなければ、その睡眠ジョブを着替えジョブに差し替える」という、先に他の
	// Postfix が結果を用意している前提の作りになっている。Harmony はパッチの適用順を
	// 明示しない限りMODの読み込み順に依存するため、このMODのPostfixが後に実行されると
	// __result がまだ null のまま相手側Postfixを素通りしてしまい、そのあとこちらが
	// 強制睡眠ジョブを差し込む…という不安定な競合が起きる。Priority.High を指定して
	// 読み込み順に関わらず必ず先に(=他modより先に __result を埋める側として)実行されるようにする。
	[HarmonyPatch(typeof(JobGiver_GetRest), "TryGiveJob")]
	public static class Patch_JobGiver_GetRest_TryGiveJob_ForceScheduledSleep
	{
		// バニラの「ベッドが無い場合に地面の寝床を探す」ロジックをそのまま再利用するための
		// private メソッド参照。壁の中や作業台の上のような不適切な位置で寝かせないため。
		private static readonly MethodInfo TryFindGroundSleepSpotMethod =
			AccessTools.Method(typeof(JobGiver_GetRest), "TryFindGroundSleepSpotFor");

		[HarmonyPriority(Priority.High)]
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

			// 既に(他MOD発行のものも含め)LayDown 中なら、新しいジョブに置き換えず
			// forceSleep フラグだけ立てて継続させる。ここで毎回新規ジョブを発行すると、
			// ジョブの発行元(jobGiver)が食い違うケース
			// (例: 他MODが ThinkNode を指定せず直接 StartJob したジョブは jobGiver が null)
			// で「同一ジョブへの不要な上書き」が起こり、その割り込み自体が
			// Toils_LayDown.FinalizeLayingJob 経由で他MODの後処理(例: 起床時の着替え)を
			// 誤って毎回トリガーしてしまうことがある
			// (Intimacy - Homely との併用で脱衣→着衣の無限ループとして確認済み)。
			Job curJob = pawn.CurJob;
			if (curJob != null && curJob.def == JobDefOf.LayDown)
			{
				curJob.forceSleep = true;
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

	// forceSleep を立てた睡眠ジョブは、Toils_LayDown の tick 処理内で
	// 「!curJob.forceSleep のときしか RestUtility.ShouldWakeUp による起床(asleep=false)を
	// 通さない」という条件になっているため、forceSleep=true のジョブは寝ている間ずっと
	// asleep フラグが立ちっぱなしのまま二度と false に戻らない。
	// PawnUtility.Awake(pawn) はこの asleep フラグを見て「起きているか」を判定しており、
	// JobGiver_Work / JobGiver_GetJoy など多くの JobGiver がその Awake() を前提条件にして
	// いるため、asleep が立ちっぱなしだと「起きているとみなされない」ポーンには仕事も娯楽も
	// 一切提案されなくなり、CheckForJobOverride を呼んでも常に空振りしてしまう
	// (実機ログで curJob が変化しないことを確認済み)。
	// スケジュールが睡眠でなくなったら、このMODが発行した強制睡眠ジョブに限って
	// asleep を明示的に false へ戻してから CheckForJobOverride を呼び直し、
	// 他の JobGiver が正しく仕事・娯楽を提案できるようにする。
	[HarmonyPatch(typeof(JobDriver), "DriverTick")]
	public static class Patch_JobDriver_DriverTick_WakeUpWhenScheduleLeavesSleep
	{
		private const int CheckIntervalTicks = 60;

		private static void Postfix(JobDriver __instance)
		{
			Job job = __instance.job;
			if (job == null || job.def != JobDefOf.LayDown || !job.forceSleep)
			{
				return;
			}
			Pawn pawn = __instance.pawn;
			if (pawn == null || !pawn.IsHashIntervalTick(CheckIntervalTicks))
			{
				return;
			}
			if (GoodSleepUtility.IsScheduledSleepNow(pawn))
			{
				return; // まだ強制睡眠の対象時間内
			}

			__instance.asleep = false;
			pawn.jobs.CheckForJobOverride();
		}
	}

	// Toils_LayDown は「211 tick ごとに自分から CheckForJobOverride を呼ぶ」処理を
	// 内蔵しているため、既に LayDown 中のポーンは自然に強制睡眠へ移行できる。
	// しかし DoBill/Research/Mine/Haul のような通常の仕事ジョブにはこの仕組みが無く、
	// GetPriority 側で優先度8を主張しているだけでは、そのジョブが自然に終わって
	// 次のジョブを探すタイミングが来るまで一切割り込めない。実機ログで、
	// ShouldForceSleepNow=True のまま同じ仕事ジョブが何百 tick も継続することを確認済み
	// (小規模コロニーの短時間タスクでは偶然すぐ次のジョブ選定が回ってきていたため
	// 表面化しなかったが、長時間の研究・製作ジョブがあると顕在化する)。
	// スケジュールが睡眠で、まだ強制睡眠に入れていないポーンについては、
	// このMOD側から定期的に CheckForJobOverride を呼び直し、割り込むチャンスを与える。
	[HarmonyPatch(typeof(JobDriver), "DriverTick")]
	public static class Patch_JobDriver_DriverTick_RequestOverrideForPendingForcedSleep
	{
		private const int CheckIntervalTicks = 100;

		private static void Postfix(JobDriver __instance)
		{
			Job job = __instance.job;
			if (job == null || job.def == JobDefOf.LayDown)
			{
				return; // 既に LayDown 中なら Toils_LayDown 自身の仕組みに任せる
			}
			Pawn pawn = __instance.pawn;
			if (pawn == null || !pawn.IsHashIntervalTick(CheckIntervalTicks))
			{
				return;
			}
			if (!GoodSleepUtility.ShouldForceSleepNow(pawn))
			{
				return;
			}

			pawn.jobs.CheckForJobOverride();
		}
	}

	// 強制睡眠ジョブが、スケジュールが睡眠のままの状態で(=このMODが起こしたのではなく)
	// 他の欲求を満たすために中断された場合、中断直後にまた即座に強制睡眠へ戻そうとすると、
	// Intimacy - Homely のような就寝前後で服を着脱するMODと組み合わせたときに
	// 着替え→即中断→着替え…を繰り返してしまう(実機で確認済み)。
	// FinalizeLayingJob は LayDown ジョブがどんな理由であれ終了する際に必ず呼ばれるため、
	// ここでバニラの Pawn_MindState.canSleepTick(元々「しばらく強制的な休息を考慮しない」
	// 目的で用意されているフィールド)にクールダウンを設定し、中断の原因になった行動が
	// 一段落する時間を与える。GoodSleepUtility.ShouldForcePriorityNow がこれを見て、
	// クールダウン中は強制睡眠を控える(IsScheduledSleepNow 自体はクールダウンを
	// 含まない。HomelyCompat 側の着替え抑制判定にも使っているため)。
	[HarmonyPatch(typeof(Toils_LayDown), "FinalizeLayingJob")]
	public static class Patch_Toils_LayDown_FinalizeLayingJob_CooldownAfterInterruptedForceSleep
	{
		private static void Prefix(Pawn pawn, out bool __state)
		{
			// スケジュールがまだ睡眠のままなら、このMOD以外の理由でジョブが終わろうとしている
			// (=途中で別の欲求のジョブに割り込まれた)と判断できる。
			__state = pawn?.CurJob != null && pawn.CurJob.def == JobDefOf.LayDown && pawn.CurJob.forceSleep
				&& pawn.timetable != null && pawn.timetable.CurrentAssignment == TimeAssignmentDefOf.Sleep;
		}

		private static void Postfix(Pawn pawn, bool __state)
		{
			if (!__state || pawn?.mindState == null)
			{
				return;
			}

			int resumeAt = Find.TickManager.TicksGame + GoodSleepUtility.ForceSleepCooldownTicks;
			if (resumeAt > pawn.mindState.canSleepTick)
			{
				pawn.mindState.canSleepTick = resumeAt;
			}
		}
	}
}
