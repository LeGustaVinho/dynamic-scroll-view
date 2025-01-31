using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LegendaryTools.UI
{
    [RequireComponent(typeof(ScrollRect))]
    public abstract class DynamicScrollView<TGameObject, TData> : MonoBehaviour, IDisposable
        where TGameObject : Component, DynamicScrollView<TGameObject, TData>.IListingItem
        where TData : class
    {
        public interface IListingItem
        {
            void Init(TData item);
            void UpdateUI(TData item);
        }

        public event Action<TGameObject, TData> OnCreateItem;
        public event Action<TGameObject, TData> OnRemoveItem;
        public event Action<List<TGameObject>> OnCompleteListingGeneration;

        /// <summary>
        /// Current set of data for this scroll view.
        /// </summary>
        public List<TData> DataSource { get; } = new List<TData>();

        /// <summary>
        /// Returns the list of currently active/instantiated listing items.
        /// </summary>
        public List<TGameObject> Listing
        {
            get
            {
                List<TGameObject> listView = new List<TGameObject>(itemsAtSlot.Values);
                return listView;
            }
        }

        [Header("References")]
        public Canvas MainCanvas;
        public ScrollRect ScrollRect;
        public TGameObject Prefab;

        [Header("Settings")]
        public bool CanOverrideItemRectTransform = false;
        public bool DebugMode = false;

        [Header("Slots")]
        [Tooltip("Number of slots to create each frame when increasing slot count.")]
        public int SlotNumInstantiateCallsPerFrame = 10;

        [Tooltip("How many slots outside the screen bounds (viewport) we want to render.")]
        public Vector2 ItemBufferCount;

        // Private fields
        private RectTransform slotPrefab;
        private readonly List<RectTransform> slots = new List<RectTransform>();
        private Coroutine generateRoutine;
        private Coroutine scrollToRoutine;
        private int pendingScrollToIndex = -1;
        private bool isInit;
        private bool isGenerating;
        private RectTransform rectTransform;
        private Rect viewportRect;
        private readonly Vector3[] bufferCorners = new Vector3[4];
        private Rect bufferRect;
        private RectTransform prefabRectTransform;

        // Maps slot index -> item instance
        private readonly Dictionary<int, TGameObject> itemsAtSlot = new Dictionary<int, TGameObject>();

        private const string SLOT_PREFAB = "SlotPrefab";

        #region Public API

        /// <summary>
        /// Creates slots/items to match the provided data and populates them as needed.
        /// </summary>
        public void Generate(TData[] data)
        {
            if (data == null)
                data = Array.Empty<TData>();

            isGenerating = true;
            Initialize();
            // Destroy old items (not the slots)
            DestroyAllItems();

            DataSource.Clear();
            DataSource.AddRange(data);

            if (gameObject.activeInHierarchy && ScrollRect != null)
            {
                // Stop old routines if any
                if (generateRoutine != null) StopCoroutine(generateRoutine);
                generateRoutine = StartCoroutine(GenerateView(DataSource.ToArray()));
            }
        }

        /// <summary>
        /// Refreshes a single item if it exists in the listing.
        /// </summary>
        public void Refresh(TData itemData)
        {
            if (itemData == null) return;

            int index = DataSource.FindIndex(item => item == itemData);
            if (index >= 0 && itemsAtSlot.TryGetValue(index, out TGameObject itemAtSlot))
            {
                itemAtSlot.UpdateUI(itemData);
            }
        }

        /// <summary>
        /// Refreshes all items that match the given subset.
        /// </summary>
        public void RefreshAll(TData[] data)
        {
            if (data == null) return;
            foreach (TData entry in data)
            {
                Refresh(entry);
            }
        }

        /// <summary>
        /// Refreshes the entire list in place, using the existing <see cref="DataSource"/>.
        /// </summary>
        public void RefreshAll()
        {
            foreach (var kvp in itemsAtSlot)
            {
                if (kvp.Key >= 0 && kvp.Key < DataSource.Count)
                {
                    kvp.Value.UpdateUI(DataSource[kvp.Key]);
                }
            }
        }

        /// <summary>
        /// Smoothly (or instantly) moves scroll so that the given data item is centered/focused if it is found.
        /// </summary>
        public void ScrollTo(TData itemToFocus)
        {
            if (itemToFocus == null) return;

            int slotIndex = DataSource.FindIndex(item => item == itemToFocus);
            if (slotIndex >= 0)
            {
                ScrollToIndex(slotIndex);
            }
        }

        /// <summary>
        /// Scroll to the first item (if any).
        /// </summary>
        public void ScrollToBeginning()
        {
            if (DataSource.Count == 0) return;
            ScrollToIndex(0);
        }

        /// <summary>
        /// Scroll to the last item (if any).
        /// </summary>
        public void ScrollToEnd()
        {
            if (DataSource.Count == 0) return;
            // We'll just scroll to the last valid slot
            ScrollToIndex(int.MaxValue);
        }

        /// <summary>
        /// Clears the pool for this prefab (if you maintain a static pool).
        /// </summary>
        public void Dispose()
        {
            Pool.ClearPool(Prefab);
        }

        /// <summary>
        /// Destroy all currently visible/instantiated items in the scroll view (but not the underlying slots).
        /// </summary>
        public void DestroyAllItems()
        {
            foreach (var kv in itemsAtSlot)
            {
                int slotIndex = kv.Key;
                TGameObject itemInSlot = kv.Value;

                if (slotIndex >= 0 && slotIndex < DataSource.Count)
                {
                    OnItemRemoved(itemInSlot, DataSource[slotIndex]);
                    OnRemoveItem?.Invoke(itemInSlot, DataSource[slotIndex]);
                }
                Pool.Destroy(itemInSlot);
            }
            itemsAtSlot.Clear();
        }

        #endregion

        #region Unity Events

        protected virtual void Reset()
        {
            // Auto-assign
            if (!ScrollRect) ScrollRect = GetComponent<ScrollRect>();
        }

        protected virtual void Start()
        {
            Initialize();
        }

        protected virtual void OnEnable()
        {
            // Re-generate if the data source has changed or if slot counts mismatch
            if (ScrollRect != null && slots.Count != DataSource.Count)
            {
                if (generateRoutine != null) StopCoroutine(generateRoutine);
                generateRoutine = StartCoroutine(GenerateView(DataSource.ToArray()));
            }

            // If we had a pending scroll index
            if (pendingScrollToIndex >= 0 && ScrollRect != null)
            {
                if (scrollToRoutine != null) StopCoroutine(scrollToRoutine);
                scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(pendingScrollToIndex));
            }
        }

        protected virtual void OnDisable()
        {
            // Stop coroutines if this gets disabled
            if (generateRoutine != null)
            {
                StopCoroutine(generateRoutine);
                generateRoutine = null;
            }

            if (scrollToRoutine != null)
            {
                StopCoroutine(scrollToRoutine);
                scrollToRoutine = null;
            }
        }

        protected virtual void OnDestroy()
        {
            if (generateRoutine != null)
            {
                StopCoroutine(generateRoutine);
                generateRoutine = null;
            }

            if (scrollToRoutine != null)
            {
                StopCoroutine(scrollToRoutine);
                scrollToRoutine = null;
            }

            // Destroy slot containers
            foreach (RectTransform slot in slots)
            {
                if (slot != null) Pool.Destroy(slot);
            }
            slots.Clear();

            // Clear items
            DestroyAllItems();

            // Remove ScrollRect listeners
            if (ScrollRect != null)
            {
                ScrollRect.onValueChanged.RemoveListener(OnScrollRectChange);
            }

            // Destroy the internal slot prefab used for pooling
            if (slotPrefab != null)
            {
                Destroy(slotPrefab.gameObject);
                slotPrefab = null;
            }
        }

        /// <summary>
        /// Called when a new item (instance of <see cref="TGameObject"/>) is created.
        /// </summary>
        protected virtual void OnItemCreated(TGameObject item, TData data)
        {
            // Child classes can override.
        }

        /// <summary>
        /// Called right before an item is removed or destroyed.
        /// </summary>
        protected virtual void OnItemRemoved(TGameObject item, TData data)
        {
            // Child classes can override.
        }

        #endregion

        #region Core Logic

        public void Initialize()
        {
            if (isInit) return;

            rectTransform = GetComponent<RectTransform>();

            // Ensure we have a main canvas
            if (MainCanvas == null)
            {
                MainCanvas = rectTransform.GetComponentInParent<Canvas>();
                if (!MainCanvas)
                {
                    Debug.LogWarning(
                        $"[{nameof(DynamicScrollView<TGameObject, TData>)}] No parent canvas found. Some features may not work correctly."
                    );
                }
            }

            // Ensure we have a slot prefab
            if (!slotPrefab)
            {
                GameObject newSlotPrefabGo = new GameObject(SLOT_PREFAB, typeof(RectTransform), typeof(GameObjectPoolReference));
                slotPrefab = newSlotPrefabGo.GetComponent<RectTransform>();
            }

            prefabRectTransform = Prefab != null ? Prefab.GetComponent<RectTransform>() : null;

            if (ScrollRect != null)
            {
                ScrollRect.onValueChanged.AddListener(OnScrollRectChange);
            }

            isInit = true;
        }

        private IEnumerator GenerateView(TData[] data)
        {
            // Make sure the object is still active
            if (!gameObject.activeInHierarchy || ScrollRect == null)
            {
                isGenerating = false;
                generateRoutine = null;
                yield break;
            }

            // Possibly add or remove slots to match the new data count
            int dataCount = data.Length;
            int currentSlotCount = slots.Count;

            if (dataCount > currentSlotCount)
            {
                // We need more slots
                int neededSlots = dataCount - currentSlotCount;
                while (neededSlots > 0)
                {
                    int slotsToCreate = Mathf.Clamp(neededSlots, 0, SlotNumInstantiateCallsPerFrame);
                    for (int i = 0; i < slotsToCreate; i++)
                    {
                        RectTransform newSlot = Pool.Instantiate(slotPrefab);
                        newSlot.SetParent(ScrollRect.content);
                        newSlot.localPosition = Vector3.zero;
                        newSlot.localScale = Vector3.one;
                        newSlot.localRotation = Quaternion.identity;

                        // Set size same as the prefab (if available)
                        if (prefabRectTransform != null)
                            newSlot.sizeDelta = prefabRectTransform.sizeDelta;

                        slots.Add(newSlot);
                    }

                    // Force a layout rebuild so subsequent checks for positions are correct
                    LayoutRebuilder.ForceRebuildLayoutImmediate(ScrollRect.content);

                    yield return null; // Wait a frame
                    yield return null; // Possibly let the layout system update

                    UpdateVisibility();

                    neededSlots = dataCount - slots.Count;
                }
            }
            else if (dataCount < currentSlotCount)
            {
                // We have extra slots; remove them
                int slotsToRemove = currentSlotCount - dataCount;
                for (int i = 0; i < slotsToRemove; i++)
                {
                    int lastIndex = slots.Count - 1;
                    RectTransform slotToDestroy = slots[lastIndex];
                    slots.RemoveAt(lastIndex);
                    Pool.Destroy(slotToDestroy);
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(ScrollRect.content);
                yield return null;

                UpdateVisibility();
            }
            else
            {
                // Same slot count
                LayoutRebuilder.ForceRebuildLayoutImmediate(ScrollRect.content);
                yield return null;

                UpdateVisibility();
            }

            isGenerating = false;
            generateRoutine = null;
            OnCompleteListingGeneration?.Invoke(Listing);
        }

        private IEnumerator WaitGenerateAndScrollTo(int index)
        {
            // Wait until generation has completed
            yield return new WaitUntil(() => !isGenerating);

            ScrollTo(index);
            pendingScrollToIndex = -1;
            scrollToRoutine = null;
        }

        private void ScrollToIndex(int index)
        {
            if (isGenerating)
            {
                // If still generating, store the index for later
                if (gameObject.activeInHierarchy)
                {
                    if (scrollToRoutine != null)
                        StopCoroutine(scrollToRoutine);
                    scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(index));
                }
                else
                {
                    pendingScrollToIndex = index;
                }
            }
            else
            {
                // Directly scroll
                ScrollTo(index);
            }
        }

        private void ScrollTo(int index)
        {
            if (slots.Count == 0 || ScrollRect == null) return;

            index = Mathf.Clamp(index, 0, slots.Count - 1);
            RectTransform target = slots[index];
            if (!target) return;

            // Force a quick layout rebuild to ensure positions are correct
            LayoutRebuilder.ForceRebuildLayoutImmediate(ScrollRect.content);

            Vector2 viewportHalfSize = ScrollRect.viewport.rect.size * 0.5f;
            Vector2 contentSize = ScrollRect.content.rect.size;

            // Get target position inside the content (in content-space)
            Vector3 targetRelativePosition = ScrollRect.content.InverseTransformPoint(target.position);

            // Shift by a small factor to center the item (adjust for your orientation)
            // For purely vertical lists, you may only want to modify 'y'.
            // For purely horizontal lists, you may only want to modify 'x'.
            Vector3 targetSizeOffset = new Vector3(
                target.rect.size.x * 0.5f,
                target.rect.size.y * 0.5f,
                0f
            );
            targetRelativePosition += targetSizeOffset;

            // Normalized position inside content
            // The formula can differ depending on horizontal vs vertical orientation
            Vector2 normalizedPosition = new Vector2(
                Mathf.Clamp01(targetRelativePosition.x / (contentSize.x - viewportHalfSize.x)),
                1f - Mathf.Clamp01(targetRelativePosition.y / -(contentSize.y - viewportHalfSize.y))
            );

            // Center offset (normalized)
            Vector2 normalizedOffsetPosition =
                new Vector2(viewportHalfSize.x / contentSize.x, viewportHalfSize.y / contentSize.y);

            // Adjust final
            normalizedPosition.x = Mathf.Clamp01(normalizedPosition.x - (1f - normalizedPosition.x) * normalizedOffsetPosition.x);
            normalizedPosition.y = Mathf.Clamp01(normalizedPosition.y + normalizedPosition.y * normalizedOffsetPosition.y);

            ScrollRect.normalizedPosition = normalizedPosition;
            UpdateVisibility();
        }

        private void OnScrollRectChange(Vector2 scrollDelta)
        {
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            if (!ScrollRect) return;

            UpdateViewportRect();

            // Decide which slots are visible, and create/destroy items accordingly
            for (int i = 0; i < slots.Count; i++)
            {
                if (IsVisible(viewportRect, slots[i]))
                {
                    CreateItemAt(i);
                }
                else
                {
                    DestroyItemAt(i);
                }
            }
        }

        /// <summary>
        /// Creates (if needed) an item at the given slot index.
        /// </summary>
        private void CreateItemAt(int index)
        {
            if (index < 0 || index >= slots.Count) return;
            if (index >= DataSource.Count) return;
            if (itemsAtSlot.ContainsKey(index)) return; // Already created

            // Instantiate item
            TGameObject newItem = Pool.Instantiate(Prefab);
            RectTransform newItemRT = newItem.GetComponent<RectTransform>();
            RectTransform prefabRT = Prefab.GetComponent<RectTransform>();

            var parentSlot = slots[index];
            newItemRT.SetParent(parentSlot);
            newItemRT.localPosition = prefabRT.localPosition;
            newItemRT.localScale = prefabRT.localScale;
            newItemRT.localRotation = prefabRT.localRotation;

            if (CanOverrideItemRectTransform)
            {
                newItemRT.pivot = new Vector2(0.5f, 0.5f);
                newItemRT.anchoredPosition = Vector3.zero;
                newItemRT.anchorMin = Vector2.zero;
                newItemRT.anchorMax = Vector2.one;
                newItemRT.offsetMin = Vector2.zero;
                newItemRT.offsetMax = Vector2.zero;
            }

            itemsAtSlot.Add(index, newItem);

            // Initialize the item
            newItem.Init(DataSource[index]);
            OnItemCreated(newItem, DataSource[index]);
            OnCreateItem?.Invoke(newItem, DataSource[index]);
        }

        /// <summary>
        /// Destroys (if exists) the item at the given slot index.
        /// </summary>
        private void DestroyItemAt(int index)
        {
            if (!itemsAtSlot.TryGetValue(index, out TGameObject viewItem)) return;
            itemsAtSlot.Remove(index);

            if (index >= 0 && index < DataSource.Count)
            {
                OnItemRemoved(viewItem, DataSource[index]);
                OnRemoveItem?.Invoke(viewItem, DataSource[index]);
            }
            Pool.Destroy(viewItem);
        }

        /// <summary>
        /// Recomputes the viewport rect in local/canvas space, with an additional item-buffer area.
        /// </summary>
        private void UpdateViewportRect()
        {
            if (!MainCanvas)
            {
                // Attempt a fallback if possible
                MainCanvas = GetComponentInParent<Canvas>();
            }

            if (!ScrollRect || !ScrollRect.viewport || !MainCanvas)
                return;

            RectTransform viewport = ScrollRect.viewport;
            viewport.GetWorldCorners(bufferCorners);
            for (int j = 0; j < bufferCorners.Length; j++)
            {
                bufferCorners[j] = MainCanvas.transform.InverseTransformPoint(bufferCorners[j]);
            }

            float minX = Mathf.Min(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float maxX = Mathf.Max(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float minY = Mathf.Min(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
            float maxY = Mathf.Max(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);

            float width = maxX - minX;
            float height = maxY - minY;

            // If we have at least one slot, estimate item size from the first slot
            if (slots.Count > 0)
            {
                slots[0].GetWorldCorners(bufferCorners);
                for (int j = 0; j < bufferCorners.Length; j++)
                {
                    bufferCorners[j] = MainCanvas.transform.InverseTransformPoint(bufferCorners[j]);
                }

                float slotWidth = Mathf.Abs(bufferCorners[2].x - bufferCorners[1].x);
                float slotHeight = Mathf.Abs(bufferCorners[1].y - bufferCorners[0].y);

                // Expand by buffer
                width += ItemBufferCount.x * slotWidth * 2;
                height += ItemBufferCount.y * slotHeight * 2;
                minX -= ItemBufferCount.x * slotWidth;
                minY -= ItemBufferCount.y * slotHeight;
            }

            viewportRect = new Rect(minX, minY, width, height);
        }

        /// <summary>
        /// Checks if the slot rect overlaps our buffered viewport rect.
        /// </summary>
        private bool IsVisible(Rect parentRect, RectTransform slotRectTransform)
        {
            if (!slotRectTransform || !MainCanvas)
                return false;

            slotRectTransform.GetWorldCorners(bufferCorners);
            for (int i = 0; i < bufferCorners.Length; i++)
            {
                bufferCorners[i] = MainCanvas.transform.InverseTransformPoint(bufferCorners[i]);
            }

            float minX = Mathf.Min(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float maxX = Mathf.Max(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float minY = Mathf.Min(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
            float maxY = Mathf.Max(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);

            bufferRect.Set(minX, minY, maxX - minX, maxY - minY);
            return parentRect.Overlaps(bufferRect);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!DebugMode) return;
            if (!MainCanvas) MainCanvas = GetComponentInParent<Canvas>();
            if (!MainCanvas) return;
            if (!ScrollRect) return;

            // Draw the viewport in green
            Gizmos.color = Color.green;
            var viewport = ScrollRect.viewport != null ? ScrollRect.viewport : GetComponent<RectTransform>();
            viewport.GetWorldCorners(bufferCorners);

            for (int j = 0; j < bufferCorners.Length; j++)
            {
                bufferCorners[j] = MainCanvas.transform.InverseTransformPoint(bufferCorners[j]);
            }

            float minX = Mathf.Min(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float maxX = Mathf.Max(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
            float minY = Mathf.Min(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
            float maxY = Mathf.Max(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
            float width = maxX - minX;
            float height = maxY - minY;

            DrawRect(new Rect(minX, minY, width, height));

            // Draw each slot in blue
            Gizmos.color = Color.blue;
            foreach (var slot in slots)
            {
                if (!slot) continue;
                slot.GetWorldCorners(bufferCorners);
                for (int j = 0; j < bufferCorners.Length; j++)
                {
                    bufferCorners[j] = MainCanvas.transform.InverseTransformPoint(bufferCorners[j]);
                }

                float sMinX = Mathf.Min(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
                float sMaxX = Mathf.Max(bufferCorners[0].x, bufferCorners[1].x, bufferCorners[2].x, bufferCorners[3].x);
                float sMinY = Mathf.Min(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
                float sMaxY = Mathf.Max(bufferCorners[0].y, bufferCorners[1].y, bufferCorners[2].y, bufferCorners[3].y);
                DrawRect(new Rect(sMinX, sMinY, sMaxX - sMinX, sMaxY - sMinY));
            }
        }

        private void DrawRect(Rect rect)
        {
            Gizmos.DrawWireCube(
                new Vector3(rect.center.x, rect.center.y, 0.01f),
                new Vector3(rect.size.x, rect.size.y, 0.01f)
            );
        }
#endif

        #endregion
    }
}