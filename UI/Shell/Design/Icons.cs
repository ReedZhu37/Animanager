using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// Vector icon set. Drawn procedurally so it stays crisp at any DPI and the shell
/// ships no image assets. Each name is a 20x20 design box scaled into the target rect.
/// </summary>
public static class Icons
{
    public const string Grid = "grid";
    public const string List = "list";
    public const string Search = "search";
    public const string Plus = "plus";
    public const string Import = "import";
    public const string Export = "export";
    public const string ChevronDown = "chevron-down";
    public const string ChevronUp = "chevron-up";
    public const string ChevronRight = "chevron-right";
    public const string ChevronLeft = "chevron-left";
    public const string Star = "star";
    public const string StarOutline = "star-outline";
    public const string Trash = "trash";
    public const string Filter = "filter";
    public const string More = "more";
    public const string MoreVertical = "more-vertical";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string SkipStart = "skip-start";
    public const string SkipEnd = "skip-end";
    public const string Repeat = "repeat";
    public const string Folder = "folder";
    public const string Tag = "tag";
    public const string Layers = "layers";
    public const string Film = "film";
    public const string Panel = "panel";
    public const string Expand = "expand";
    public const string Close = "close";
    public const string Check = "check";
    public const string Gear = "gear";
    public const string Cube = "cube";
    public const string File = "file";

    public static void Draw(CanvasItem target, string name, Rect2 box, Color color, float weight = 1.6f)
    {
        float side = Mathf.Min(box.Size.X, box.Size.Y);
        if (side <= 0) return;
        var origin = new Vector2(box.Position.X + (box.Size.X - side) * .5f, box.Position.Y + (box.Size.Y - side) * .5f);
        Vector2 P(float x, float y) => new(origin.X + x * side, origin.Y + y * side);
        float w = Mathf.Max(1f, weight * (side / 20f));

        switch (name)
        {
            case Grid:
                foreach (var (x, y) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
                    target.DrawRect(new Rect2(P(.10f + x * .40f, .10f + y * .40f), new Vector2(side * .22f, side * .22f)),
                        color, false, w);
                break;

            case List:
                for (int i = 0; i < 3; i++)
                {
                    float y = .22f + i * .28f;
                    target.DrawCircle(P(.16f, y), w * .9f, color);
                    target.DrawLine(P(.32f, y), P(.88f, y), color, w);
                }
                break;

            case Search:
                target.DrawArc(P(.44f, .44f), side * .27f, 0, 360, 32, color, w, true);
                target.DrawLine(P(.64f, .64f), P(.86f, .86f), color, w, true);
                break;

            case Plus:
                target.DrawLine(P(.16f, .5f), P(.84f, .5f), color, w, true);
                target.DrawLine(P(.5f, .16f), P(.5f, .84f), color, w, true);
                break;

            case Import:
                target.DrawLine(P(.5f, .10f), P(.5f, .58f), color, w, true);
                target.DrawPolyline([P(.32f, .42f), P(.5f, .60f), P(.68f, .42f)], color, w, true);
                target.DrawPolyline([P(.16f, .72f), P(.16f, .86f), P(.84f, .86f), P(.84f, .72f)], color, w, true);
                break;

            case Export:
                target.DrawLine(P(.5f, .62f), P(.5f, .14f), color, w, true);
                target.DrawPolyline([P(.32f, .32f), P(.5f, .14f), P(.68f, .32f)], color, w, true);
                target.DrawPolyline([P(.16f, .72f), P(.16f, .86f), P(.84f, .86f), P(.84f, .72f)], color, w, true);
                break;

            case ChevronDown: target.DrawPolyline([P(.28f, .42f), P(.5f, .62f), P(.72f, .42f)], color, w, true); break;
            case ChevronUp: target.DrawPolyline([P(.28f, .58f), P(.5f, .38f), P(.72f, .58f)], color, w, true); break;
            case ChevronRight: target.DrawPolyline([P(.42f, .28f), P(.62f, .5f), P(.42f, .72f)], color, w, true); break;
            case ChevronLeft: target.DrawPolyline([P(.58f, .28f), P(.38f, .5f), P(.58f, .72f)], color, w, true); break;

            case Star:
                target.DrawColoredPolygon(StarPoints(P, .5f, .5f, .40f, .18f), color);
                break;

            case StarOutline:
                var outline = StarPoints(P, .5f, .52f, .40f, .18f);
                target.DrawPolyline([.. outline, outline[0]], color, w, true);
                break;

            case Trash:
                target.DrawLine(P(.16f, .26f), P(.84f, .26f), color, w, true);
                target.DrawLine(P(.38f, .26f), P(.38f, .14f), color, w, true);
                target.DrawLine(P(.62f, .26f), P(.62f, .14f), color, w, true);
                target.DrawLine(P(.38f, .14f), P(.62f, .14f), color, w, true);
                target.DrawPolyline([P(.26f, .30f), P(.30f, .86f), P(.70f, .86f), P(.74f, .30f)], color, w, true);
                break;

            case Filter:
                target.DrawLine(P(.14f, .24f), P(.86f, .24f), color, w, true);
                target.DrawLine(P(.26f, .48f), P(.74f, .48f), color, w, true);
                target.DrawLine(P(.38f, .72f), P(.62f, .72f), color, w, true);
                break;

            case More:
                for (int i = 0; i < 3; i++) target.DrawCircle(P(.22f + i * .28f, .5f), w * 1.15f, color);
                break;

            case MoreVertical:
                for (int i = 0; i < 3; i++) target.DrawCircle(P(.5f, .22f + i * .28f), w * 1.15f, color);
                break;

            case Play:
                target.DrawColoredPolygon([P(.30f, .16f), P(.84f, .5f), P(.30f, .84f)], color);
                break;

            case Pause:
                target.DrawRect(new Rect2(P(.28f, .18f), new Vector2(side * .14f, side * .64f)), color);
                target.DrawRect(new Rect2(P(.58f, .18f), new Vector2(side * .14f, side * .64f)), color);
                break;

            case SkipStart:
                target.DrawRect(new Rect2(P(.22f, .20f), new Vector2(side * .10f, side * .60f)), color);
                target.DrawColoredPolygon([P(.84f, .18f), P(.36f, .5f), P(.84f, .82f)], color);
                break;

            case SkipEnd:
                target.DrawRect(new Rect2(P(.68f, .20f), new Vector2(side * .10f, side * .60f)), color);
                target.DrawColoredPolygon([P(.16f, .18f), P(.64f, .5f), P(.16f, .82f)], color);
                break;

            case Repeat:
                target.DrawArc(P(.5f, .5f), side * .30f, Mathf.DegToRad(40), Mathf.DegToRad(320), 28, color, w, true);
                target.DrawColoredPolygon([P(.62f, .10f), P(.90f, .18f), P(.66f, .32f)], color);
                break;

            case Folder:
                target.DrawPolyline([P(.14f, .30f), P(.14f, .80f), P(.86f, .80f), P(.86f, .36f), P(.46f, .36f),
                    P(.38f, .24f), P(.14f, .24f), P(.14f, .30f)], color, w, true);
                break;

            case Tag:
                target.DrawPolyline([P(.16f, .24f), P(.52f, .24f), P(.84f, .56f), P(.56f, .84f), P(.16f, .44f),
                    P(.16f, .24f)], color, w, true);
                target.DrawCircle(P(.32f, .38f), w * .85f, color);
                break;

            case Layers:
                target.DrawPolyline([P(.5f, .14f), P(.86f, .34f), P(.5f, .54f), P(.14f, .34f), P(.5f, .14f)], color, w, true);
                target.DrawPolyline([P(.14f, .52f), P(.5f, .72f), P(.86f, .52f)], color, w, true);
                target.DrawPolyline([P(.14f, .68f), P(.5f, .88f), P(.86f, .68f)], color, w, true);
                break;

            case Film:
                target.DrawRect(new Rect2(P(.14f, .20f), new Vector2(side * .72f, side * .60f)), color, false, w);
                for (int i = 0; i < 3; i++)
                {
                    target.DrawLine(P(.08f, .28f + i * .20f), P(.14f, .28f + i * .20f), color, w);
                    target.DrawLine(P(.86f, .28f + i * .20f), P(.92f, .28f + i * .20f), color, w);
                }
                break;

            case Panel:
                target.DrawRect(new Rect2(P(.14f, .20f), new Vector2(side * .72f, side * .60f)), color, false, w);
                target.DrawLine(P(.40f, .20f), P(.40f, .80f), color, w);
                break;

            case Expand:
                target.DrawPolyline([P(.16f, .40f), P(.16f, .16f), P(.40f, .16f)], color, w, true);
                target.DrawPolyline([P(.84f, .40f), P(.84f, .16f), P(.60f, .16f)], color, w, true);
                target.DrawPolyline([P(.16f, .60f), P(.16f, .84f), P(.40f, .84f)], color, w, true);
                target.DrawPolyline([P(.84f, .60f), P(.84f, .84f), P(.60f, .84f)], color, w, true);
                break;

            case Close:
                target.DrawLine(P(.24f, .24f), P(.76f, .76f), color, w, true);
                target.DrawLine(P(.76f, .24f), P(.24f, .76f), color, w, true);
                break;

            case Check:
                target.DrawPolyline([P(.20f, .52f), P(.42f, .74f), P(.80f, .28f)], color, w, true);
                break;

            case Gear:
                target.DrawArc(P(.5f, .5f), side * .22f, 0, 360, 24, color, w, true);
                for (int i = 0; i < 6; i++)
                {
                    float angle = Mathf.DegToRad(i * 60f);
                    target.DrawLine(P(.5f + Mathf.Cos(angle) * .30f, .5f + Mathf.Sin(angle) * .30f),
                        P(.5f + Mathf.Cos(angle) * .42f, .5f + Mathf.Sin(angle) * .42f), color, w, true);
                }
                break;

            case Cube:
                target.DrawPolyline([P(.5f, .14f), P(.84f, .34f), P(.84f, .66f), P(.5f, .86f), P(.16f, .66f),
                    P(.16f, .34f), P(.5f, .14f)], color, w, true);
                target.DrawLine(P(.16f, .34f), P(.5f, .54f), color, w, true);
                target.DrawLine(P(.84f, .34f), P(.5f, .54f), color, w, true);
                target.DrawLine(P(.5f, .54f), P(.5f, .86f), color, w, true);
                break;

            case File:
                target.DrawPolyline([P(.30f, .14f), P(.62f, .14f), P(.80f, .32f), P(.80f, .86f), P(.30f, .86f),
                    P(.30f, .14f)], color, w, true);
                target.DrawLine(P(.60f, .14f), P(.60f, .32f), color, w);
                target.DrawLine(P(.60f, .32f), P(.80f, .32f), color, w);
                break;
        }
    }

    private static Vector2[] StarPoints(Func<float, float, Vector2> p, float cx, float cy, float outer, float inner)
    {
        var points = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float angle = Mathf.DegToRad(-90 + i * 36);
            float radius = i % 2 == 0 ? outer : inner;
            points[i] = p(cx + Mathf.Cos(angle) * radius, cy + Mathf.Sin(angle) * radius);
        }
        return points;
    }
}
