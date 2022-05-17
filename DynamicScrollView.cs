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
        }

        public event Action<TGameObject, TData> OnCreateItem;
        public event Action<TGameObject, TData> OnRemoveItem;
        
        public event Action<List<TGameObject>> OnCompleteListingGeneration;

        public List<TGameObject> Listing
        {
            get
            {
                List<TGameObject> listView = new List<TGameObject>();
                listView.AddRange(itemsAtSlot.Values);
                return listView;
            }
        }

        [SerializeField] Canvas canvas;
        [SerializeField] ScrollRect scrollRect;
        [SerializeField] TGameObject prefab;
        [SerializeField] bool canOverrideItemRectTransform;
        [SerializeField] bool debugMode;

        [Header("Slots"), SerializeField]  int SlotNumInstantiateCallsPerFrame = 10;
        [SerializeField] Vector2 ItemBufferCount;

        RectTransform slotPrefab;
        readonly List<RectTransform> slots = new List<RectTransform>();
        Coroutine createSlotsRoutine;

        bool isInit;
        bool isGenerating;
        RectTransform rectTransform;
        Coroutine generateRoutine;
        Coroutine scrollToRoutine;
        int pendingScrollToIndex = -1;
        Rect viewportRect;
        readonly Vector3[] bufferCorners = new Vector3[4];
        Rect bufferRect;
        RectTransform prefabRectTransform;

        readonly Dictionary<int, TGameObject> itemsAtSlot = new Dictionary<int, TGameObject>();
        
        static string SLOT_PREFAB = "SlotPrefab";

        public List<TData> DataSource { get; } = new List<TData>();

        public void Generate(TData[] data)
        {
            isGenerating = true;
            Initialize();
            DestroyAllItems();

            DataSource.Clear();
            DataSource.AddRange(data);
            
            if (gameObject.activeInHierarchy)
            {
                if (generateRoutine != null)
                {
                    StopCoroutine(generateRoutine);
                }
                generateRoutine = StartCoroutine(GenerateView(data));
            }
        }
        
        public void Refresh(TData itemData)
        {
            int index = DataSource.FindIndex(item => item == itemData);
            if (index >= 0)
            {
                if (itemsAtSlot.TryGetValue(index, out TGameObject itemAtSlot))
                {
                    itemAtSlot.Init(itemData);
                }
            }
        }

        public void RefreshAll()
        {
            foreach (KeyValuePair<int, TGameObject> itemAtSlot in itemsAtSlot)
            {
                itemAtSlot.Value.Init(DataSource[itemAtSlot.Key]);
            }
        }

        public void ScrollTo(TData itemToFocus)
        {
            int slotIndex = DataSource.FindIndex(item => item == itemToFocus);

            if (slotIndex >= 0)
            {
                if (isGenerating)
                {
                    if (gameObject.activeInHierarchy)
                    {
                        if (scrollToRoutine != null)
                        {
                            StopCoroutine(scrollToRoutine);
                        }
                        scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(slotIndex));
                    }
                    else
                    {
                        pendingScrollToIndex = slotIndex;
                    }
                }
                else
                {
                    ScrollTo(slotIndex);
                }
            }
        }
        
        public void ScrollToBeginning()
        {
            if (isGenerating)
            {
                if (gameObject.activeInHierarchy)
                {
                    if (scrollToRoutine != null)
                    {
                        StopCoroutine(scrollToRoutine);
                    }
                    scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(0));
                }
                else
                {
                    pendingScrollToIndex = 0;
                }
            }
            else
            {
                ScrollTo(0);
            }
        }
        
        public void ScrollToEnd()
        {
            if (isGenerating)
            {
                if (gameObject.activeInHierarchy)
                {
                    if (scrollToRoutine != null)
                    {
                        StopCoroutine(scrollToRoutine);
                    }
                    scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(Int32.MaxValue));
                }
                else
                {
                    pendingScrollToIndex = Int32.MaxValue;
                }
            }
            else
            {
                ScrollTo(slots.Count-1);
            }
        }

        private void ScrollTo(int index)
        {
            if (slots.Count > 0)
            {
                index = Mathf.Clamp(index, 0, slots.Count - 1);
                RectTransform target = slots[index];
                Vector2 viewportHalfSize = scrollRect.viewport.rect.size * 0.5f;
                Vector2 contentSize = scrollRect.content.rect.size;

                //get object position inside content
                Vector3 targetRelativePosition =
                    scrollRect.content.InverseTransformPoint(target.position);

                //adjust for item size
                targetRelativePosition += new Vector3(target.rect.size.x, target.rect.size.y, 0f) * 0.25f;

                //get the normalized position inside content
                Vector2 normalizedPosition = new Vector2(
                    Mathf.Clamp01(targetRelativePosition.x / (contentSize.x - viewportHalfSize.x)),
                    1f - Mathf.Clamp01(targetRelativePosition.y / -(contentSize.y - viewportHalfSize.y))
                );

                //we want the position to be at the middle of the visible area
                //so get the normalized center offset based on the visible area width and height
                Vector2 normalizedOffsetPosition =
                    new Vector2(viewportHalfSize.x / contentSize.x, viewportHalfSize.y / contentSize.y);

                //and apply it
                normalizedPosition.x -= (1f - normalizedPosition.x) * normalizedOffsetPosition.x;
                normalizedPosition.y += normalizedPosition.y * normalizedOffsetPosition.y;

                normalizedPosition.x = Mathf.Clamp01(normalizedPosition.x);
                normalizedPosition.y = Mathf.Clamp01(normalizedPosition.y);

                scrollRect.normalizedPosition = normalizedPosition;

                UpdateVisibility();
            }
        }
        
        public void Dispose()
        {
            Pool.ClearPool(prefab);
        }

        public void DestroyAllItems()
        {
            foreach (KeyValuePair<int, TGameObject> itemInSlot in itemsAtSlot)
            {
                OnItemRemoved(itemInSlot.Value, DataSource[itemInSlot.Key]);
                OnRemoveItem?.Invoke(itemInSlot.Value, DataSource[itemInSlot.Key]);
                Pool.Destroy(itemInSlot.Value);
            }

            itemsAtSlot.Clear();
        }

        protected void Awake()
        {
            Initialize();
        }

        protected virtual void Start()
        {
            Initialize();
        }

        protected virtual void OnEnable()
        {
            if (slots.Count != DataSource.Count)
            {
                if (generateRoutine != null)
                {
                    StopCoroutine(generateRoutine);
                }
                generateRoutine = StartCoroutine(GenerateView(DataSource.ToArray()));
            }

            if (pendingScrollToIndex >= 0)
            {
                scrollToRoutine = StartCoroutine(WaitGenerateAndScrollTo(pendingScrollToIndex));
            }
        }

        protected virtual void OnDisable()
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

            foreach (RectTransform slot in slots)
            {
                if (slot != null)
                {
                    Pool.Destroy(slot);
                }
            }

            DestroyAllItems();

            scrollRect.onValueChanged.RemoveListener(OnScrollRectChange);

            if (slotPrefab != null)
            {
                Destroy(slotPrefab.gameObject);
                slotPrefab = null;
            }
        }

        protected virtual void Reset()
        {
            scrollRect = GetComponent<ScrollRect>();
        }

        protected virtual void OnItemCreated(TGameObject item, TData data)
        {
        }

        protected virtual void OnItemRemoved(TGameObject item, TData data)
        {
        }

        void Initialize()
        {
            if (!isInit)
            {
                rectTransform = GetComponent<RectTransform>();
                if (canvas == null)
                {
                    canvas = rectTransform.GetComponentInParent<Canvas>();
                }
                
                UpdateViewportRect();

                GameObject newSlotPrefabGo =
                    new GameObject(SLOT_PREFAB, typeof(RectTransform), typeof(GameObjectPoolReference));
                slotPrefab = newSlotPrefabGo.GetComponent<RectTransform>();

                prefabRectTransform = prefab.GetComponent<RectTransform>();

                scrollRect.onValueChanged.AddListener(OnScrollRectChange);

                isInit = true;
            }
        }

        IEnumerator GenerateView(TData[] data)
        {
            if (DataSource.Count > slots.Count)
            {
                int slotsToCreate = Mathf.Clamp(DataSource.Count - slots.Count, 0, SlotNumInstantiateCallsPerFrame);

                while (slotsToCreate != 0)
                {
                    for (int i = 0; i < slotsToCreate; i++)
                    {
                        RectTransform newSlot = Pool.Instantiate(slotPrefab);
                        newSlot.SetParent(scrollRect.content);

                        newSlot.localPosition = Vector3.zero;
                        newSlot.localScale = Vector3.one;
                        newSlot.localRotation = Quaternion.identity;
                        newSlot.sizeDelta = prefabRectTransform.sizeDelta; //Note: Layout of the content MAY overwrite the slot size (eg. GridLayoutGroup)

                        slots.Add(newSlot);
                    }

                    LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
                    yield return new WaitForEndOfFrame(); //wait for layout rebuild
                    yield return
                        new WaitForEndOfFrame(); //wait again to make sure the layout is right on newly created objects

                    UpdateVisibility();

                    slotsToCreate = Mathf.Clamp(DataSource.Count - slots.Count, 0, SlotNumInstantiateCallsPerFrame);
                }
            }
            else if (slots.Count > DataSource.Count)
            {
                int slotsToRemove = slots.Count - DataSource.Count;

                for (int i = 0; i < slotsToRemove; i++)
                {
                    RectTransform slotToDestroy = slots[slots.Count - 1];
                    slots.RemoveAt(slots.Count - 1);
                    Pool.Destroy(slotToDestroy);
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
                yield return new WaitForEndOfFrame();

                UpdateVisibility();
            }
            else //slots.Count == DataSource.Count
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
                yield return new WaitForEndOfFrame();

                UpdateVisibility();
            }

            generateRoutine = null;
            isGenerating = false;
            OnCompleteListingGeneration?.Invoke(Listing);
        }

        IEnumerator WaitGenerateAndScrollTo(int index)
        {
            yield return new WaitUntil(() => isGenerating == false);
            ScrollTo(index);
            pendingScrollToIndex = -1;
            scrollToRoutine = null;
        }
        
        void OnScrollRectChange(Vector2 scrollDelta)
        {
            UpdateVisibility();
        }

        void UpdateVisibility()
        {
            UpdateViewportRect();
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

        void CreateItemAt(int index)
        {
            if (slots.Count > index && DataSource.Count > index)
            {
                if (!itemsAtSlot.ContainsKey(index))
                {
                    TGameObject newItem = Pool.Instantiate(prefab);
                    RectTransform newItemRT = newItem.GetComponent<RectTransform>();
                    RectTransform prefabRT = prefab.GetComponent<RectTransform>();

                    newItemRT.SetParent(slots[index]);
                    newItemRT.localPosition = prefabRT.localPosition;
                    newItemRT.localScale = prefabRT.localScale;
                    newItemRT.localRotation = prefabRT.localRotation;

                    if (canOverrideItemRectTransform)
                    {
                        newItemRT.pivot = new Vector2(0.5f, 0.5f);
                        newItemRT.anchoredPosition = Vector3.zero;
                        newItemRT.anchorMin = Vector2.zero;
                        newItemRT.anchorMax = Vector2.one;
                        newItemRT.offsetMin = Vector2.zero;
                        newItemRT.offsetMax = Vector2.zero;
                    }

                    itemsAtSlot.Add(index, newItem);
                    newItem.Init(DataSource[index]);

                    OnItemCreated(newItem, DataSource[index]);
                    OnCreateItem?.Invoke(newItem, DataSource[index]);
                }
            }
        }

        void DestroyItemAt(int index)
        {
            if (itemsAtSlot.TryGetValue(index, out TGameObject viewItem))
            {
                itemsAtSlot.Remove(index);
                OnItemRemoved(viewItem, DataSource[index]);
                OnRemoveItem?.Invoke(viewItem, DataSource[index]);

                Pool.Destroy(viewItem);
            }
        }

        void UpdateViewportRect()
        {
            (scrollRect.viewport != null ? scrollRect.viewport : rectTransform).GetWorldCorners(bufferCorners);
            for (int j = 0; j < bufferCorners.Length; j++)
            {
                bufferCorners[j] = canvas.transform.InverseTransformVector(bufferCorners[j]);
            }
            
            Vector2 estimatedSlotSize = new Vector2(0, 0);
            if (slots.Count > 0)
            {
                Vector3[] slotCorners = new Vector3[4];
                slots[0].GetWorldCorners(slotCorners);
                estimatedSlotSize.Set(Vector3.Distance(slotCorners[2], slotCorners[1]), Vector3.Distance(slotCorners[1],slotCorners[0]));
            }

            viewportRect.Set(bufferCorners[1].x - (ItemBufferCount.x * estimatedSlotSize.x), 
                bufferCorners[1].y + (ItemBufferCount.y * estimatedSlotSize.y), 
                Vector3.Distance(bufferCorners[2], bufferCorners[1]) + (ItemBufferCount.x * estimatedSlotSize.x * 2),
                Vector3.Distance(bufferCorners[1],bufferCorners[0]) + (ItemBufferCount.y * estimatedSlotSize.y * 2));
        }

        bool IsVisible(Rect parentRect, RectTransform rectTransform)
        {
            if (rectTransform != null)
            {
                rectTransform.GetWorldCorners(bufferCorners);
                for (int i = 0; i < bufferCorners.Length; i++)
                {
                    bufferCorners[i] = canvas.transform.InverseTransformVector(bufferCorners[i]);
                }

                float width = Mathf.Abs(Vector3.Distance(bufferCorners[2] ,bufferCorners[1]));
                float height = Mathf.Abs(Vector3.Distance(bufferCorners[1], bufferCorners[0]));

                bufferRect.Set(bufferCorners[1].x, bufferCorners[1].y + - height + parentRect.height , width, height);
                return parentRect.Overlaps(bufferRect);
            }

            return false;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!debugMode) return;
            
            Gizmos.color = new Color(0.0f, 1.0f, 0.0f);
            (scrollRect.viewport != null ? scrollRect.viewport : rectTransform).GetWorldCorners(bufferCorners);
            
            for (int j = 0; j < bufferCorners.Length; j++)
            {
                bufferCorners[j] = canvas.transform.InverseTransformVector(bufferCorners[j]);
            }

            float viewportHeight = Vector3.Distance(bufferCorners[1], bufferCorners[0]);
            
            DrawRect(new Rect(bufferCorners[1].x, 
                bufferCorners[1].y, 
                Vector3.Distance(bufferCorners[2], bufferCorners[1]),
                viewportHeight));

             Gizmos.color = new Color(0.0f, 0.0f, 1.0f);
             for (int i = 0; i < slots.Count; i++)
             {
                 slots[i].GetWorldCorners(bufferCorners);
                 for (int j = 0; j < bufferCorners.Length; j++)
                 {
                     bufferCorners[j] = canvas.transform.InverseTransformVector(bufferCorners[j]);
                 }
                 
                 float width = Mathf.Abs(Vector3.Distance(bufferCorners[2] ,bufferCorners[1]));
                 float height = Mathf.Abs(Vector3.Distance(bufferCorners[1], bufferCorners[0]));
            
                 bufferRect.Set(bufferCorners[1].x, bufferCorners[1].y - height + viewportHeight, width, height);
                 DrawRect(bufferRect);
             }
        }
        
        void DrawRect(Rect rect)
        {
            Gizmos.DrawWireCube(new Vector3(rect.center.x, rect.center.y, 0.01f), new Vector3(rect.size.x, rect.size.y, 0.01f));
            // Gizmos.DrawLine(new Vector3(rect.x, rect.y, 0), new Vector3(rect.xMax, rect.y));
            // Gizmos.DrawLine(new Vector3(rect.x, rect.y, 0), new Vector3(rect.x, rect.yMax));
            // Gizmos.DrawLine(new Vector3(rect.xMax, rect.y, 0), new Vector3(rect.xMax, rect.yMax));
            // Gizmos.DrawLine(new Vector3(rect.x, rect.yMax, 0), new Vector3(rect.xMax, rect.yMax));
        }
#endif
    }
}