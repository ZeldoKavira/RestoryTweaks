using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Data.Elements;
using Restory.Gameplay.Elements;
using Restory.Data.Shops.Elements;
using Restory.Gameplay.Shops.Elements;
using Restory.UI.Presenters.Notepad;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RestoryTweaks
{
    // Double-click a part in the notepad (the parts list for the device on the repair table) to put
    // it in the elements shop's basket, instead of reading off the list and hunting for each part
    // in the shop by hand.
    //
    // The notepad item already knows which ElementInfo it represents, and every shop listing is an
    // ElementsShopItemData wrapping that same ElementInfo — so matching one to the other is a
    // direct comparison rather than any name or id guesswork.
    public static class OrderPartConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Quantity;
        internal static ConfigEntry<bool> OnlyMissing;
        internal static ConfigEntry<bool> BuyImmediately;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("OrderParts", "Enabled", true,
                "Double-click a part on the repair table's notepad to add it to the parts shop basket.");
            Quantity = cfg.Bind("OrderParts", "QuantityPerDoubleClick", 1,
                new ConfigDescription("How many to add each time.",
                    new AcceptableValueRange<int>(1, 20)));
            BuyImmediately = cfg.Bind("OrderParts", "BuyImmediately", true,
                "Buy the part straight away instead of only adding it to the basket. Anything " +
                "already in your basket is set aside and put back, so only the part you clicked " +
                "is bought.");
            OnlyMissing = cfg.Bind("OrderParts", "OnlyMissingParts", true,
                "Only respond for parts the notepad flags as missing, so double-clicking a part " +
                "you already have does nothing. Turn off to also stock up on parts you have.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    // ---------------------------------------------------------------- reaching the game's systems

    internal static class Shop
    {
        // ElementsShopInteractor is a plain class built by Zenject, so there's no singleton to look
        // up and FindObjectOfType won't see it. Capturing it as it's constructed is the reliable
        // way in, and it's built once at load.
        public static ElementsShopInteractor Interactor;

        private static ElementsShopService _service;

        // The service IS a MonoBehaviour, so this one can just be found.
        public static ElementsShopService Service
        {
            get
            {
                if (_service == null) _service = UnityEngine.Object.FindObjectOfType<ElementsShopService>();
                return _service;
            }
        }

        // The shop listing for a given part, or null if it isn't sold (or isn't unlocked yet).
        public static ElementsShopItemData FindListing(ElementInfo element)
        {
            if (element == null || Service == null) return null;

            // GetAllowedElementItems respects licences and unlocks, so an item you couldn't buy in
            // the shop UI isn't silently added to the basket here either.
            foreach (var item in Service.GetAllowedElementItems())
                if (item != null && item.Element == element) return item;

            return null;
        }
    }

    // Patched by hand rather than by attribute.
    //
    // [HarmonyPatch(type, MethodType.Constructor)] with no argument types means the PARAMETERLESS
    // constructor, which this class doesn't have - the target resolves to null and the patch fails.
    // Listing the six parameter types instead would hard-code namespaces that are easy to get wrong
    // (and were), so the constructor is looked up reflectively.
    internal static class InteractorHook
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                var ctors = typeof(ElementsShopInteractor).GetConstructors();
                if (ctors.Length == 0)
                {
                    Plugin.Log.LogError("[OrderParts] ElementsShopInteractor has no public constructor.");
                    return;
                }
                if (ctors.Length > 1)
                    Plugin.Log.LogWarning($"[OrderParts] {ctors.Length} constructors; hooking them all.");

                var postfix = new HarmonyMethod(typeof(InteractorHook).GetMethod(nameof(Captured),
                    BindingFlags.Static | BindingFlags.NonPublic));

                foreach (var ctor in ctors) harmony.Patch(ctor, postfix: postfix);
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] couldn't hook the shop: {e.Message}"); }
        }

        private static void Captured(ElementsShopInteractor __instance)
        {
            Shop.Interactor = __instance;
            Plugin.Log.LogInfo("[OrderParts] Hooked the parts shop.");
        }
    }

    // ---------------------------------------------------------------- the click handling

    // The notepad item's view only implements pointer ENTER/EXIT for its tooltip - there's no click
    // handling to hook - so this adds it.
    public class NotepadItemClickCatcher : MonoBehaviour, IPointerClickHandler
    {
        internal GUI_NotepadElementItem Item;

        public void OnPointerClick(PointerEventData eventData)
        {
            try
            {
                if (!OrderPartConfig.On || eventData == null) return;
                if (eventData.clickCount != 2) return;      // single clicks keep their normal meaning

                OrderParts.Order(Item);
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] click failed: {e.Message}"); }
        }
    }

    internal static class OrderParts
    {
        public static void Order(GUI_NotepadElementItem item)
        {
            if (item == null) return;

            var interactor = Shop.Interactor;
            if (interactor == null) { Plugin.Log.LogWarning("[OrderParts] Parts shop isn't ready yet."); return; }

            if (OrderPartConfig.OnlyMissing.Value && item.View != null && !item.View.IsElementMissed)
            {
                Plugin.Log.LogInfo("[OrderParts] That part isn't missing; ignoring (OnlyMissingParts is on).");
                return;
            }

            var element = item.Info;

            // Already own one? Take it out of the parts box and put it on the table instead of
            // buying another - that's what you'd do by hand, and it costs nothing.
            var slot = PartsBox.FindSlot(element);
            if (slot != null)
            {
                if (PartsBox.TakeOutOntoTable(slot))
                {
                    Plugin.Log.LogInfo($"[OrderParts] Took {Describe(element)} from the parts box " +
                                       $"onto the table.");
                    return;
                }
                Plugin.Log.LogWarning("[OrderParts] Couldn't take it from the parts box; buying instead.");
            }

            var listing = Shop.FindListing(element);
            if (listing == null)
            {
                Plugin.Log.LogInfo("[OrderParts] That part isn't available in the shop.");
                return;
            }

            int qty = Mathf.Max(1, OrderPartConfig.Quantity.Value);

            if (!OrderPartConfig.BuyImmediately.Value)
            {
                // Basket-only mode: just add and leave checkout to the player.
                int current = interactor.GetItemCountInShoppingCart(listing);
                interactor.SetItemCountInShoppingCart(listing, current + qty);
                Plugin.Log.LogInfo($"[OrderParts] Basket: {Describe(element)} " +
                                   $"x{interactor.GetItemCountInShoppingCart(listing)} " +
                                   $"({interactor.GetTotalItemsCostInShoppingCart()} total).");
                return;
            }

            Buy(interactor, listing, element, qty);
        }

        // Buy JUST this part.
        //
        // TryToBuyAllItemsFromShoppingCart does what it says - it buys the entire basket, licences
        // included - so checking out on top of whatever the player had queued would spend their
        // money on things they hadn't confirmed. Instead the basket is set aside, ours is bought
        // alone, and their basket is put back exactly as it was.
        private static void Buy(ElementsShopInteractor interactor, ElementsShopItemData listing,
                                ElementInfo element, int qty)
        {
            var savedItems = new Dictionary<ElementsShopItemData, int>();
            var savedLicenses = new List<LicenseShopItemData>();

            try
            {
                foreach (var item in interactor.AllItemsInShoppingCart)
                    if (item != null) savedItems[item] = interactor.GetItemCountInShoppingCart(item);
                savedLicenses.AddRange(interactor.AllLicensesInShoppingCart);
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] couldn't read the basket: {e.Message}"); return; }

            bool bought = false;
            try
            {
                interactor.ClearShoppingCart();
                interactor.SetItemCountInShoppingCart(listing, qty);

                int cost = interactor.GetAvailableTotalItemsCostInShoppingCart();
                bought = interactor.TryToBuyAllItemsFromShoppingCart();

                if (bought)
                {
                    Plugin.Log.LogInfo($"[OrderParts] Ordered {Describe(element)} x{qty} for {cost}.");

                    // The banner, not the parts popup: that popup means "these have arrived", and
                    // showing it at the moment of ordering claimed something that hadn't happened -
                    // the parts are still in delivery at this point.
                    string shown = Toast.NameOf(element);
                    Toast.Show(qty > 1 ? $"Ordered {qty}x {shown}" : $"Ordered {shown}");
                }
                else
                {
                    Plugin.Log.LogInfo($"[OrderParts] Couldn't order {Describe(element)} - " +
                                       $"not enough money, or it's out of stock ({cost} needed).");
                    Toast.Show($"Couldn't order {Toast.NameOf(element)}");
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] order failed: {e.Message}"); }
            finally
            {
                // Always put the player's basket back, including when the purchase failed or threw.
                try
                {
                    interactor.ClearShoppingCart();
                    foreach (var kv in savedItems) interactor.SetItemCountInShoppingCart(kv.Key, kv.Value);
                    foreach (var lic in savedLicenses) interactor.TryToAddLicenseToShoppingCart(lic);

                    if (savedItems.Count > 0 || savedLicenses.Count > 0)
                        Plugin.Log.LogInfo($"[OrderParts] Restored your basket " +
                                           $"({savedItems.Count} item type(s), {savedLicenses.Count} licence(s)).");
                }
                catch (Exception e) { Plugin.Log.LogError($"[OrderParts] couldn't restore the basket: {e.Message}"); }
            }
        }

        private static string Describe(ElementInfo element)
        {
            try { return element != null ? element.name : "part"; }
            catch { return "part"; }
        }
    }

    // Attach the click catcher as each notepad row is set up. Init runs every time a row is
    // (re)used from the pool, so guard against adding a second one.
    [HarmonyPatch(typeof(GUI_NotepadElementItem), "Init")]
    public static class Patch_NotepadItem_Init
    {
        private static void Postfix(GUI_NotepadElementItem __instance)
        {
            try
            {
                if (__instance == null || __instance.View == null) return;

                var go = __instance.View.gameObject;
                var catcher = go.GetComponent<NotepadItemClickCatcher>();
                if (catcher == null) catcher = go.AddComponent<NotepadItemClickCatcher>();
                catcher.Item = __instance;
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] attach failed: {e.Message}"); }
        }
    }
}
