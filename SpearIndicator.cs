using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FearNoSpear;

internal static class SpearIndicator
{
    private const float BeamHeight = 500f;
    private static readonly List<SpearLocator.Target> Targets = new(FearNoSpearConfig.MaxLocationResults);
    private static readonly List<Marker> Markers = new(FearNoSpearConfig.MaxLocationResults);
    private static readonly List<Vector2> HudPositions = new(FearNoSpearConfig.MaxLocationResults);
    private static readonly int[] HudOrder = new int[FearNoSpearConfig.MaxLocationResults];
    private static GameObject? _root;
    private static Canvas? _canvas;
    private static Material? _material;
    private static float _retryAt;

    internal static void Clear()
    {
        if (_root != null) Object.Destroy(_root);
        if (_canvas != null) Object.Destroy(_canvas.gameObject);
        if (_material != null) Object.Destroy(_material);
        _root = null;
        _canvas = null;
        _material = null;
        Targets.Clear();
        Markers.Clear();
        HudPositions.Clear();
        _retryAt = 0f;
    }

    private static void Hide()
    {
        if (_root != null) _root.SetActive(false);
        if (_canvas != null) _canvas.gameObject.SetActive(false);
    }

    internal static void Update()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            if (_root != null || _canvas != null || _material != null) Clear();
            return;
        }
        Camera camera = Utils.GetMainCamera();
        FearNoSpearPlugin.IndicatorStyle style = FearNoSpearPlugin.Cfg.SpearIndicatorStyle.Value;
        if (FearNoSpearPlugin.IsShuttingDown || style == FearNoSpearPlugin.IndicatorStyle.Off ||
            camera == null || camera.pixelHeight <= 0 || Hud.instance == null ||
            !Hud.instance.IsVisible() || Hud.IsUserHidden() ||
            player.IsDead() || player.IsTeleporting() || player.IsSleeping() ||
            Minimap.IsOpen() || InventoryGui.IsVisible() || Menu.IsVisible())
        {
            Hide();
            return;
        }

        SpearLocator.GetNearest(player, Targets, FearNoSpearPlugin.Cfg.MaxDisplayedSpears.Value);
        if (Targets.Count == 0)
        {
            Hide();
            return;
        }
        if ((_root == null || _canvas == null) && !Create()) return;

        bool beam = style == FearNoSpearPlugin.IndicatorStyle.BeamAndHud || style == FearNoSpearPlugin.IndicatorStyle.Beam;
        bool hud = style == FearNoSpearPlugin.IndicatorStyle.BeamAndHud || style == FearNoSpearPlugin.IndicatorStyle.Hud;
        _root!.SetActive(beam);
        _canvas!.gameObject.SetActive(hud);
        Rect viewport = camera.pixelRect;
        Rect safe = Screen.safeArea;
        Rect safeViewport = Rect.MinMaxRect(Mathf.Max(viewport.xMin, safe.xMin), Mathf.Max(viewport.yMin, safe.yMin),
            Mathf.Min(viewport.xMax, safe.xMax), Mathf.Min(viewport.yMax, safe.yMax));
        if (safeViewport.width <= 0f || safeViewport.height <= 0f) safeViewport = viewport;
        float scale = GetHudScale(safeViewport, Targets.Count);
        HudPositions.Clear();

        foreach (Marker marker in Markers)
        {
            bool selected = false;
            foreach (SpearLocator.Target target in Targets)
                if (target.Key == marker.Key) { selected = true; break; }
            if (selected) continue;
            marker.Beam.gameObject.SetActive(false);
            marker.Label.gameObject.SetActive(false);
        }
        foreach (SpearLocator.Target target in Targets)
        {
            Marker marker = GetMarker(target.Key);
            float distance = Vector3.Distance(player.transform.position, target.Position);
            marker.Beam.gameObject.SetActive(beam);
            marker.Label.gameObject.SetActive(hud);
            if (beam)
            {
                if (!marker.BeamPositionSet || (target.Position - marker.BeamPosition).sqrMagnitude > 0.000001f)
                {
                    marker.BeamPositionSet = true;
                    marker.BeamPosition = target.Position;
                    marker.Beam.SetPosition(0, target.Position);
                    marker.Beam.SetPosition(1, target.Position + Vector3.up * BeamHeight);
                }
                float width = GetBeamWidth(distance);
                if (!Mathf.Approximately(width, marker.BeamWidth))
                {
                    marker.BeamWidth = width;
                    marker.Beam.startWidth = marker.Beam.endWidth = width;
                }
            }
            if (!hud) continue;

            // Overlay placement deliberately has no far-clip or terrain-visibility test.
            Vector3 view = camera.transform.InverseTransformPoint(target.Position);
            Vector3 projected = view.z > 0f ? camera.WorldToViewportPoint(target.Position) : new Vector3(0.5f, 0.5f, view.z);
            bool edge = GetHudPlacement(projected, new Vector2(view.x, view.y), viewport, safeViewport, scale,
                out Vector2 position, out Vector2 direction);
            HudPositions.Add(position);
            marker.Label.localScale = Vector3.one * scale;
            marker.Pointer.gameObject.SetActive(edge);
            if (edge) marker.Pointer.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f);
            marker.Icon.sprite = target.Icon;
            marker.Icon.enabled = target.Icon != null;
            if (Time.unscaledTime >= marker.NextTextAt)
            {
                marker.Distance.text = distance < 1000f ? $"{distance:0} m" : $"{distance / 1000f:0.0} km";
                marker.NextTextAt = Time.unscaledTime + 0.1f;
            }
        }
        if (hud)
        {
            ArrangeHud(safeViewport, scale, HudPositions);
            Vector2 center = new Vector2(Screen.width, Screen.height) * 0.5f;
            for (int i = 0; i < Targets.Count; ++i)
                GetMarker(Targets[i].Key).Label.anchoredPosition = HudPositions[i] - center;
        }
    }

    private static Marker GetMarker(string key)
    {
        foreach (Marker marker in Markers)
            if (marker.Key == key) return marker;

        foreach (Marker marker in Markers)
        {
            bool retained = false;
            foreach (SpearLocator.Target target in Targets)
                if (target.Key == marker.Key) { retained = true; break; }
            if (retained) continue;
            marker.Key = key;
            marker.NextTextAt = 0f;
            return marker;
        }
        Marker created = new(_root!.transform, _canvas!.transform, _material!, Hud.instance.m_healthText.font) { Key = key };
        Markers.Add(created);
        return created;
    }

    private static float GetBeamWidth(float distance)
    {
        return Mathf.Clamp(distance * 0.0014f, 0.1f, 1f);
    }

    private static float GetHudScale(Rect safeViewport, int count)
    {
        // Reserve enough vertical space for all labels, including their edge pointers.
        return Mathf.Min(1f, Mathf.Min(safeViewport.width / 320f,
            safeViewport.height / (Mathf.Clamp(count, 1, FearNoSpearConfig.MaxLocationResults) * 90f + 136f)));
    }

    private static bool GetHudPlacement(Vector3 projected, Vector2 behindDirection, Rect viewport, Rect safeViewport,
        float scale, out Vector2 position, out Vector2 direction)
    {
        Vector2 halfSize = new(safeViewport.width * 0.5f - 100f * scale, safeViewport.height * 0.5f - 68f * scale);
        Vector2 center = safeViewport.center;
        Vector2 offset = new((projected.x - 0.5f) * viewport.width, (projected.y - 0.5f) * viewport.height);
        bool behind = projected.z <= 0f;
        bool edge = behind || projected.x < 0f || projected.x > 1f || projected.y < 0f || projected.y > 1f;
        if (!edge)
        {
            Vector2 point = viewport.center + offset + Vector2.up * (32f * scale);
            position = new Vector2(Mathf.Clamp(point.x, center.x - halfSize.x, center.x + halfSize.x),
                Mathf.Clamp(point.y, center.y - halfSize.y, center.y + halfSize.y));
            direction = Vector2.zero;
            return false;
        }

        // Camera-local XY avoids mirrored directions and division by zero behind the camera.
        if (behind) offset = behindDirection;
        direction = offset.sqrMagnitude < 0.0001f ? Vector2.down : offset.normalized;
        float x = Mathf.Abs(direction.x) > 0.0001f ? halfSize.x / Mathf.Abs(direction.x) : float.PositiveInfinity;
        float y = Mathf.Abs(direction.y) > 0.0001f ? halfSize.y / Mathf.Abs(direction.y) : float.PositiveInfinity;
        position = center + direction * Mathf.Min(x, y);
        return true;
    }

    private static void ArrangeHud(Rect safeViewport, float scale, List<Vector2> positions)
    {
        // Keep target/marker identity order intact; sort only a reusable index buffer.
        for (int i = 0; i < positions.Count; ++i)
        {
            int j = i;
            while (j > 0 && positions[HudOrder[j - 1]].y > positions[i].y)
            {
                HudOrder[j] = HudOrder[j - 1];
                --j;
            }
            HudOrder[j] = i;
        }
        float gap = 90f * scale;
        for (int i = 0; i < positions.Count; ++i)
        {
            Vector2 point = positions[HudOrder[i]];
            point.y = Mathf.Max(point.y, safeViewport.yMin + 68f * scale);
            for (int j = 0; j < i; ++j)
            {
                Vector2 previous = positions[HudOrder[j]];
                if (Mathf.Abs(point.x - previous.x) < 160f * scale)
                    point.y = Mathf.Max(point.y, previous.y + gap);
            }
            positions[HudOrder[i]] = point;
        }
        // Repack from the top edge when forward spacing would leave the viewport.
        for (int i = positions.Count - 1; i >= 0; --i)
        {
            Vector2 point = positions[HudOrder[i]];
            point.y = Mathf.Min(point.y, safeViewport.yMax - 68f * scale);
            for (int j = i + 1; j < positions.Count; ++j)
            {
                Vector2 next = positions[HudOrder[j]];
                if (Mathf.Abs(point.x - next.x) < 160f * scale)
                    point.y = Mathf.Min(point.y, next.y - gap);
            }
            positions[HudOrder[i]] = point;
        }
    }

    private static bool Create()
    {
        if (Time.unscaledTime < _retryAt) return false;
        _retryAt = Time.unscaledTime + 5f;
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        TMP_FontAsset font = Hud.instance.m_healthText != null ? Hud.instance.m_healthText.font : null!;
        if (shader == null || font == null) return false;

        _root = new GameObject("FearNoSpear Beams");
        _root.SetActive(false);
        _material = new Material(shader) { color = new Color(1f, 0.85f, 0.3f, 0.95f) };
        GameObject canvasObject = new("FearNoSpear Indicator UI", typeof(RectTransform), typeof(Canvas));
        canvasObject.SetActive(false);
        _canvas = canvasObject.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 50;
        return true;
    }

    private sealed class Marker
    {
        internal string Key = string.Empty;
        internal float NextTextAt;
        internal Vector3 BeamPosition;
        internal float BeamWidth;
        internal bool BeamPositionSet;
        internal readonly LineRenderer Beam;
        internal readonly RectTransform Label;
        internal readonly RectTransform Pointer;
        internal readonly Image Icon;
        internal readonly TextMeshProUGUI Distance;

        internal Marker(Transform root, Transform canvas, Material material, TMP_FontAsset font)
        {
            GameObject beam = new("Spear Beam", typeof(LineRenderer));
            beam.transform.SetParent(root, false);
            Beam = beam.GetComponent<LineRenderer>();
            Beam.sharedMaterial = material;
            Beam.shadowCastingMode = ShadowCastingMode.Off;
            Beam.receiveShadows = false;
            Beam.lightProbeUsage = LightProbeUsage.Off;
            Beam.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Beam.useWorldSpace = true;
            Beam.positionCount = 2;
            Beam.alignment = LineAlignment.View;
            Beam.textureMode = LineTextureMode.Stretch;
            Beam.numCapVertices = Beam.numCornerVertices = 0;
            Beam.startColor = Color.white;
            Beam.endColor = new Color(1f, 1f, 1f, 0.15f);

            GameObject label = new("Spear", typeof(RectTransform));
            label.transform.SetParent(canvas, false);
            Label = label.GetComponent<RectTransform>();
            Label.anchorMin = Label.anchorMax = Label.pivot = new Vector2(0.5f, 0.5f);
            Label.sizeDelta = new Vector2(152f, 44f);

            GameObject pointer = new("Edge direction", typeof(RectTransform), typeof(CanvasRenderer), typeof(SpearEdgeArrow));
            pointer.transform.SetParent(Label, false);
            pointer.GetComponent<SpearEdgeArrow>().raycastTarget = false;
            Pointer = pointer.GetComponent<RectTransform>();
            Pointer.sizeDelta = new Vector2(24f, 28f);
            Pointer.anchoredPosition = new Vector2(0f, 42f);

            GameObject image = new("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            image.transform.SetParent(Label, false);
            Icon = image.GetComponent<Image>();
            Icon.raycastTarget = false;
            Icon.preserveAspect = true;
            Icon.rectTransform.sizeDelta = new Vector2(40f, 40f);
            Icon.rectTransform.anchoredPosition = new Vector2(-54f, 0f);

            GameObject text = new("Distance", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            text.transform.SetParent(Label, false);
            Distance = text.GetComponent<TextMeshProUGUI>();
            Distance.font = font;
            Distance.fontSize = 23f;
            Distance.alignment = TextAlignmentOptions.MidlineLeft;
            Distance.color = Color.white;
            Distance.outlineWidth = 0.25f;
            Distance.outlineColor = Color.black;
            Distance.raycastTarget = false;
            Distance.textWrappingMode = TextWrappingModes.NoWrap;
            Distance.rectTransform.sizeDelta = new Vector2(108f, 44f);
            Distance.rectTransform.anchoredPosition = new Vector2(24f, 0f);
        }
    }
}

internal sealed class SpearEdgeArrow : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vertices)
    {
        vertices.Clear();
        vertices.AddVert(new Vector3(0f, 14f), Color.black, Vector2.zero);
        vertices.AddVert(new Vector3(-11f, -11f), Color.black, Vector2.zero);
        vertices.AddVert(new Vector3(11f, -11f), Color.black, Vector2.zero);
        vertices.AddTriangle(0, 1, 2);
        Color gold = new(1f, 0.85f, 0.3f, 1f);
        vertices.AddVert(new Vector3(0f, 10f), gold, Vector2.zero);
        vertices.AddVert(new Vector3(-7f, -8f), gold, Vector2.zero);
        vertices.AddVert(new Vector3(7f, -8f), gold, Vector2.zero);
        vertices.AddTriangle(3, 4, 5);
    }
}
