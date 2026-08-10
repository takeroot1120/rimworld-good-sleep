# RimWorld MOD: Good Sleep

*[English version](README.md)*

## やりたいこと
バニラのRimWorldでは、スケジュールの該当コマが「睡眠」になっていても、実際にベッドへ向かうのは
ある程度疲れている場合(`Need_Rest` が下がって `RestUtility.CanFallAsleep` が true を返す状態)に限られる。
種族MODの中には疲労が一切溜まらない(`Need_Rest` を持たない、または休息needが常に満タンで減らない)
種族を追加するものがあり、そういった種族は8時間分「睡眠」を割り当てても自分からは絶対に寝に行かない。

本MODは、そういった種族も含めて、疲れているかどうかに関わらずスケジュールで割り当てられた
睡眠時間になったら必ず眠るようにする。ただし、ワークタブで優先度1(最優先)に設定した作業が
今すぐ着手可能な場合は、そちらを優先させ、それが無いときだけ強制的に眠らせる。

- MOD名・author・packageId は確定済み(`Good Sleep` / `takeroot1120` / `takeroot1120.goodsleep`)。

## 前提環境
- RimWorld: `1.6.4871 rev590`
  - Steam既定インストール先: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld`
- .NET SDK: `8.0.423` または互換バージョン。
  `dotnet build Source/GoodSleep/GoodSleep.csproj` でビルド可能。
  (現在のシェルのPATHに `dotnet` が通っていない場合は、`C:\Program Files\dotnet\dotnet.exe` のように
  フルパスで実行する)
- 他MODへの依存なし。プロジェクト単体でビルドできるよう、[Lib.Harmony](https://github.com/pardeike/Harmony)
  (`0Harmony.dll`、MITライセンス)のコピーを `Source/GoodSleep/Libs/` に同梱しており、
  ビルド時にMOD本体のDLLと一緒に `1.6/Assemblies/` へコピーされる。

## 実装内容
ILSpyCmd で `Assembly-CSharp.dll` を逆コンパイルして特定した実際のクラス・メソッド:
- `RimWorld.JobGiver_GetRest` (ポーンの思考ツリー上の `ThinkNode_JobGiver`。火消しや負傷者救助などの
  緊急対応より後、通常の仕事より前という位置にある)
  - `GetPriority(Pawn)` は、ポーンが眠れるほど疲れていない限り `0` を返す
    (`Need_Rest` を持たない場合、または `RestUtility.CanFallAsleep` が「まだ眠くない」と判定した場合、
    いずれも `0`)。`ThinkNode_PrioritySorter` は優先度が `0` のノードをそもそも候補にすら入れないため、
    疲れていないポーンは、スケジュールの内容に関わらず `TryGiveJob` 自体が一切呼ばれない。
  - `TryGiveJob(Pawn)` は、ノードが選ばれた後に実際の `LayDown` ジョブを組み立てる
    (`RestUtility.FindBedFor` でベッドを探し、見つからなければ private な
    `TryFindGroundSleepSpotFor` で地面の寝床を探す)。
- `Verse.AI.Job.forceSleep` (public フィールド) - これを立てておくと、`Toils_LayDown` の
  tick 処理が「入眠可能」とみなす条件が `RestUtility.CanFallAsleep(actor) || curJob.forceSleep` になり、
  自動起床の判定も `RestUtility.ShouldWakeUp(actor) && !curJob.forceSleep` として無効化されるため、
  休息レベルに関わらず入眠・睡眠継続ができるようになる。

`Source/GoodSleep/HarmonyPatches.cs` でこの2つのメソッドにパッチを当てている:
- `Patch_JobGiver_GetRest_GetPriority_ForceScheduledSleep` (`GetPriority` への postfix):
  バニラの結果が `0` で、かつ現在のスケジュールが「睡眠」であれば
  (軽量な `GoodSleepUtility.IsScheduledSleepNow` で判定)、バニラが「睡眠スケジュールかつ疲れている」
  ときに使うのと同じ優先度 `8` を強制的に与える。バニラの定義では「疲れていない」ポーンについて、
  このパッチが無ければノード自体が候補に入らない。
- `Patch_JobGiver_GetRest_TryGiveJob_ForceScheduledSleep` (`TryGiveJob` への postfix):
  バニラがジョブを出さなかった場合に限り、`GoodSleepUtility.ShouldForceSleepNow`
  (スケジュール判定に加えて「優先度1の作業が無い」ことも確認、後述) が true であれば、
  バニラと同じ手順で `LayDown` ジョブを組み立て(ベッドが無ければリフレクション経由で
  private な `TryFindGroundSleepSpotFor` を呼んで地面の寝床を探す)、`forceSleep = true` を立てる。

### 「優先度1の作業がなければ」の判定
`GoodSleepUtility.HasPendingPriority1Work` が「緊急の作業があれば強制睡眠しない」というルールを
実装している。ポーンがワークタブで優先度1に設定している `WorkTypeDef` すべてについて、
紐づく `WorkGiverDef` ごとに今すぐ着手可能なジョブがあるかを実際に確認する
(`WorkGiver.ShouldSkip`/`MissingRequiredCapacity` で足切りした上で、スキャン型は
`WorkGiver_Scanner.PotentialWorkThingsGlobal`/`PotentialWorkCellsGlobal` と
`HasJobOnThing`/`HasJobOnCell`、非スキャン型は `NonScanJob` で判定)。
既に優先度1の作業に取り掛かっている最中であれば、その作業を中断させることもない。
これは実質的にバニラの `JobGiver_Work` が後で行うのと同じスキャンを、優先度1の作業種別
(通常ポーンごとにごく少数)だけ先取りして行う形になっており、コストは就寝スケジュールの
時間帯に限定される。

### 動作確認中に見つかったバグ: 休息100%ちょうどで起きてしまう
実装当初のテストでは、ポーンは正しく寝に行くものの、休息ゲージが100%に到達した瞬間に
起きてしまい、その後進行中のタスクが終わるとまた寝る、というのを繰り返す挙動になっていた。
原因は `GoodSleepUtility.IsScheduledSleepNow` の判定で、「既に `LayDown` ジョブ中のポーンは
対象外」というガードを入れていたこと。強制睡眠ジョブが既に動いているなら再度強制する必要は
ないだろう、という想定だったが、休息が100%に達するとバニラ自身の `GetPriority` は
(`RestUtility.CanFallAsleep` の判定により)本当に `0` まで落ちる。その瞬間、眠っているポーンの
優先度 `8` を維持していたのは唯一この強制付与だけだったため、「既に寝ている」ポーンを対象外に
してしまうと優先度が黙って `0` に落ち、他の(優先度が正の)ジョブに上書きされて起きてしまっていた。
このガードを削除し、スケジュールが「睡眠」である間は入眠済みかどうかに関わらず優先度8を
主張し続けるようにしたところ、実機で解消を確認できた。

## 実機での動作確認状況
通常の人型ポーン・休息needが常に100%で減らない種族MOD・休息needを一切持たない種族MODの
3パターンいずれでも実機で確認済み:
- スケジュールの現在のコマを「睡眠」にすると、疲れていなくてもベッドへ向かうこと
- 休息100%に到達しても即座に起きず、睡眠状態を継続すること
  (休息need無し・常に100%の種族は、バニラのままでは自分から絶対に寝ないため、
  この2パターンでの確認が特に重要)

優先度1の作業がある場合に強制睡眠をスキップする挙動は、実装としては上記の通り組み込んでいるが、
実際に優先度1の作業が発生している状態での実機での個別確認はまだ行っていない。

## フォルダ構成
```
rimworld-good-sleep/
  About/About.xml            MOD情報(name/author/packageId 確定済み)
  LoadFolders.xml             1.6向けロード設定
  Source/GoodSleep/           C# Harmonyパッチのソース
    Libs/0Harmony.dll         同梱している Lib.Harmony (MIT)。ビルド時に参照
  1.6/Assemblies/              ビルド後のDLL出力先
```
