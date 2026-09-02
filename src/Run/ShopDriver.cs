using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.Run;

/// <summary>
/// 商店驱动：打开商品栏，按结构评分买卡/买遗物/买药水/移除卡牌，
/// 购买途中若打开覆盖层（移除选牌）则边等边处理，最后关栏点 Proceed 离开。
/// 由 RunAutoController 在进入 Shop 房间时启动。
/// </summary>
internal static class ShopDriver
{
    private static bool _active;

    private const int MaxPurchases = 10;

    public static void OnRoomEntered()
    {
        RunAutoSession? session = RunAutoController.Session;
        if (session == null || !RunAutoSettings.Enabled || _active)
            return;
        _active = true;
        TaskHelper.RunSafely(HandleAsync());
    }

    private static async Task HandleAsync()
    {
        try
        {
            RunAutoSession? session = RunAutoController.Session;
            if (session == null)
                return;
            CancellationToken token = session.CancellationToken;

            Node root = ((SceneTree)Godot.Engine.GetMainLoop()).Root;
            NMerchantRoom? room = null;
            await RunUiHelper.WaitUntilAsync(
                () => (room = RunUiHelper.FindFirst<NMerchantRoom>(root)) != null,
                token,
                TimeSpan.FromSeconds(15),
                "商店房间未出现");
            if (room == null)
                return;

            session.LogDecision("商店：打开商品栏");
            room.OpenInventory();
            await Task.Delay(600, token);

            for (int attempt = 0; attempt < MaxPurchases; attempt++)
            {
                session = RunAutoController.Session;
                if (session == null || !GodotObject.IsInstanceValid(room))
                    return;

                Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
                DeckContext deckContext = DeckContext.From(player, session.RunState);
                bool hasPotionSlots = player?.HasOpenPotionSlots ?? false;

                (NMerchantSlot? slot, float score) best = (null, float.MinValue);
                foreach (NMerchantSlot slot in room.Inventory.GetAllSlots())
                {
                    MerchantEntry entry = slot.Entry;
                    if (entry == null || !entry.IsStocked || !entry.EnoughGold)
                        continue;
                    float score = ScoreSlot(slot, deckContext, hasPotionSlots, player);
                    if (score > best.score)
                        best = (slot, score);
                }

                if (best.slot == null)
                {
                    session.LogDecision("商店：没有值得买的商品");
                    break;
                }

                session.LogDecision($"商店：购买 {best.slot.GetType().Name}（{best.score:0.#} 分）");
                await PurchaseDrainingOverlaysAsync(best.slot.Entry, room.Inventory.Inventory, token);
                await Task.Delay(250, token);
            }

            // 关闭商品栏。反编译确认：NMerchantInventory._Ready 里 _backButton.Connect(Released, Close)，
            // NClickableControl.ForceClick 无条件发 Released（不检查 IsEnabled），所以无论返回按钮是否处于
            // 禁用态都能触发 Close()（Close 本身也不看 _isInputBlocked，无条件关栏）。
            // 先按 Inventory 子树找（库存自建场景时返回按钮在内），找不到再全房间找（可能是 %BackButton 兄弟节点）。
            NBackButton? back = RunUiHelper.FindFirst<NBackButton>(room.Inventory)
                ?? RunUiHelper.FindFirst<NBackButton>(room);
            if (back != null)
            {
                session.LogDecision($"商店：关闭商品栏（enabled={back.IsEnabled} path={back.GetPath()}）");
                await RunUiHelper.ClickAsync(back, 300);
            }
            await RunUiHelper.WaitUntilAsync(
                () => room.Inventory == null || !room.Inventory.IsOpen,
                token,
                TimeSpan.FromSeconds(10),
                $"商店商品栏未关闭（back={(back == null ? "null" : $"enabled={back.IsEnabled}")} " +
                $"inventory_open={room.Inventory != null && room.Inventory.IsOpen}）");

            NProceedButton proceedButton = room.ProceedButton;
            await RunUiHelper.WaitUntilAsync(
                () => proceedButton != null && proceedButton.IsEnabled,
                token,
                TimeSpan.FromSeconds(10),
                "商店 Proceed 未变可用");
            if (proceedButton != null && proceedButton.IsEnabled)
            {
                session.LogDecision("离开商店");
                await RunUiHelper.ClickAsync(proceedButton, 150);
            }

            // 商店 Proceed 只打开地图，不触发 RoomExited 房间退出事件（与篝火/事件同一缺口），
            // MapRouter 不会被 RunAutoController 触发，主动请求选路进下一房间。
            await RunUiHelper.WaitUntilAsync(
                () => NMapScreen.Instance is { IsOpen: true },
                token,
                TimeSpan.FromSeconds(10),
                "商店地图未打开");
            session.LogDecision("商店完成，地图已打开，请求选路");
            MapRouter.RequestRoute();
        }
        catch (OperationCanceledException)
        {
            // 跑局结束，静默退出。
        }
        catch (RunAutoTimeoutException ex)
        {
            RunAutoController.Session?.LogDecision($"商店处理超时：{ex.Message}");
        }
        finally
        {
            _active = false;
        }
    }

    /// <summary>购买入口评分。卡牌走 CardPickerAI，遗物走 RelicPickerAI，药水看空槽，移除看牌组污染。</summary>
    private static float ScoreSlot(
        NMerchantSlot slot,
        DeckContext deckContext,
        bool hasPotionSlots,
        Player? player)
    {
        if (slot is NMerchantCardRemoval)
        {
            if (player == null)
                return 0f;
            bool hasCurse = HasCurseOrStatus(player);
            if (hasCurse)
                return 28f;
            return deckContext.DeckSize >= 15 ? 16f : 10f;
        }

        if (slot.Entry is MerchantCardEntry cardEntry && cardEntry.CreationResult?.Card is { } card)
        {
            if (card.Type is CardType.Status or CardType.Curse)
                return -20f;
            float value = CardPickerAI.Evaluate(card, deckContext);
            return value >= 6f ? value : -100f;
        }

        if (slot.Entry is MerchantRelicEntry relicEntry && relicEntry.Model is { } relic)
            return RelicPickerAI.Score(relic) >= 6f ? RelicPickerAI.Score(relic) : -100f;

        if (slot.Entry is MerchantPotionEntry potionEntry && potionEntry.Model is { } potion)
        {
            if (!hasPotionSlots)
                return -100f;
            float value = potion.Rarity switch
            {
                PotionRarity.Rare => 9f,
                PotionRarity.Uncommon => 6f,
                _ => 3f,
            };
            return value >= 5f ? value : -100f;
        }

        return -100f;
    }

    private static bool HasCurseOrStatus(Player player)
    {
        IReadOnlyList<CardModel>? deck = PileType.Deck.GetPile(player).Cards;
        if (deck == null)
            return false;
        foreach (CardModel card in deck)
        {
            if (card.Type is CardType.Status or CardType.Curse)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 启动购买任务；若它打开覆盖层（卡牌移除的 NDeckCardSelectScreen）则边等边处理，
    /// 完成后等待任务结束。普通购买（卡/遗物/药水）不打开覆盖层，循环立即退出。
    /// </summary>
    private static async Task PurchaseDrainingOverlaysAsync(MerchantEntry entry, MerchantInventory? inventory, CancellationToken token)
    {
        Task<bool> purchase = entry.OnTryPurchaseWrapper(inventory);
        while (!purchase.IsCompleted)
        {
            if (NOverlayStack.Instance?.Peek() is NDeckCardSelectScreen removal
                && GodotObject.IsInstanceValid(removal))
            {
                await HandleCardRemovalScreenAsync(removal, token);
            }
            else
            {
                await Task.Delay(60, token);
            }
        }
        await purchase;
    }

    /// <summary>卡牌移除屏幕：优先移除诅咒/状态，否则移除评分最低的牌，然后确认。</summary>
    private static async Task HandleCardRemovalScreenAsync(NDeckCardSelectScreen screen, CancellationToken token)
    {
        List<NGridCardHolder> holders = [];
        await RunUiHelper.WaitUntilAsync(
            () => (holders = RunUiHelper.FindAll<NGridCardHolder>(screen)).Count > 0,
            token,
            TimeSpan.FromSeconds(5),
            "移除选牌未出现");

        NGridCardHolder? target = null;
        float targetScore = float.MaxValue;
        Player? player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        DeckContext context = DeckContext.From(player, RunAutoController.Session?.RunState);
        foreach (NGridCardHolder holder in holders)
        {
            CardModel? card = holder.CardModel;
            if (card == null)
                continue;
            float score = card.Type is CardType.Status or CardType.Curse
                ? -1000f
                : CardPickerAI.Evaluate(card, context);
            if (score < targetScore)
            {
                targetScore = score;
                target = holder;
            }
        }
        if (target == null)
            return;

        target.EmitSignal(NCardHolder.SignalName.Pressed, target);

        // 选 1 张时预览自动弹出；若没弹，点主确认。
        NConfirmButton? confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        await RunUiHelper.WaitUntilAsync(
            () =>
            {
                Control? preview = screen.GetNodeOrNull<Control>("%PreviewContainer");
                return preview != null && preview.Visible;
            },
            token,
            TimeSpan.FromSeconds(5),
            "移除选牌预览未出现");

        NConfirmButton? previewConfirm = screen.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
        await RunUiHelper.WaitUntilAsync(
            () => previewConfirm != null && previewConfirm.IsEnabled,
            token,
            TimeSpan.FromSeconds(5),
            "移除确认按钮未就绪");
        if (previewConfirm != null && previewConfirm.IsEnabled)
            await RunUiHelper.ClickAsync(previewConfirm, 150);

        await RunUiHelper.WaitUntilAsync(
            () => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            token,
            TimeSpan.FromSeconds(10),
            "移除选牌屏幕未关闭");
    }
}
