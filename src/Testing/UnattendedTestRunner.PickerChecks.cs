using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Run;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// 评分 AI 纯逻辑检查模式：开新局（不进入战斗），逐项调用 CardPickerAI/RelicPickerAI，
    /// 断言选牌结果或精确评分。用于锁定评分公式与跳过/选牌阈值（对应 TEST_MATRIX 标注缺失的评分 AI 单测）。
    /// 检查逐项串行，每项可设置牌组构成、玩家生命和幕索引作为评分前置条件。
    /// </summary>
    private async Task RunPickerChecksAsync()
    {
        (CharacterModel character, RunState runState, Player player) = await _scenarioBuilder.StartRunAsync();
        for (int index = 0; index < _request.PickerChecks.Length; index++)
        {
            UnattendedPickerCheck check = _request.PickerChecks[index];
            SetStage($"picker_check_{index + 1}");
            await RunPickerCheckAsync(check, runState, player);
            string expectation = check.ExpectedScore is { } score
                ? $":score={score}"
                : string.IsNullOrWhiteSpace(check.ExpectedPickId) ? ":skip" : $":{check.ExpectedPickId}";
            _completedChecks.Add($"Picker:{check.Kind}:{string.Join("/", check.OptionIds)}{expectation}");
            Entry.Logger.Info(
                $"[CombatSolver/Unattended] PICKER_CHECK run_id={_request.RunId} index={index + 1} " +
                $"kind={check.Kind} options={string.Join("/", check.OptionIds)} " +
                $"expected={check.ExpectedPickId ?? "skip"} passed");
        }

        SetStage("picker_cleanup");
        await _host.ReturnToMainMenu();
        EnsureWithinDeadline();

        SetStage("passed");
        _writer.Write(
            "Passed",
            _stage,
            character.Id.ToString(),
            "-",
            combatEnded: false,
            startedTurn: 0,
            finishedTurn: 0);
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] PICKER PASSED run_id={_request.RunId} " +
            $"scenario={_request.ScenarioId} checks={_request.PickerChecks.Length} " +
            $"elapsed_ms={_stopwatch.Elapsed.TotalMilliseconds:F1}");
        await ExitIfRequestedAsync(0);
    }

    private async Task RunPickerCheckAsync(UnattendedPickerCheck check, RunState runState, Player player)
    {
        // 牌组构成与生命只对卡牌评分有意义；遗物评分是纯静态模型属性。
        if (check.Kind == "Card")
        {
            foreach (string cardId in check.DeckCardIds)
            {
                CardModel canonical = ResolveUnique(ModelDb.AllCards, cardId, "卡牌");
                CardModel card = runState.CreateCard(canonical, player);
                CardPileAddResult result = await CardPileCmd.Add(card, PileType.Deck);
                if (!result.success)
                    throw new InvalidOperationException($"游戏拒绝把 {cardId} 注入跑局牌组。");
            }
            if (check.PlayerMaxHp is { } maxHp)
                await CreatureCmd.SetMaxHp(player.Creature, maxHp);
            if (check.PlayerHp is { } hp)
            {
                await CreatureCmd.SetCurrentHp(
                    player.Creature,
                    Math.Clamp(hp, 1, player.Creature.MaxHp));
            }
        }
        if (check.ActIndexForTest is { } actIndex)
            await RunManager.Instance.SetActInternal(actIndex);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        switch (check.Kind)
        {
            case "Card":
                RunCardPickerCheck(check, player, runState);
                break;
            case "Relic":
                RunRelicPickerCheck(check);
                break;
            case "AncientRelic":
                RunAncientRelicPickerCheck(check, runState);
                break;
            default:
                throw new InvalidOperationException($"未知选牌检查类型 {check.Kind}。");
        }
    }

    private void RunCardPickerCheck(UnattendedPickerCheck check, Player player, RunState runState)
    {
        CardModel[] options = check.OptionIds
            .Select(id => ResolveUnique(ModelDb.AllCards, id, "卡牌"))
            .ToArray();
        if (check.ExpectedScore is { } expectedScore)
        {
            if (options.Length != 1)
                throw new InvalidOperationException("ExpectedScore 断言只支持单个候选卡牌。");
            DeckContext context = DeckContext.From(player, runState);
            float actual = CardPickerAI.Evaluate(options[0], context);
            if (Math.Abs(actual - expectedScore) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"卡牌 {options[0].Id.Entry} 评分期望 {expectedScore}，实际 {actual:0.###}。" +
                    $"牌组画像: deck={context.DeckSize} attacks={context.AttackCount} " +
                    $"blocks={context.BlockCardCount} aoe={context.AoECount} act={context.ActIndex} " +
                    $"hp={context.HpRatio:0.##} count={context.CountOf(options[0])}");
            }
            return;
        }
        CardModel? chosen = CardPickerAI.PickBest(options, player, runState);
        DeckContext deckContext = DeckContext.From(player, runState);
        string scores = string.Join(
            " | ",
            options.Select(card => $"{card.Id.Entry}={CardPickerAI.Evaluate(card, deckContext):0.###}"));
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] PICKER_SCORES 角色={_request.CharacterId} " +
            $"chosen={chosen?.Id.Entry ?? "跳过"} 评分=[{scores}]");
        AssertPickResult(chosen?.Id.Entry, check.ExpectedPickId, "卡牌", string.Join("/", check.OptionIds));
    }

    private void RunRelicPickerCheck(UnattendedPickerCheck check)
    {
        RelicModel[] options = check.OptionIds
            .Select(id => ResolveUnique(ModelDb.AllRelics, id, "遗物"))
            .ToArray();
        if (check.ExpectedScore is { } expectedScore)
        {
            if (options.Length != 1)
                throw new InvalidOperationException("ExpectedScore 断言只支持单个候选遗物。");
            float actual = RelicPickerAI.Score(options[0]);
            if (Math.Abs(actual - expectedScore) > 0.001f)
            {
                throw new InvalidOperationException(
                    $"遗物 {options[0].Id.Entry} 评分期望 {expectedScore}，实际 {actual:0.###}。");
            }
            return;
        }
        RelicModel? chosen = RelicPickerAI.PickBest(options);
        AssertPickResult(chosen?.Id.Entry, check.ExpectedPickId, "遗物", string.Join("/", check.OptionIds));
    }

    private void RunAncientRelicPickerCheck(UnattendedPickerCheck check, RunState runState)
    {
        RelicModel[] options = check.OptionIds
            .Select(id => ResolveUnique(ModelDb.AllRelics, id, "遗物"))
            .ToArray();
        RelicModel chosen = RelicPickerAI.PickBestAncientChoice(options, runState);
        if (check.AllowAncientCurseFallback)
        {
            // 文档行为：全部选项都是诅咒时退回第一个选项。
            if (!ReferenceEquals(chosen, options[0]))
            {
                throw new InvalidOperationException(
                    $"全诅咒先古遗物期望回退到 {options[0].Id.Entry}，实际 {chosen.Id.Entry}。");
            }
        }
        else
        {
            if (RelicPickerAI.IsAncientCurse(chosen))
            {
                throw new InvalidOperationException(
                    $"先古遗物选择了诅咒遗物 {chosen.Id.Entry}（选项 {string.Join("/", check.OptionIds)}）。");
            }
            if (!string.IsNullOrWhiteSpace(check.ExpectedPickId)
                && !chosen.Id.Entry.Equals(check.ExpectedPickId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"期望选中 {check.ExpectedPickId}，实际选中 {chosen.Id.Entry}（选项 {string.Join("/", check.OptionIds)}）。");
            }
        }
    }

    private static void AssertPickResult(
        string? actualId,
        string? expectedId,
        string kind,
        string optionSummary)
    {
        if (string.IsNullOrWhiteSpace(expectedId))
        {
            if (actualId != null)
            {
                throw new InvalidOperationException(
                    $"期望跳过{kind}，实际选中 {actualId}（选项 {optionSummary}）。");
            }
            return;
        }
        if (actualId == null)
        {
            throw new InvalidOperationException(
                $"期望选中 {expectedId}，实际跳过{kind}（选项 {optionSummary}）。");
        }
        if (!actualId.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"期望选中 {expectedId}，实际选中 {actualId}（选项 {optionSummary}）。");
        }
    }
}
