using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Restory.Gameplay.Delivery;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Inventory;
using Restory.Gameplay.InventoryNotification;
using Restory.StorageSystem;
using Restory.StorageSystem.StorageElements;
using Restory.Data.Elements;
using UnityEngine;

namespace RestoryTweaks
{
    // Move delivered parts straight into the parts box, instead of leaving them in the delivery
    // box to be carried across by hand.
    //
    // DeliveryService raises OnDeliveryArrived once a delivery lands, and its DeliveryBox exposes
    // ContainedElements. The parts box is IInventory.StorageElements, which only accepts
    // StorageItemElement - so each HeldElement is wrapped in one and added with its own amount.
    //
    // Only ELEMENTS are moved. Palettes, sticker packs, parts boxes and whole devices are left in
    // the delivery box: they aren't parts, and some of them are physical objects you're meant to
    // pick up.
    public static class DeliveryToPartsBoxConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Notify;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Delivery", "PartsStraightToPartsBox", true,
                "Move delivered parts into your parts box as soon as they arrive.");
            Notify = cfg.Bind("Delivery", "ShowNotification", true,
                "Show the game's usual on-screen parts notification for what was moved.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class PartsBox
    {
        private static Inventory _inventory;
        private static InventoryNotificationService _notifications;

        public static StorageElasticElements Storage
        {
            get
            {
                if (_inventory == null) _inventory = UnityEngine.Object.FindObjectOfType<Inventory>();
                return _inventory != null ? _inventory.StorageElements : null;
            }
        }

        // The game's own "you received these parts" popup - reusing it means the feedback looks
        // native rather than being a second, competing notification style.
        public static InventoryNotificationService Notifications
        {
            get
            {
                if (_notifications == null)
                    _notifications = UnityEngine.Object.FindObjectOfType<InventoryNotificationService>();
                return _notifications;
            }
        }

        public static void Announce(IEnumerable<HeldElement> elements)
        {
            try
            {
                if (!DeliveryToPartsBoxConfig.Notify.Value) return;
                var list = elements as IList<HeldElement> ?? elements.ToList();
                if (list.Count == 0) return;

                var service = Notifications;
                if (service != null) service.ShowElements(list);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Delivery] notification failed: {e.Message}"); }
        }

        // The slot holding this part, or null if the box hasn't got one.
        public static IReadOnlyStorageSlot FindSlot(IElementInfo element)
        {
            var storage = Storage;
            if (storage == null || element == null) return null;

            for (int i = 0; i < storage.Size; i++)
            {
                var slot = storage[i];
                if (slot == null || slot.IsEmpty() || slot.Count <= 0) continue;
                if (!(slot.Item is StorageItemElement item)) continue;
                if (item.Info == element) return slot;
            }
            return null;
        }

        // Take one out of the box and put it on the work surface.
        //
        // This hands off to the game's own DropItemsFromStorage rather than placing the element
        // directly. Building it by hand produced a part that looked right but ignored the mouse:
        // the real routine also runs a placement controller to find a free spot, clones the element
        // data, marks it inspected and - the part that actually matters for interaction - calls
        // BehaviorSwitcher.SwitchToPlacedBehavior(). Reproducing all of that here would just be a
        // worse copy of it.
        //
        // Clearing the whole slot is correct here, not a bug to work around: StorageItemElement
        // returns false from CanStackWith, so elements never stack and a slot holds exactly one
        // part. The game's drop service clears the slot for the same reason.
        public static bool TakeOutOntoTable(IReadOnlyStorageSlot slot)
        {
            try
            {
                var storage = Storage;
                var service = ElementServiceRef;
                if (storage == null || slot == null || slot.IsEmpty()) return false;
                if (!(slot.Item is StorageItemElement)) return false;
                if (service == null) { Plugin.Log.LogWarning("[OrderParts] Element service not ready."); return false; }

                // Snapshot before the coroutine runs; the slot is only cleared once it's finished.
                int index = slot.Index;

                Action onPost = null;
                onPost = delegate
                {
                    service.OnPostDrop -= onPost;
                    try { storage.ClearItem(index); }
                    catch (Exception e) { Plugin.Log.LogError($"[OrderParts] stock update failed: {e.Message}"); }
                };

                service.OnPostDrop += onPost;
                service.DropItemsFromStorage(new[] { slot });
                return true;
            }
            catch (Exception e) { Plugin.Log.LogError($"[OrderParts] take-out failed: {e.Message}"); return false; }
        }

        private static ElementService _elementService;

        public static ElementService ElementServiceRef
        {
            get
            {
                if (_elementService == null) _elementService = UnityEngine.Object.FindObjectOfType<ElementService>();
                return _elementService;
            }
        }

        // Put parts in the box. Returns how many individual parts actually went in.
        public static int Deposit(IEnumerable<HeldElement> elements)
        {
            int moved = 0;
            var storage = Storage;
            if (storage == null) { Plugin.Log.LogWarning("[Delivery] No parts box found."); return 0; }

            // StorageElastic grows itself until everything fits and always reports nothing left
            // over, so there's no "box full" case to handle - parts can't be lost this way. Each
            // part takes its own slot, since elements don't stack.
            foreach (var held in elements)
            {
                if (held == null || held.ElementData == null) continue;
                try
                {
                    storage.AddItem(new StorageItemElement(held.ElementData), held.HeldAmount);
                    moved += held.HeldAmount;
                }
                catch (Exception e) { Plugin.Log.LogError($"[Delivery] deposit failed: {e.Message}"); }
            }
            return moved;
        }
    }

    // OnDeliveryArrived is an event rather than a method, so it's subscribed to rather than patched.
    // The service is a MonoBehaviour, so it can simply be found once it exists.
    public class DeliveryWatcher : MonoBehaviour
    {
        private DeliveryService _service;
        private float _next;

        private void Update()
        {
            if (_service != null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            _service = UnityEngine.Object.FindObjectOfType<DeliveryService>();
            if (_service == null) return;

            _service.OnDeliveryArrived += OnArrived;
            Plugin.Log.LogInfo("[Delivery] Watching for deliveries.");
        }

        private void OnDestroy()
        {
            if (_service != null) _service.OnDeliveryArrived -= OnArrived;
        }

        private void OnArrived()
        {
            try
            {
                if (!DeliveryToPartsBoxConfig.On || _service == null) return;

                var box = _service.DeliveryBox;
                if (box == null) return;

                // Copy first: clearing the box while enumerating its own list would be a mistake,
                // and we need the list afterwards for the notification.
                var elements = box.ContainedElements.ToList();
                if (elements.Count == 0) return;

                int moved = PartsBox.Deposit(elements);
                if (moved <= 0) { Plugin.Log.LogWarning("[Delivery] Nothing was deposited; leaving the delivery box alone."); return; }

                // Safe to empty now the parts are in. This only guards against the deposit throwing
                // outright - the storage itself can't refuse, since it grows to fit.
                box.ClearElements();

                PartsBox.Announce(elements);
                Plugin.Log.LogInfo($"[Delivery] Moved {moved} part(s) into the parts box.");
            }
            catch (Exception e) { Plugin.Log.LogError($"[Delivery] {e}"); }
        }
    }
}
