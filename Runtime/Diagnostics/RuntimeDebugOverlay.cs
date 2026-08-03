using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#endif

namespace ArkFramework
{
    [DisallowMultipleComponent]
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [ExecuteAlways]
#endif
    public sealed class RuntimeDebugOverlay : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const string RootName = "ArkFramework.RuntimeDebugOverlay";
        private const int PageCount = 10;

        [SerializeField, Min(0.1f)]
        private float _refreshIntervalSeconds = 0.5f;

        private readonly List<ButtonBinding> _buttonBindings =
            new List<ButtonBinding>();
        private GameObject _ownedRoot;
        private EventSystem _ownedEventSystem;
        private Text _header;
        private Text _status;
        private Text _content;
        private DiagnosticsSnapshot _snapshot;
        private DiagnosticsPageKind _selectedPage;
        private float _nextRefreshTime;

        private void OnEnable()
        {
            TrySetupAndRefresh();
        }

        private void Update()
        {
            if (_ownedRoot == null)
            {
                TrySetupAndRefresh();
                return;
            }

            EnsureEventSystem();
            if (Time.unscaledTime >= _nextRefreshTime)
            {
                RefreshNow();
            }
        }

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Setup()
        {
            if (_ownedRoot != null)
            {
                return;
            }

            _selectedPage = PageKindAt(0);
            _ownedRoot = new GameObject(
                RootName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            _ownedRoot.hideFlags = HideFlags.DontSave;
            _ownedRoot.transform.SetParent(transform, false);

            var canvas = _ownedRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var scaler = _ownedRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);

            var panel = CreateRect(
                "Panel",
                _ownedRoot.transform,
                new Vector2(0.05f, 0.05f),
                new Vector2(0.95f, 0.95f),
                Vector2.zero,
                Vector2.zero);
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(0.04f, 0.05f, 0.08f, 0.94f);

            _header = CreateText(
                "Header",
                panel,
                new Vector2(0.02f, 0.93f),
                new Vector2(0.98f, 0.99f),
                22,
                TextAnchor.MiddleLeft);
            _header.text = "ArkFramework Diagnostics";
            _status = CreateText(
                "Status",
                panel,
                new Vector2(0.02f, 0.88f),
                new Vector2(0.98f, 0.93f),
                14,
                TextAnchor.MiddleLeft);

            CreatePageButtons(panel);
            CreateScrollableContent(panel);
        }

        private void CreatePageButtons(RectTransform panel)
        {
            const float top = 0.87f;
            const float bottom = 0.05f;
            var height = (top - bottom) / PageCount;
            for (var index = 0; index < PageCount; index++)
            {
                var pageKind = PageKindAt(index);
                var buttonRect = CreateRect(
                    $"PageButton.{pageKind}",
                    panel,
                    new Vector2(
                        0.02f,
                        top - ((index + 1) * height) + 0.005f),
                    new Vector2(
                        0.2f,
                        top - (index * height) - 0.005f),
                    Vector2.zero,
                    Vector2.zero);
                var image = buttonRect.gameObject.AddComponent<Image>();
                image.color = new Color(0.12f, 0.15f, 0.22f, 1f);
                var button = buttonRect.gameObject.AddComponent<Button>();
                var label = CreateText(
                    "Label",
                    buttonRect,
                    Vector2.zero,
                    Vector2.one,
                    14,
                    TextAnchor.MiddleCenter);
                label.text = pageKind.ToString();
                UnityAction listener = () => SelectPage(pageKind);
                button.onClick.AddListener(listener);
                _buttonBindings.Add(new ButtonBinding(button, listener));
            }
        }

        private void EnsureEventSystem()
        {
            if (_ownedRoot == null ||
                _ownedEventSystem != null ||
                EventSystem.current != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventSystem.hideFlags = HideFlags.DontSave;
            eventSystem.transform.SetParent(_ownedRoot.transform, false);
            _ownedEventSystem = eventSystem.GetComponent<EventSystem>();
        }

        private void CreateScrollableContent(RectTransform panel)
        {
            var scrollRectTransform = CreateRect(
                "ContentScroll",
                panel,
                new Vector2(0.22f, 0.05f),
                new Vector2(0.98f, 0.87f),
                Vector2.zero,
                Vector2.zero);
            var scrollRect =
                scrollRectTransform.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            var viewport = CreateRect(
                "Viewport",
                scrollRectTransform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            var viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0.02f, 0.025f, 0.04f, 0.85f);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            var contentRect = CreateRect(
                "Content",
                viewport,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(12f, -12f),
                new Vector2(-12f, -12f));
            contentRect.pivot = new Vector2(0.5f, 1f);
            _content = contentRect.gameObject.AddComponent<Text>();
            ConfigureText(_content, 14, TextAnchor.UpperLeft);
            _content.horizontalOverflow = HorizontalWrapMode.Wrap;
            _content.verticalOverflow = VerticalWrapMode.Overflow;
            var contentFitter =
                contentRect.gameObject.AddComponent<ContentSizeFitter>();
            contentFitter.horizontalFit =
                ContentSizeFitter.FitMode.Unconstrained;
            contentFitter.verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = contentRect;
        }

        private void RefreshNow()
        {
            _nextRefreshTime =
                Time.unscaledTime + Mathf.Max(0.1f, _refreshIntervalSeconds);
            try
            {
                _snapshot = DiagnosticsSnapshot.Capture(
                    FrameworkHost.Current?.Runtime);
                _status.text = FrameworkHost.Current?.Runtime == null
                    ? "Runtime unavailable"
                    : "Runtime diagnostics snapshot";
                RenderSelectedPage();
            }
            catch (Exception exception)
            {
                if (_status != null)
                {
                    _status.text = $"Overlay refresh failed: {exception}";
                }
            }
        }

        private void TrySetupAndRefresh()
        {
            try
            {
                Setup();
                RefreshNow();
            }
            catch (Exception exception)
            {
                Cleanup();
                Debug.LogException(exception, this);
            }
        }

        private void SelectPage(DiagnosticsPageKind pageKind)
        {
            _selectedPage = pageKind;
            RenderSelectedPage();
        }

        private void RenderSelectedPage()
        {
            if (_snapshot == null || _content == null || _header == null)
            {
                return;
            }

            DiagnosticsPageSnapshot selected = null;
            for (var index = 0; index < _snapshot.Pages.Count; index++)
            {
                if (_snapshot.Pages[index].Kind == _selectedPage)
                {
                    selected = _snapshot.Pages[index];
                    break;
                }
            }

            _header.text = $"ArkFramework Diagnostics / {_selectedPage}";
            _content.text = FormatPage(selected);
        }

        private static string FormatPage(DiagnosticsPageSnapshot page)
        {
            if (page == null)
            {
                return "Page unavailable.";
            }

            var builder = new StringBuilder();
            builder.Append("Status: ");
            builder.AppendLine(
                page.IsAvailable ? "Available" : "Unavailable");
            if (!string.IsNullOrEmpty(page.Error))
            {
                builder.Append("Error: ");
                builder.AppendLine(page.Error);
            }

            if (page.Entries.Count == 0)
            {
                builder.AppendLine("No diagnostics entries.");
                return builder.ToString();
            }

            for (var entryIndex = 0;
                 entryIndex < page.Entries.Count;
                 entryIndex++)
            {
                var entry = page.Entries[entryIndex];
                builder.Append('[');
                builder.Append(entry.Id);
                builder.AppendLine("]");
                for (var fieldIndex = 0;
                     fieldIndex < entry.Fields.Count;
                     fieldIndex++)
                {
                    var field = entry.Fields[fieldIndex];
                    builder.Append("  ");
                    builder.Append(field.Name);
                    builder.Append(": ");
                    builder.AppendLine(field.Value);
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static DiagnosticsPageKind PageKindAt(int index)
        {
            switch (index)
            {
                case 0: return DiagnosticsPageKind.Modules;
                case 1: return DiagnosticsPageKind.Events;
                case 2: return DiagnosticsPageKind.Resources;
                case 3: return DiagnosticsPageKind.Pools;
                case 4: return DiagnosticsPageKind.UI;
                case 5: return DiagnosticsPageKind.Audio;
                case 6: return DiagnosticsPageKind.Scene;
                case 7: return DiagnosticsPageKind.Config;
                case 8: return DiagnosticsPageKind.FSM;
                case 9: return DiagnosticsPageKind.Procedure;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private void Cleanup()
        {
            for (var index = 0; index < _buttonBindings.Count; index++)
            {
                _buttonBindings[index].Remove();
            }

            _buttonBindings.Clear();
            _snapshot = null;
            _ownedEventSystem = null;
            _header = null;
            _status = null;
            _content = null;
            var root = _ownedRoot;
            _ownedRoot = null;
            if (root == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(root);
            }
            else
            {
                DestroyImmediate(root);
            }
        }

        private static RectTransform CreateRect(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.hideFlags = HideFlags.DontSave;
            var rect = gameObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return rect;
        }

        private static Text CreateText(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            int fontSize,
            TextAnchor alignment)
        {
            var rect = CreateRect(
                name,
                parent,
                anchorMin,
                anchorMax,
                Vector2.zero,
                Vector2.zero);
            var text = rect.gameObject.AddComponent<Text>();
            ConfigureText(text, fontSize, alignment);
            return text;
        }

        private static void ConfigureText(
            Text text,
            int fontSize,
            TextAnchor alignment)
        {
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
        }

        private sealed class ButtonBinding
        {
            private readonly Button _button;
            private readonly UnityAction _listener;

            public ButtonBinding(Button button, UnityAction listener)
            {
                _button = button;
                _listener = listener;
            }

            public void Remove()
            {
                if (_button != null)
                {
                    _button.onClick.RemoveListener(_listener);
                }
            }
        }
#endif
    }
}
