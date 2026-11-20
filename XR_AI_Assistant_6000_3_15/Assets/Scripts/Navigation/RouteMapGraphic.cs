using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Decorative uGUI route overlay. All coordinates are in its local UI rectangle.</summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class RouteMapGraphic : MaskableGraphic
{
    private readonly List<Vector2> points = new List<Vector2>();
    private Vector2 position;
    private Vector2 destination;
    private bool showPosition;
    private bool showDestination;
    private bool positionFresh;
    private bool headingKnown;
    private float markerAngle;

    public void SetGeometry(IList<Vector2> route, Vector2 currentPosition, bool hasPosition,
        bool fresh, float positionAngle, Vector2 target, bool hasTarget, bool freshHeading = true)
    {
        raycastTarget = false;
        points.Clear();
        if (route != null)
            for (int i = 0; i < route.Count && i < 4096; i++) points.Add(route[i]);
        position = currentPosition;
        destination = target;
        showPosition = hasPosition;
        positionFresh = fresh;
        headingKnown = freshHeading;
        markerAngle = positionAngle;
        showDestination = hasTarget;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect clip = rectTransform.rect;
        // Two passes keep the route's light centre continuous over its dark outline.
        for (int pass = 0; pass < 2; pass++)
        {
            float width = pass == 0 ? 9f : 5f;
            Color32 stroke = pass == 0 ? new Color32(14, 39, 63, 240) : new Color32(0, 157, 255, 255);
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                if (ClipSegment(ref a, ref b, clip)) AddLine(vh, a, b, width, stroke);
            }
        }

        if (showDestination && clip.Contains(destination))
        {
            AddCircle(vh, destination, 11f, new Color32(15, 32, 45, 255));
            AddCircle(vh, destination, 8f, new Color32(255, 209, 72, 255));
            AddCircle(vh, destination, 3f, new Color32(15, 32, 45, 255));
        }
        if (showPosition && clip.Contains(position))
        {
            AddCircle(vh, position, 12f, new Color32(255, 255, 255, 255));
            AddCircle(vh, position, 9f, positionFresh ? new Color32(0, 138, 247, 255) : new Color32(123, 131, 142, 255));
            if (positionFresh && headingKnown)
            {
                float r = markerAngle * Mathf.Deg2Rad;
                Vector2 up = new Vector2(-Mathf.Sin(r), Mathf.Cos(r));
                Vector2 right = new Vector2(up.y, -up.x);
                AddTriangle(vh, position + up * 17f, position - up * 2f + right * 6f,
                    position - up * 2f - right * 6f, new Color32(255, 255, 255, 255));
            }
        }
    }

    private static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float width, Color32 color)
    {
        Vector2 direction = b - a;
        if (direction.sqrMagnitude < 0.001f) return;
        Vector2 normal = new Vector2(-direction.y, direction.x).normalized * width * 0.5f;
        int first = vh.currentVertCount;
        AddVertex(vh, a - normal, color); AddVertex(vh, a + normal, color);
        AddVertex(vh, b + normal, color); AddVertex(vh, b - normal, color);
        vh.AddTriangle(first, first + 1, first + 2); vh.AddTriangle(first, first + 2, first + 3);
    }

    private static void AddCircle(VertexHelper vh, Vector2 centre, float radius, Color32 color)
    {
        int first = vh.currentVertCount;
        AddVertex(vh, centre, color);
        const int sides = 16;
        for (int i = 0; i < sides; i++)
        {
            float angle = i * (Mathf.PI * 2f / sides);
            AddVertex(vh, centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color);
        }
        for (int i = 0; i < sides; i++) vh.AddTriangle(first, first + 1 + i, first + 1 + ((i + 1) % sides));
    }

    private static void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color32 color)
    {
        int first = vh.currentVertCount;
        AddVertex(vh, a, color); AddVertex(vh, b, color); AddVertex(vh, c, color);
        vh.AddTriangle(first, first + 1, first + 2);
    }

    private static void AddVertex(VertexHelper vh, Vector2 p, Color32 color)
    {
        UIVertex vertex = UIVertex.simpleVert;
        vertex.position = p; vertex.color = color; vertex.uv0 = Vector2.zero;
        vh.AddVert(vertex);
    }

    // Clip before tessellation: remote geometry cannot create enormous off-screen quads.
    public static bool ClipSegment(ref Vector2 a, ref Vector2 b, Rect rect)
    {
        Vector2 delta = b - a;
        float low = 0f, high = 1f;
        if (!Clip(-delta.x, a.x - rect.xMin, ref low, ref high) ||
            !Clip(delta.x, rect.xMax - a.x, ref low, ref high) ||
            !Clip(-delta.y, a.y - rect.yMin, ref low, ref high) ||
            !Clip(delta.y, rect.yMax - a.y, ref low, ref high)) return false;
        b = a + high * delta; a += low * delta;
        return true;
    }

    private static bool Clip(float p, float q, ref float low, ref float high)
    {
        if (Mathf.Abs(p) < 0.000001f) return q >= 0f;
        float t = q / p;
        if (p < 0f) { if (t > high) return false; if (t > low) low = t; }
        else { if (t < low) return false; if (t < high) high = t; }
        return true;
    }
}
